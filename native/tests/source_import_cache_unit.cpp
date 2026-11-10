#include "source_import_cache.h"
#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <mutex>
#include <sys/mman.h>
#include <thread>
#include <vector>

// Only filesystem recognition and dup failure are injected. Identity, fd reuse,
// pin lifetime, close-on-exec and allocation lengths use real Linux syscalls.
static std::atomic<bool> known_dma_buf{true};
static std::atomic<bool> fail_dup{false};
extern "C" int __wrap_fstatfs(int, struct statfs* value) {
    *value = {};
    value->f_type = known_dma_buf ? DMA_BUF_MAGIC : 0;
    return 0;
}
extern "C" int __real_fcntl(int, int, ...);
extern "C" int __wrap_fcntl(int fd, int command, ...) {
    if (command == F_GETFD) return __real_fcntl(fd, command);
    va_list arguments;
    va_start(arguments, command);
    int value = va_arg(arguments, int);
    va_end(arguments);
    if (command == F_DUPFD_CLOEXEC && fail_dup) return -1;
    return __real_fcntl(fd, command, value);
}
static void require(bool value, const char* message) {
    if (!value) { std::fprintf(stderr, "%s\n", message); std::abort(); }
}
struct MockImported {
    static inline int created = 0, released = 0, live = 0;
    int fd;
    ino_t inode;
    explicit MockImported(int source) : fd(source) {
        struct stat allocation{};
        require(fstat(fd, &allocation) == 0, "import received closed fd");
        inode = allocation.st_ino;
        ++created;
        ++live;
    }
    ~MockImported() {
        require(fcntl(fd, F_GETFD) >= 0, "allocation pin closed before import release");
        ++released;
        --live;
    }
};
using Cache = rks_detail::SourceImportCache<MockImported>;
struct Buffer {
    int fd;
    rks_frame frame{};
    explicit Buffer(uint64_t bytes = 4096) {
        fd = memfd_create("source-cache-unit", MFD_CLOEXEC);
        require(fd >= 0 && ftruncate(fd, bytes) == 0, "fixture allocation failed");
        frame = {fd, 16, 16, RKS_RGB888, 48, 16, 768, 0, 0, 0};
    }
    ~Buffer() { if (fd >= 0) close(fd); }
    Buffer(const Buffer&) = delete;
};
static Cache::Lease acquire(Cache& cache, const rks_frame& frame) {
    return cache.acquire(frame, [](int fd) { return std::make_unique<MockImported>(fd); });
}
static void hit_and_fd_reuse() {
    Cache cache;
    Buffer first;
    auto a = acquire(cache, first.frame);
    int pin = a->retained_fd;
    require(pin >= 0 && (fcntl(pin, F_GETFD) & FD_CLOEXEC), "pin lacks close-on-exec");
    require(acquire(cache, first.frame) == a, "same source missed cache");
    auto alias = first.frame;
    alias.fd = dup(first.fd);
    require(alias.fd >= 0 && acquire(cache, alias) == a, "dup alias missed allocation identity");
    close(alias.fd);
    Buffer second;
    require(dup2(second.fd, first.fd) == first.fd, "fd reuse fixture failed");
    auto b = acquire(cache, first.frame);
    require(a != b && a->imported->inode != b->imported->inode, "reused fd selected stale import");
    // Decoder/image disposal drops its own fd. A bounded cache pin survives it.
    close(first.fd);
    first.fd = -1;
    require(fcntl(pin, F_GETFD) >= 0, "producer close invalidated cached allocation pin");
    a.reset();
    b.reset();
    cache.clear();
    require(fcntl(pin, F_GETFD) == -1, "clear leaked allocation pin");
}
static void layout_and_length_invalidation() {
    Cache cache;
    Buffer buffer;
    auto previous = acquire(cache, buffer.frame);
    // Each descriptor field affecting the imported view must invalidate it.
    std::vector<rks_frame> layouts(8, buffer.frame);
    layouts[0].width = 14;
    layouts[1].height = 14;
    layouts[2].row_stride_bytes = 60;
    layouts[3].height_stride = 18;
    layouts[4].format = RKS_RGBA8888;
    layouts[5].color_space = RKS_BT709_LIMIT;
    layouts[6].size_bytes = 2048;
    layouts[7].offset_bytes = 1; // Validation rejects this before production acquire.
    for (const auto& frame : layouts) {
        auto current = acquire(cache, frame);
        require(current != previous, "changed layout reused stale imported view");
        previous = current;
        require(acquire(cache, frame) == current, "updated layout did not hit");
    }
    require(ftruncate(buffer.fd, 8192) == 0, "resize fixture failed");
    auto resized = acquire(cache, layouts.back());
    require(resized != previous, "actual allocation length change reused stale import");
    previous.reset();
    resized.reset();
    cache.clear();
    require(MockImported::live == 0, "metadata replacement leaked old entries");
}
static void capacity_and_lru() {
    Cache cache;
    std::vector<std::unique_ptr<Buffer>> buffers;
    for (size_t i = 0; i < Cache::capacity; ++i) {
        buffers.push_back(std::make_unique<Buffer>());
        acquire(cache, buffers.back()->frame);
    }
    require(MockImported::live == int(Cache::capacity), "cache capacity fill mismatch");
    const int imported = MockImported::created;
    acquire(cache, buffers.front()->frame); // Touch oldest, making index 1 LRU.
    Buffer extra;
    acquire(cache, extra.frame);
    require(MockImported::live == int(Cache::capacity), "entry limit exceeded");
    acquire(cache, buffers.front()->frame);
    require(MockImported::created == imported + 1, "LRU evicted recently used source");
    acquire(cache, buffers[1]->frame);
    require(MockImported::created == imported + 2, "LRU retained oldest source");
}
static void bytes_and_temporary_paths() {
    Cache cache;
    Buffer a(Cache::byte_limit / 2), b(Cache::byte_limit / 2), c(Cache::byte_limit / 2);
    acquire(cache, a.frame);
    acquire(cache, b.frame);
    acquire(cache, c.frame);
    require(MockImported::live == 2, "actual allocation bytes did not bound residency");
    cache.clear();
    Buffer large(Cache::byte_limit + 4096);
    auto temporary = acquire(cache, large.frame);
    require(temporary->retained_fd == -1, "oversize allocation was pinned");
    temporary.reset();
    require(MockImported::live == 0, "oversize import was cached");
    known_dma_buf = false;
    temporary = acquire(cache, a.frame);
    require(temporary->retained_fd == -1, "unknown filesystem was pinned");
    temporary.reset();
    known_dma_buf = true;
    fail_dup = true;
    temporary = acquire(cache, a.frame);
    require(temporary->retained_fd == -1, "failed dup poisoned temporary import");
    temporary.reset();
    fail_dup = false;
    require(MockImported::live == 0, "temporary paths retained imports");
    temporary = acquire(cache, a.frame);
    require(temporary->retained_fd >= 0, "dup recovery did not resume caching");
}
static void failures_and_retry() {
    Cache cache;
    Buffer buffer;
    int pin = -1;
    bool failed = false;
    try {
        cache.acquire(buffer.frame, [&](int fd) -> std::unique_ptr<MockImported> {
            pin = fd;
            throw std::runtime_error("injected import failure");
        });
    } catch (const std::runtime_error&) { failed = true; }
    require(failed && pin >= 0 && fcntl(pin, F_GETFD) == -1, "failed import leaked pin");
    auto retry = acquire(cache, buffer.frame);
    require(retry->retained_fd >= 0, "import failure poisoned retry");
    retry.reset();
    auto invalid = buffer.frame;
    invalid.size_bytes = 8192;
    failed = false;
    try { acquire(cache, invalid); } catch (const std::runtime_error&) { failed = true; }
    require(failed, "invalid declared length accepted");
    invalid.fd = -1;
    failed = false;
    try { acquire(cache, invalid); } catch (const std::runtime_error&) { failed = true; }
    require(failed, "invalid fd accepted");
    // Same scope/order as run_encoded: unwind lease and work before clearing.
    try {
        auto source = acquire(cache, buffer.frame);
        throw std::runtime_error("injected synchronous hardware failure");
    } catch (const std::runtime_error&) { cache.clear(); }
    require(MockImported::live == 0, "hardware failure retained stale import");
    acquire(cache, buffer.frame);
}
static void concurrent_sessions() {
    Buffer a, b;
    Cache shared;
    std::mutex session_gate;
    std::atomic<int> completed{0};
    auto run = [&](const rks_frame& frame) {
        for (int i = 0; i < 500; ++i) {
            std::lock_guard<std::mutex> lock(session_gate);
            auto source = acquire(shared, frame);
            struct stat allocation{};
            require(fstat(frame.fd, &allocation) == 0 && source->imported->inode == allocation.st_ino,
                "concurrent caller used another source");
            ++completed;
        }
    };
    std::thread first(run, std::cref(a.frame)), second(run, std::cref(b.frame));
    first.join();
    second.join();
    require(completed == 1000, "concurrent calls incomplete");
    require(MockImported::live == 2, "serialized cache failed to reuse two sources");
    Cache independent;
    auto other = acquire(independent, a.frame);
    require(other != acquire(shared, a.frame), "independent sessions shared cache entries");
}
int main() {
    hit_and_fd_reuse();
    layout_and_length_invalidation();
    capacity_and_lru();
    require(MockImported::live == 0, "session destruction leaked imports");
    bytes_and_temporary_paths();
    failures_and_retry();
    concurrent_sessions();
    require(MockImported::live == 0 && MockImported::created == MockImported::released,
        "final imports/pins unbalanced");
    std::printf("source import cache unit passed: hits, fd reuse, dup alias, layout/length invalidation, "
        "LRU/count/bytes, temporary paths, close-on-exec, failures/retry, release order, "
        "1000 gated concurrent calls, independent sessions; imports=%d releases=%d\n",
        MockImported::created, MockImported::released);
}
