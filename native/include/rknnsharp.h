#ifndef RKNN_SHARP_H
#define RKNN_SHARP_H
#include <stdint.h>
#if defined(__GNUC__)
#define RKS_API __attribute__((visibility("default")))
#else
#define RKS_API
#endif
#ifdef __cplusplus
extern "C" {
#endif
typedef struct rks_session rks_session;
typedef struct rks_image rks_image;
enum { RKS_NV12=1, RKS_RGB888=2, RKS_RGBA8888=3 };
enum { RKS_BT601_LIMIT=1, RKS_BT601_FULL=2, RKS_BT709_LIMIT=3 };
/* All integer fields have natural alignment. Only linear single-fd storage is
   accepted. offset=0. NV12 UV starts at row_stride_bytes*height_stride.
   fd is borrowed; producer must finish before entry, and retain ownership and
   allocation until return. Never call concurrently with release of fd/sample.
   No CPU image mapping is performed. All RGA and NPU operations are synchronous. */
typedef struct {
    int32_t fd;
    uint32_t width, height, format, row_stride_bytes, height_stride;
    uint64_t size_bytes, offset_bytes;
    uint32_t color_space, reserved;
} rks_frame;
typedef struct {
    uint32_t index, n_dims, dims[16], element_count;
    int32_t layout, data_type, quantization, zero_point;
    float scale;
    uint32_t size_bytes, width_stride, size_with_stride;
    char name[256];
} rks_tensor;
typedef struct { int32_t x,y,width,height; } rks_rect;
typedef struct {
    float scale_x,scale_y;
    int32_t pad_left,pad_top,resize_width,resize_height;
    double preprocess_ms,inference_ms,outputs_ms;
    int32_t input_dma_fd;
    uint32_t input_stride_bytes;
} rks_run_info;
/* Status 0=success, -1=invalid/unsupported/hardware failure; details in
   rks_last_error (thread local, valid until next failing call on this thread).
   Create supports static NHWC RGB INT8 with zp=-128/scale=1/255 only.
   No normalization, color-order assumptions or CPU fallback are hidden. */
RKS_API const char* rks_last_error(void);
RKS_API uint32_t rks_abi_version(void);
/* Optional additive features; old ABI-2 libraries may omit this export.
   Bit 0 indicates rks_run_float_into with the complete contract below. */
RKS_API uint32_t rks_capabilities(void);
RKS_API int rks_create(const char* model_path, rks_session** session);
/* Caller serializes destroy with every API call; it must never race new calls.
   Session operations serialize internally. Different sessions are independent. */
RKS_API void rks_destroy(rks_session* session);
RKS_API int rks_get_tensor_count(rks_session*, uint32_t* inputs, uint32_t* outputs);
RKS_API int rks_get_tensor(rks_session*, uint32_t is_output, uint32_t index, rks_tensor*);
RKS_API int rks_get_versions(rks_session*, char* utf8, uint32_t capacity);
/* Explicit context policy, supported masks 0(auto),1,2,4,3,7. Mask combinations
   do not promise acceleration for a particular compiled model. */
RKS_API int rks_set_core_mask(rks_session*, uint32_t mask);
enum { RKS_FLOAT_OUTPUT_SDK_EXACT=0, RKS_FLOAT_OUTPUT_PREFER_FAST_INT8=1 };
/* Optional additive ABI-2 export. Default is SDK_EXACT. PREFER_FAST_INT8
   converts eligible standard NCHW affine INT8 heads with IEEE float
   (q-zero_point)*scale; other heads retain SDK float conversion. Float values
   can differ from SDK rounding; no universal absolute tolerance is promised.
   Changes only future Run/RunInto calls; raw INT8/Detect is unaffected.
   Rejects unknown modes without changing the session or retained outputs. */
RKS_API int rks_set_float_output_mode(rks_session*, uint32_t mode);
/* Letterbox requires RK3588 RGA3 hardware, explicit bilinear resize and RGB
   padding 114. Both verified RGA3 cores may schedule each operation. No RGA3
   support or incompatible geometry fails, without RGA2/CPU resize fallback.
   RGB channels
   convert to signed bytes by hardware ROP XOR128; native input pass_through=1.
   Outputs are float tensors under the selected mode; CPU may postprocess them.
   A successful run replaces previous output state; a failed run invalidates it. */
RKS_API int rks_run(rks_session*, const rks_frame*, rks_run_info*);
/* Synchronous float snapshot into caller-owned, distinct output buffers.
   output_count and each count must exactly match the queried output tensors.
   Buffers remain exclusively writable/alive (and pinned if managed) until
   return; no pointer is retained. Success publishes only these buffers, not
   an internal cache readable with rks_copy_output. Every attempted call
   invalidates previous native output state and borrowed INT8 generations.
   On failure buffers may be partially written: discard every output. */
RKS_API int rks_run_float_into(rks_session*, const rks_frame*,
                              float* const* outputs, const uint32_t* counts,
                              uint32_t output_count, rks_run_info*);
RKS_API int rks_copy_output(rks_session*, uint32_t index, float* values, uint32_t count);
/* Raw path supports standard OUTPUT_ATTR contiguous NCHW affine INT8 tensors.
   It preserves the output quantization metadata and avoids float conversion.
   Borrowed pointers are valid only until the next attempted run or destroy.
   Caller must hold its session-level exclusive lock through the complete
   consumer operation; no pointer/Span may escape that scope. Native call-level
   locks do not by themselves protect a borrowed pointer after return.
   Generation increases on every attempted run, including failed runs. */
RKS_API int rks_run_int8(rks_session*, const rks_frame*, rks_run_info*);
RKS_API int rks_borrow_output_int8(rks_session*, uint32_t index,
                                   const int8_t** values, uint32_t* count, uint64_t* generation);
/* In-place hardware drawing. Caller grants exclusive writable buffer ownership
   during call; decoder-owned read-only/reused frames are unsuitable. If border
   thickness covers a small box, RGA fills it. RGB boxes with a 1-pixel extent
   become a nearest in-bounds 2x2 (or 2xN/Nx2) hardware marker. Small fills whose
   resulting origin is exactly 1 extend left/up to 0 to satisfy this BSP.
   NV12 boxes require
   even coordinates and extents. The color is RGB; on NV12 it is converted
   with the frame's explicit color space. Other hardware limits fail explicitly. */
RKS_API int rks_draw_rectangles(const rks_frame*, const rks_rect*, uint32_t count, uint32_t rgba, int32_t thickness);
/* Synchronous hardware copy/CSC into caller-owned DMA buffer. No resize.
   Format conversion uses the verified RGA3 cores. Same-format copies do not
   request a core. */
RKS_API int rks_copy_frame(const rks_frame* source, const rks_frame* destination);
/* Allocates uncacheable DMA32 storage, never maps pixels to CPU. out_frame.fd
   is borrowed from owner. Caller must serialize release with all frame uses,
   including APIs that only receive the copied descriptor. */
RKS_API int rks_allocate_image(uint32_t width, uint32_t height, uint32_t format,
                               rks_image** owner, rks_frame* out_frame);
RKS_API void rks_release_image(rks_image* owner);
#ifdef __cplusplus
}
#endif
#endif
