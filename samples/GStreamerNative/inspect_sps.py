"""Diagnostic for captured H.264 AVC caps, not a production H.264 parser.

Reads compressed SPS/PPS only; never decodes or maps image pixels. Restricted
to baseline/main/extended profiles with their default 4:2:0 chroma format.
"""
import json
import pathlib
import re
import sys


def inspect(caps_path, output_directory):
    caps = pathlib.Path(caps_path).read_text()
    match = re.search(r"codec_data=\(buffer\)([0-9a-fA-F]+)", caps)
    if not match:
        raise ValueError("AVC codec_data was not found")
    avcc = bytes.fromhex(match.group(1))
    if len(avcc) < 7 or avcc[0] != 1:
        raise ValueError("Invalid AVC configuration record")
    position = 6
    nals = []
    for count in (avcc[5] & 31,):
        for _ in range(count):
            size = int.from_bytes(avcc[position:position + 2], "big")
            position += 2
            nals.append(avcc[position:position + size])
            position += size
    count = avcc[position]
    position += 1
    for _ in range(count):
        size = int.from_bytes(avcc[position:position + 2], "big")
        position += 2
        nals.append(avcc[position:position + size])
        position += size
    sps = next(nal for nal in nals if nal and nal[0] & 31 == 7)
    rbsp = sps[1:].replace(b"\x00\x00\x03", b"\x00\x00")
    bits = "".join(format(value, "08b") for value in rbsp)
    bit_position = 0

    def read(count):
        nonlocal bit_position
        if bit_position + count > len(bits):
            raise ValueError("SPS bitstream ended early")
        value = int(bits[bit_position:bit_position + count], 2) if count else 0
        bit_position += count
        return value

    def ue():
        zeros = 0
        while read(1) == 0:
            zeros += 1
            if zeros > 31:
                raise ValueError("Invalid Exp-Golomb value")
        return (1 << zeros) - 1 + read(zeros)

    profile = read(8)
    read(8)  # constraint flags
    level = read(8)
    if profile not in (66, 77, 88):
        raise ValueError("Diagnostic supports baseline/main/extended SPS only")
    ue()  # seq_parameter_set_id
    ue()  # log2_max_frame_num_minus4
    poc = ue()
    if poc == 0:
        ue()
    elif poc == 1:
        read(1)
        ue()  # signed Exp-Golomb has the same codeword length
        ue()
        for _ in range(ue()):
            ue()
    ue()  # max_num_ref_frames
    read(1)
    width_mbs, height_map_units = ue() + 1, ue() + 1
    frame_only = read(1)
    if not frame_only:
        read(1)
    read(1)
    crop = [ue() for _ in range(4)] if read(1) else [0, 0, 0, 0]
    width = width_mbs * 16 - (crop[0] + crop[1]) * 2
    height = (2 - frame_only) * height_map_units * 16 - (crop[2] + crop[3]) * 2 * (2 - frame_only)
    vui = read(1)
    signal = full = description = matrix = None
    if vui:
        if read(1):
            aspect = read(8)
            if aspect == 255:
                read(16)
                read(16)
        if read(1):
            read(1)
        signal = read(1)
        if signal:
            read(3)
            full, description = read(1), read(1)
            if description:
                read(8)
                read(8)
                matrix = read(8)
    for key, value in (("width", width), ("height", height)):
        declared = re.search(r"\b" + key + r"=\(int\)(\d+)", caps)
        if declared and int(declared.group(1)) != value:
            raise ValueError("Derived dimensions disagree with captured caps")
    result = dict(profile_idc=profile, level_idc=level,
                  derived_width=width, derived_height=height,
                  vui_parameters_present_flag=vui,
                  video_signal_type_present_flag=signal,
                  video_full_range_flag=full,
                  colour_description_present_flag=description,
                  matrix_coefficients=matrix,
                  parsed_sps_only_no_pixel_decode=True)
    output = pathlib.Path(output_directory)
    output.mkdir(parents=True, exist_ok=True)
    (output / "camera-sps-pps.h264").write_bytes(b"".join(b"\x00\x00\x00\x01" + nal for nal in nals))
    text = json.dumps(result, indent=2) + "\n"
    (output / "camera-sps-vui.json").write_text(text)
    print(text, end="")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit("usage: inspect_sps.py CAPTURED_CAPS_TEXT OUTPUT_DIRECTORY")
    inspect(sys.argv[1], sys.argv[2])
