#ifndef RKNN_SHARP_GST_H
#define RKNN_SHARP_GST_H
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
typedef struct rkgst_capture rkgst_capture;
/* ABI v1. All offsets refer to the start of the corresponding borrowed fd.
 * fds may repeat. No fd is transferred. token owns the GstSample reference.
 * The caller must finish ALL hardware access before rkgst_release(). */
typedef struct {
    uint32_t struct_size, width, height, format; /* format = 0x3231564e (NV12) */
    /* 0 = unsupported/undeclared; 1=BT601 limited, 2=BT601 full, 3=BT709 limited.
     * Only explicit caps colorimetry is accepted as evidence; no size heuristic. */
    uint32_t n_planes, color_space;
    int32_t fd[2], stride[2];
    uint64_t offset[2], allocation_size[2];
    uint64_t pts_ns, dts_ns, running_time_ns, arrival_monotonic_ns;
    void *token;
} rkgst_frame;
/* Returns 0 success, -1 error. Does not block waiting for RTSP negotiation.
 * URL is never inserted into a pipeline description or returned in error text.
 * codec is exactly "h264" or "h265". RTSP uses TCP and bounded jitter/queues. */
int rkgst_open_rtsp(const char *url, const char *codec, uint32_t latency_ms,
                    rkgst_capture **out, char *error, uint32_t error_size);
/* Local MP4 stream (with timestamps): same MPP decoder and strict layout. */
int rkgst_open_file(const char *path, const char *codec, rkgst_capture **out,
                    char *error, uint32_t error_size);
/* Finite MP4 throughput benchmark: no clock pacing and no appsink drops.
 * Same decoder/layout checks as open_file. max-buffers=1 applies backpressure;
 * the sole producer must pull until EOS, drain consumers, release then close.
 * Preserves original PTS. Does not loop, resize or change the source codec. */
int rkgst_open_file_unpaced(const char *path, const char *codec,
                            rkgst_capture **out, char *error, uint32_t error_size);
/* Stop the sole producer before calling. Changes PLAYING to PAUSED and waits
 * up to 5 seconds for the state transition. SUCCESS and live NO_PREROLL both
 * succeed only with current=PAUSED and no pending state. Returns 0/-1.
 * Existing sample leases remain valid; this does not flush, unref or close.
 * No further pulls are allowed after success. Finish consumers, release every
 * sample, then close. On failure the state is uncertain: do not benchmark or
 * race hardware operations with cleanup. No resume entry point is provided. */
int rkgst_pause(rkgst_capture *capture, char *error, uint32_t error_size);
/* Single-consumer API; do not call concurrently with close. Zero initialize the
 * frame before its first use. Returns 1 frame, 0 timeout, 2 EOS, -1 error.
 * Timeout/EOS/error leave frame empty. Release the previous frame first. */
int rkgst_pull(rkgst_capture *capture, uint32_t timeout_ms, rkgst_frame *frame,
               char *error, uint32_t error_size);
void rkgst_release(rkgst_frame *frame);
/* Release all outstanding frames before close. RTSP TEARDOWN is bounded. */
void rkgst_close(rkgst_capture *capture);
#ifdef __cplusplus
}
#endif
#endif
