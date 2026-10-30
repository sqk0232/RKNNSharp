"""Read-only extraction of an RKNN model's embedded Python metadata.

This is evidence collection, not a supported replacement for rknn_query.
No RKNN/Python inference package is required. The model is never changed.
"""

import argparse
import ast
import hashlib
import json
from pathlib import Path


def extract(path: Path) -> dict:
    data = path.read_bytes()
    metadata = None
    for section in data.split(b"\0"):
        start = section.find(b"{'attrs':")
        if start < 0 or b"'quant_tab'" not in section:
            continue
        end = section.rfind(b"}")
        try:
            candidate = ast.literal_eval(section[start : end + 1].decode("utf-8"))
        except (UnicodeDecodeError, ValueError, SyntaxError):
            continue
        if isinstance(candidate, dict) and "attrs" in candidate:
            metadata = candidate
            break
    if metadata is None:
        raise ValueError("No recognized embedded attrs/quant_tab metadata; use RKNN query")
    return {
        "model": str(path.resolve()),
        "size": len(data),
        "sha256": hashlib.sha256(data).hexdigest(),
        "embedded_metadata": metadata,
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("model", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = json.dumps(extract(args.model), ensure_ascii=False, indent=2)
    if args.output:
        if args.output.resolve() == args.model.resolve():
            parser.error("output must differ from the model path")
        with args.output.open("x", encoding="utf-8") as stream:
            stream.write(result + "\n")
    else:
        print(result)
