// Exercise the production C entry points and session gate with simulated vendor
// calls. No RGA/NPU/decoder hardware behavior or performance is inferred here.
#include "../src/rknnsharp.cpp"
#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <thread>
#include <unordered_map>

namespace {
std::mutex imports_gate;
struct ImportRecord { int fd; ino_t inode; };
std::unordered_map<rga_buffer_handle_t, ImportRecord> imports;
rga_buffer_handle_t next_handle = 1;
std::atomic<int> import_count{0};
thread_local ino_t expected_source = 0;
thread_local bool fail_run = false;
thread_local bool fail_import = false;
thread_local int last_import_fd = -1;
void verify(bool value, const char* message) {
    if (!value) { std::fprintf(stderr, "%s\n", message); std::abort(); }
}
void verify_source(const rga_buffer_t& source) {
    std::lock_guard<std::mutex> lock(imports_gate);
    auto found = imports.find(source.handle);
    verify(found != imports.end() && found->second.inode == expected_source,
        "production run used stale or another caller's source");
}
struct Fixture {
    struct Storage {
        int fd = memfd_create("source-cache-regression", MFD_CLOEXEC);
        ~Storage() { if (fd >= 0) close(fd); }
    } storage;
    rknn_tensor_mem input{};
    rks_session session;
    rks_frame frame{};
    Fixture() {
        int fd = storage.fd;
        verify(fd >= 0 && ftruncate(fd, 4096) == 0, "fixture allocation failed");
        frame = {fd, 16, 16, RKS_RGB888, 48, 16, 768, 0, 0, 0};
        session.w = session.h = session.stride = 16;
        session.signed_input = &input;
        session.rgb_rga = std::make_unique<Imported>(fd, 768, 16, 16, 16, 16, RK_FORMAT_RGB_888);
        session.signed_rga = std::make_unique<Imported>(fd, 768, 16, 16, 16, 16, RK_FORMAT_RGB_888);
        session.attrs.resize(1);
        session.attrs[0].n_elems = session.attrs[0].size = 1;
        session.attrs[0].fmt = RKNN_TENSOR_NCHW;
        session.attrs[0].type = RKNN_TENSOR_INT8;
        session.attrs[0].qnt_type = RKNN_TENSOR_QNT_AFFINE_ASYMMETRIC;
        session.attrs[0].scale = 1;
        session.values = {{0}};
        session.quantized_values = {{0}};
        session.supports_int8 = true;
    }
    void select_source() {
        struct stat allocation{};
        verify(fstat(frame.fd, &allocation) == 0, "fixture stat failed");
        expected_source = allocation.st_ino;
    }
    // Member order destroys the real session (and all imports) before storage.
};
}
extern "C" int __wrap_fstatfs(int, struct statfs* value) {
    *value = {};
    value->f_type = DMA_BUF_MAGIC;
    return 0;
}
rga_buffer_handle_t importbuffer_fd(int fd, int) {
    last_import_fd = fd;
    if (fail_import) return 0;
    struct stat allocation{};
    verify(fstat(fd, &allocation) == 0, "vendor import received closed fd");
    std::lock_guard<std::mutex> lock(imports_gate);
    auto handle = next_handle++;
    imports.emplace(handle, ImportRecord{fd, allocation.st_ino});
    ++import_count;
    return handle;
}
IM_STATUS releasebuffer_handle(rga_buffer_handle_t handle) {
    std::lock_guard<std::mutex> lock(imports_gate);
    auto found = imports.find(handle);
    verify(found != imports.end() && fcntl(found->second.fd, F_GETFD) >= 0,
        "vendor release ran after fd close or twice");
    imports.erase(found);
    return IM_STATUS_SUCCESS;
}
rga_buffer_t wrapbuffer_handle(rga_buffer_handle_t handle, int w, int h, int fmt, int ws, int hs) {
    rga_buffer_t buffer{};
    buffer.handle = handle;
    buffer.width = w;
    buffer.height = h;
    buffer.wstride = ws;
    buffer.hstride = hs;
    buffer.format = fmt;
    return buffer;
}
rga_buffer_t wrapbuffer_handle(rga_buffer_handle_t handle, int w, int h, int fmt) {
    return wrapbuffer_handle(handle, w, h, fmt, w, h);
}
const char* imStrError_t(IM_STATUS) { return "injected vendor error"; }
IM_STATUS imcheck_t(rga_buffer_t src, rga_buffer_t, rga_buffer_t, im_rect, im_rect, im_rect, int) {
    verify_source(src);
    return IM_STATUS_SUCCESS;
}
im_job_handle_t imbeginJob(uint64_t) { return 0; }
IM_STATUS imendJob(im_job_handle_t, int, int, int*) { return IM_STATUS_SUCCESS; }
IM_STATUS imcancelJob(im_job_handle_t) { return IM_STATUS_SUCCESS; }
IM_STATUS imfillTask(im_job_handle_t, rga_buffer_t, im_rect, uint32_t) { return IM_STATUS_SUCCESS; }
IM_STATUS improcessTask(im_job_handle_t, rga_buffer_t, rga_buffer_t, rga_buffer_t,
    im_rect, im_rect, im_rect, im_opt_t*, int) { return IM_STATUS_SUCCESS; }
