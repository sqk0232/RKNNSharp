"""Finite H.264 RTSP remux fixture. No decoder/encoder or CPU pixel access.

Usage: capture_compressed.py URI_FILE OUTPUT_DIRECTORY
The URI is read only on the device and never printed or passed to a subprocess.
"""
import hashlib
import json
import os
import pathlib
import subprocess
import sys
import time

os.environ["GST_DEBUG"] = "0"
import gi
gi.require_version("Gst", "1.0")
from gi.repository import Gst


def capture(uri_file, output_directory):
    os.umask(0o077)
    root = pathlib.Path(output_directory)
    root.mkdir(parents=True, exist_ok=True)
    partial = root / "camera-2k-h264.partial.mp4"
    final = root / "camera-2k-h264.mp4"
    if partial.exists() or final.exists():
        raise RuntimeError("Capture output already exists; refusing overwrite")
    Gst.init(None)
    pipeline = Gst.parse_launch(
        "rtspsrc name=source protocols=tcp latency=100 drop-on-latency=false "
        "tcp-timeout=5000000 teardown-timeout=1000000000 "
        "! application/x-rtp,media=video,encoding-name=H264 "
        "! rtph264depay ! h264parse "
        "! video/x-h264,stream-format=avc,alignment=au "
        "! identity name=limit eos-after=601 "
        "! mp4mux ! filesink name=output sync=false"
    )
    assert pipeline.get_by_name("limit").find_property("eos-after") is not None
    pipeline.get_by_name("source").set_property("location", pathlib.Path(uri_file).read_text().strip())
    pipeline.get_by_name("output").set_property("location", str(partial))
    started = time.monotonic()
    try:
        if pipeline.set_state(Gst.State.PLAYING) == Gst.StateChangeReturn.FAILURE:
            raise RuntimeError("Compressed capture failed to start")
        message = pipeline.get_bus().timed_pop_filtered(20 * Gst.SECOND, Gst.MessageType.EOS | Gst.MessageType.ERROR)
        if message is None:
            raise RuntimeError("Compressed capture timed out without EOS")
        if message.type == Gst.MessageType.ERROR:
            error, _debug = message.parse_error()
            raise RuntimeError("Compressed capture failed: source=" + message.src.get_name() + " code=" + str(error.code))
        elapsed = time.monotonic() - started
    finally:
        pipeline.set_state(Gst.State.NULL)
    probe = subprocess.run(
        ["ffprobe", "-v", "error", "-skip_frame", "all", "-count_packets", "-show_entries",
         "stream=codec_name,width,height,nb_frames,nb_read_packets,pix_fmt,color_range,color_space,avg_frame_rate,time_base,duration:format=duration,size",
         "-of", "json", str(partial)], capture_output=True, text=True, timeout=5, check=True)
    metadata = json.loads(probe.stdout)
    streams = metadata.get("streams", [])
    assert len(streams) == 1 and streams[0]["codec_name"] == "h264"
    assert streams[0]["width"] == 2560 and streams[0]["height"] == 1440
    assert int(streams[0]["nb_read_packets"]) >= 500
    packet_probe = subprocess.run(
        ["ffprobe", "-v", "error", "-skip_frame", "all", "-read_intervals", "%+#1", "-show_packets",
         "-show_entries", "packet=flags,pts_time,dts_time", "-of", "json", str(partial)],
        capture_output=True, text=True, timeout=5, check=True)
    first_packet = json.loads(packet_probe.stdout)["packets"][0]
    assert "K" in first_packet["flags"], "Fixture must start on a keyframe"
    partial.replace(final)
    sha256 = hashlib.sha256(final.read_bytes()).hexdigest()  # Compressed bytes only.
    report = dict(eos_received=True, capture_seconds=elapsed, video_file=str(final),
                  sha256=sha256, metadata=metadata, first_packet=first_packet,
                  no_decode_or_encode=True, no_cpu_pixel_access=True)
    text = json.dumps(report, indent=2) + "\n"
    (root / "camera-2k-metadata.json").write_text(text)
    print(text, end="")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    capture(*sys.argv[1:])
