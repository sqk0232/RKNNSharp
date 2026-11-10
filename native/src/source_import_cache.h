#ifndef RKS_SOURCE_IMPORT_CACHE_H
#define RKS_SOURCE_IMPORT_CACHE_H
#include "rknnsharp.h"
#include <cstdint>
#include <fcntl.h>
#include <linux/magic.h>
#include <list>
#include <memory>
#include <stdexcept>
#include <sys/stat.h>
#include <sys/vfs.h>
#include <unistd.h>

namespace rks_detail {
// Session-private. The session gate must cover acquire, synchronous hardware
// access, lease destruction and clear. No producer sample/pixel pointer is held.
template<class Imported> class SourceImportCache {
public:
    static constexpr size_t capacity = 8;
    static constexpr uint64_t byte_limit = 64ull * 1024 * 1024;
    struct Entry {
        rks_frame frame{};
        struct stat allocation{};
        int retained_fd = -1;
        std::unique_ptr<Imported> imported;
        ~Entry() {
            imported.reset(); // Detach RGA before dropping the allocation pin.
            if (retained_fd >= 0) close(retained_fd);
        }
    };
    using Lease = std::shared_ptr<Entry>;

    template<class Factory> Lease acquire(const rks_frame& frame, Factory factory) {
        struct stat allocation{};
        if (fstat(frame.fd, &allocation) != 0 || allocation.st_size <= 0)
            throw std::runtime_error("DMA fd has no verifiable allocation length");
        if (frame.size_bytes > uint64_t(allocation.st_size))
            throw std::runtime_error("declared DMA size exceeds actual fd allocation");

        // Linux DMA-BUF has a dedicated filesystem and per-allocation inode.
        // Older/unknown exporters keep the original temporary import behavior.
        struct statfs filesystem{};
        bool cacheable = allocation.st_ino != 0 &&
            uint64_t(allocation.st_size) <= byte_limit &&
            fstatfs(frame.fd, &filesystem) == 0 && filesystem.f_type == DMA_BUF_MAGIC;
        if (cacheable) {
            for (auto it = entries_.begin(); it != entries_.end(); ++it) {
                const auto& entry = *it;
                if (entry->allocation.st_dev != allocation.st_dev || entry->allocation.st_ino != allocation.st_ino) continue;
                if (same_allocation(entry->allocation, allocation) && same_layout(entry->frame, frame)) {
                    auto lease = entry;
                    entries_.splice(entries_.begin(), entries_, it);
                    return lease;
                }
                // A new layout replaces the old view of this allocation.
                resident_bytes_ -= uint64_t(entry->allocation.st_size);
                entries_.erase(it);
                break;
            }
            // Evict before importing the replacement, bounding retained RGA
            // handles and allocation pins even while a cache miss is prepared.
            const auto bytes = uint64_t(allocation.st_size);
            while (!entries_.empty() &&
                   (entries_.size() >= capacity || resident_bytes_ > byte_limit - bytes)) {
                resident_bytes_ -= uint64_t(entries_.back()->allocation.st_size);
                entries_.pop_back();
            }
        }

        auto entry = std::make_shared<Entry>();
        entry->frame = frame;
        entry->allocation = allocation;
        if (cacheable) {
            entry->retained_fd = fcntl(frame.fd, F_DUPFD_CLOEXEC, 0);
            // FD pressure must not turn a previously usable frame into failure.
            cacheable = entry->retained_fd >= 0;
            if (cacheable) {
                struct stat pinned{};
                if (fstat(entry->retained_fd, &pinned) != 0 || !same_allocation(allocation, pinned))
                    throw std::runtime_error("DMA allocation changed while retaining source fd");
            }
        }
        entry->imported = factory(cacheable ? entry->retained_fd : frame.fd);
        if (!cacheable) return entry;

        entries_.push_front(entry);
        resident_bytes_ += uint64_t(allocation.st_size);
        return entry;
    }

    void clear() noexcept { entries_.clear(); resident_bytes_ = 0; }

private:
    static bool same_allocation(const struct stat& a, const struct stat& b) {
        return a.st_dev == b.st_dev && a.st_ino == b.st_ino && a.st_size == b.st_size;
    }
    static bool same_layout(const rks_frame& a, const rks_frame& b) {
        return a.width == b.width && a.height == b.height && a.format == b.format &&
            a.row_stride_bytes == b.row_stride_bytes && a.height_stride == b.height_stride &&
            a.size_bytes == b.size_bytes && a.offset_bytes == b.offset_bytes &&
            a.color_space == b.color_space && a.reserved == b.reserved;
    }
    std::list<Lease> entries_;
    uint64_t resident_bytes_ = 0;
};
}
#endif