IM_STATUS improcess(rga_buffer_t src, rga_buffer_t, rga_buffer_t, im_rect, im_rect, im_rect,
    int, int*, im_opt_t*, int) { verify_source(src); return IM_STATUS_SUCCESS; }
IM_STATUS imfill(rga_buffer_t, im_rect, int, int, int*) { return IM_STATUS_SUCCESS; }
IM_STATUS imrop(rga_buffer_t, rga_buffer_t, int, int, int*) { return IM_STATUS_SUCCESS; }
int rknn_run(rknn_context, rknn_run_extend*) { return fail_run ? -1 : 0; }
int rknn_outputs_get(rknn_context, uint32_t count, rknn_output* output, rknn_output_extend*) {
    for (uint32_t i = 0; i < count; ++i) std::memset(output[i].buf, 0, output[i].size);
    return 0;
}
int rknn_outputs_release(rknn_context, uint32_t, rknn_output*) { return 0; }
int rknn_destroy(rknn_context) { return 0; }
int rknn_destroy_mem(rknn_context, rknn_tensor_mem*) { return 0; }

int main() {
    {
        Fixture fixture;
        fixture.select_source();
        rks_run_info info{};
        verify(rks_run(&fixture.session, &fixture.frame, &info) == 0, "Run failed");
        int baseline = import_count;
        verify(rks_run_int8(&fixture.session, &fixture.frame, &info) == 0, "INT8 entry failed");
        float output = -1;
        float* outputs[] = {&output};
        uint32_t counts[] = {1};
        verify(rks_run_float_into(&fixture.session, &fixture.frame, outputs, counts, 1, &info) == 0,
            "RunInto entry failed");
        verify(import_count == baseline, "normal entry points did not share source cache");
        fail_run = true;
        verify(rks_run(&fixture.session, &fixture.frame, &info) != 0, "injected run failure accepted");
        verify(!fixture.session.has_output && fixture.session.output_encoding == 0,
            "failed run published outputs");
        fail_run = false;
        verify(rks_run(&fixture.session, &fixture.frame, &info) == 0 && import_count == baseline + 1,
            "failed run retained import or prevented retry");
        auto invalid = fixture.frame;
        invalid.row_stride_bytes = 1;
        verify(rks_run(&fixture.session, &invalid, &info) != 0, "invalid layout accepted");
        baseline = import_count;
        verify(rks_run(&fixture.session, &fixture.frame, &info) == 0 && import_count == baseline + 1,
            "validation failure retained import or prevented retry");
        baseline = import_count;
        verify(rks_run(&fixture.session, nullptr, &info) != 0, "null frame accepted");
        verify(rks_run(&fixture.session, &fixture.frame, &info) == 0 && import_count == baseline + 1,
            "null argument failure retained import or prevented retry");
        auto changed = fixture.frame;
        changed.width = 14;
        fail_import = true;
        verify(rks_run(&fixture.session, &changed, &info) != 0 &&
            std::strstr(rks_last_error(), "RGA importbuffer_fd failed"), "failed vendor import accepted");
        verify(last_import_fd >= 0 && fcntl(last_import_fd, F_GETFD) == -1,
            "failed vendor import leaked allocation pin");
        fail_import = false;
        baseline = import_count;
        verify(rks_run(&fixture.session, &fixture.frame, &info) == 0 && import_count == baseline + 1,
            "vendor import failure poisoned retry");
        baseline = import_count;
        auto concurrent = [&] {
            fixture.select_source();
            for (int i = 0; i < 100; ++i) {
                rks_run_info timing{};
                verify(rks_run(&fixture.session, &fixture.frame, &timing) == 0, "concurrent Run failed");
            }
        };
        std::thread a(concurrent), b(concurrent);
        a.join();
        b.join();
        verify(import_count == baseline, "same-session concurrent calls lost cache hits");
        Fixture second;
        auto independent = [&] {
            second.select_source();
            for (int i = 0; i < 100; ++i) {
                rks_run_info timing{};
                verify(rks_run(&second.session, &second.frame, &timing) == 0, "independent session Run failed");
            }
        };
        std::thread c(concurrent), d(independent);
        c.join();
        d.join();
    }
    verify(imports.empty(), "session destruction leaked vendor imports");
    std::puts("source import cache production-entry regression passed: Run/RunInto/INT8 shared hits, "
        "failed run/import invalidation/retry/output state, invalid/null layout, 400 concurrent calls, session disposal");
}
