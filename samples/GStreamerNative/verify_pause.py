"""Finite board-only retained-DMABUF pause check. No CPU pixel mappings.

Usage: verify_pause.py LIBGST_ADAPTER LIBRKS URI_FILE EVIDENCE_LOG
The URI stays inside this process and is never passed on a command line/log.
"""
import ctypes as c
import fcntl
import os
import pathlib
import sys
import time


class Frame(c.Structure):
    _fields_ = [(name, c.c_uint32) for name in ("size", "width", "height", "format", "planes", "color")]
    _fields_ += [("fd", c.c_int32 * 2), ("stride", c.c_int32 * 2), ("offset", c.c_uint64 * 2), ("allocation", c.c_uint64 * 2)]
    _fields_ += [(name, c.c_uint64) for name in ("pts", "dts", "running", "monotonic")]
    _fields_ += [("token", c.c_void_p)]


class Image(c.Structure):
    _fields_ = [("fd", c.c_int32)]
    _fields_ += [(name, c.c_uint32) for name in ("width", "height", "format", "stride", "height_stride")]
    _fields_ += [("size", c.c_uint64), ("offset", c.c_uint64), ("color", c.c_uint32), ("reserved", c.c_uint32)]


def verify(adapter, hardware, uri_file, log_path):
    os.environ["GST_DEBUG"] = "0"
    lib = c.CDLL(adapter)
    rks = c.CDLL(hardware)
    gst = c.CDLL("libgstreamer-1.0.so.0")
    lib.rkgst_open_rtsp.argtypes = [c.c_char_p, c.c_char_p, c.c_uint32, c.POINTER(c.c_void_p), c.c_char_p, c.c_uint32]
    lib.rkgst_pull.argtypes = [c.c_void_p, c.c_uint32, c.POINTER(Frame), c.c_char_p, c.c_uint32]
    lib.rkgst_pause.argtypes = [c.c_void_p, c.c_char_p, c.c_uint32]
    lib.rkgst_release.argtypes = [c.POINTER(Frame)]
    lib.rkgst_close.argtypes = [c.c_void_p]
    rks.rks_allocate_image.argtypes = [c.c_uint32, c.c_uint32, c.c_uint32, c.POINTER(c.c_void_p), c.POINTER(Image)]
    rks.rks_copy_frame.argtypes = [c.POINTER(Image), c.POINTER(Image)]
    rks.rks_release_image.argtypes = [c.c_void_p]
    gst.gst_bin_get_by_name.argtypes = [c.c_void_p, c.c_char_p]
    gst.gst_bin_get_by_name.restype = c.c_void_p
    gst.gst_element_get_static_pad.argtypes = [c.c_void_p, c.c_char_p]
    gst.gst_element_get_static_pad.restype = c.c_void_p
    gst.gst_element_get_state.argtypes = [c.c_void_p, c.POINTER(c.c_int), c.POINTER(c.c_int), c.c_uint64]
    callback_type = c.CFUNCTYPE(c.c_int, c.c_void_p, c.c_void_p, c.c_void_p)
    gst.gst_pad_add_probe.argtypes = [c.c_void_p, c.c_int, callback_type, c.c_void_p, c.c_void_p]
    gst.gst_pad_add_probe.restype = c.c_ulong
    gst.gst_pad_remove_probe.argtypes = [c.c_void_p, c.c_ulong]
    gst.gst_object_unref.argtypes = [c.c_void_p]
    frames, lines = [], []
    capture, owner = c.c_void_p(), c.c_void_p()
    decoder = pad = probe = None
    error = c.create_string_buffer(256)
    count = 0

    @callback_type
    def on_buffer(_pad, _info, _data):
        nonlocal count
        count += 1
        return 1  # GST_PAD_PROBE_OK

    try:
        assert c.sizeof(Frame) == 112
        assert lib.rkgst_pause(None, error, 256) == -1
        assert lib.rkgst_open_rtsp(pathlib.Path(uri_file).read_bytes().strip(), b"h264", 100, c.byref(capture), error, 256) == 0, error.value.decode()
        # Test-only inspection of the opaque capture's first field (pipeline).
        pipeline = c.cast(capture, c.POINTER(c.c_void_p))[0]
        decoder = gst.gst_bin_get_by_name(pipeline, b"mppvideodec0")
        assert decoder
        pad = gst.gst_element_get_static_pad(decoder, b"src")
        probe = gst.gst_pad_add_probe(pad, 1 << 4, on_buffer, None, None)
        for _ in range(6):
            frame = Frame()
            assert lib.rkgst_pull(capture, 5000, c.byref(frame), error, 256) == 1, error.value.decode()
            frames.append(frame)
        assert len({f.token for f in frames}) == 6
        assert len({f.fd[0] for f in frames}) == 6
        started = time.monotonic()
        assert lib.rkgst_pause(capture, error, 256) == 0, error.value.decode()
        elapsed = (time.monotonic() - started) * 1000
        current, pending = c.c_int(), c.c_int()
        state_result = gst.gst_element_get_state(pipeline, c.byref(current), c.byref(pending), 0)
        assert state_result in (1, 3) and current.value == 3 and pending.value == 0
        assert lib.rkgst_pause(capture, error, 256) == 0
        lines.append(f"PASS paused=true state_result={state_result} current=PAUSED pending=VOID elapsed_ms={elapsed:.3f} frame_abi=112")
        time.sleep(0.1)  # Allow packets already submitted before PAUSE to settle.
        before = count
        time.sleep(0.5)
        after = count
        assert before == after
        lines.append(f"PASS decoded_output_buffers_stable_500ms=true count_before={before} count_after={after}")
        empty = Frame()
        assert lib.rkgst_pull(capture, 0, c.byref(empty), error, 256) == -1 and empty.token is None
        first = frames[0]
        output = Image()
        assert rks.rks_allocate_image(first.width, first.height, 1, c.byref(owner), c.byref(output)) == 0
        output.color = 2  # Explicit BT601_FULL test policy; source matrix unknown.
        for frame in frames:
            assert frame.token and frame.fd[0] == frame.fd[1] and frame.offset[0] == 0
            assert frame.stride[0] == frame.stride[1] and frame.offset[1] % frame.stride[0] == 0
            fcntl.fcntl(frame.fd[0], fcntl.F_GETFD)
            source = Image(frame.fd[0], frame.width, frame.height, 1, frame.stride[0], frame.offset[1] // frame.stride[0], frame.allocation[0], 0, 2, 0)
            assert rks.rks_copy_frame(c.byref(source), c.byref(output)) == 0
        lines.append("PASS retained_samples=6 fd_valid=true RGA_hardware_copy_after_pause=6 no_cpu_pixel_map=true further_pull_rejected=true")
    finally:
        if owner.value:
            rks.rks_release_image(owner)
        if probe:
            gst.gst_pad_remove_probe(pad, probe)
        if pad:
            gst.gst_object_unref(pad)
        if decoder:
            gst.gst_object_unref(decoder)
        for frame in frames:
            lib.rkgst_release(c.byref(frame))
        if capture.value:
            lib.rkgst_close(capture)
    lines.append("PASS consumers_finished_then_release_all_samples_then_close=true")
    text = "\n".join(lines) + "\n"
    pathlib.Path(log_path).write_text(text)
    print(text, end="")


if __name__ == "__main__":
    if len(sys.argv) != 5:
        raise SystemExit(__doc__)
    verify(*sys.argv[1:])
