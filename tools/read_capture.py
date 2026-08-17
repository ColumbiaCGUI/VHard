#!/usr/bin/env python3
import argparse
import csv
import importlib
import io
import zlib
from pathlib import Path


VECTOR_FIELDS = ("PosX", "PosY", "PosZ", "RotX", "RotY", "RotZ", "RotW")
EXPECTED_COLUMNS = (
    ["utc", "sessionTime", "frame", "blockTime", "mode", "route", "hold"]
    + ["headPosX", "headPosY", "headPosZ", "headRotX", "headRotY", "headRotZ", "headRotW"]
    + [f"L{bone}{field}" for bone in range(26) for field in VECTOR_FIELDS]
    + ["LConf"]
    + [f"R{bone}{field}" for bone in range(26) for field in VECTOR_FIELDS]
    + ["RConf"]
    + [f"{hand}{field}" for hand in "LR" for field in ("Hold", "GripFlag", "FingerMask", "GripScore")]
)
FLOAT_COLUMNS = {
    "sessionTime",
    "blockTime",
    "headPosX", "headPosY", "headPosZ",
    "headRotX", "headRotY", "headRotZ", "headRotW",
    "LGripScore",
    "RGripScore",
} | {
    f"{hand}{bone}{field}"
    for hand in "LR"
    for bone in range(26)
    for field in VECTOR_FIELDS
}


def recover_gzip_members(path: Path) -> bytes:
    data = path.read_bytes()
    output = bytearray()
    offset = 0
    while offset < len(data):
        marker = data.find(b"\x1f\x8b", offset)
        if marker < 0:
            break
        decompressor = zlib.decompressobj(16 + zlib.MAX_WBITS)
        try:
            member = decompressor.decompress(data[marker:]) + decompressor.flush()
        except zlib.error:
            break
        if not decompressor.eof:
            break
        output.extend(member)
        consumed = len(data[marker:]) - len(decompressor.unused_data)
        if consumed <= 0:
            break
        offset = marker + consumed
    return bytes(output)


def read_capture(path: Path):
    pd = importlib.import_module("pandas")

    return pd.read_csv(io.BytesIO(read_recovered_bytes(path)))


def read_capture_rows(path: Path):
    recovered = read_recovered_bytes(path).decode("utf-8")
    return list(csv.DictReader(io.StringIO(recovered)))


def validate_capture_rows(path: Path):
    recovered = read_recovered_bytes(path).decode("utf-8")
    reader = csv.DictReader(io.StringIO(recovered))
    if reader.fieldnames != EXPECTED_COLUMNS:
        raise RuntimeError(
            f"Capture schema mismatch: expected {len(EXPECTED_COLUMNS)} columns, "
            f"found {len(reader.fieldnames or [])}"
        )

    rows = list(reader)
    if not rows:
        raise RuntimeError("Capture contains no rows")

    previous_time = -1.0
    for index, row in enumerate(rows, start=2):
        if None in row or any(value is None for value in row.values()):
            raise RuntimeError(f"Capture row {index} has the wrong number of columns")
        for column in FLOAT_COLUMNS:
            value = row[column]
            parts = value.lstrip("-").split(".")
            if len(parts) != 2 or not all(part.isdigit() for part in parts) or len(parts[1]) != 5:
                raise RuntimeError(f"Capture row {index} column {column} is not formatted to 5 decimals")
        block_time = float(row["blockTime"])
        if block_time < previous_time:
            raise RuntimeError(f"Capture blockTime decreases at row {index}")
        previous_time = block_time
    return rows


def read_recovered_bytes(path: Path) -> bytes:
    recovered = recover_gzip_members(path)
    if not recovered:
        raise RuntimeError(f"No complete gzip member found in {path}")
    return recovered


def main():
    parser = argparse.ArgumentParser(description="Read and validate a VHard capture.csv.gz.")
    parser.add_argument("capture", type=Path)
    parser.add_argument("--plot", type=Path)
    args = parser.parse_args()
    try:
        rows = validate_capture_rows(args.capture)
    except RuntimeError as error:
        raise SystemExit(str(error)) from error
    times = [float(row["blockTime"]) for row in rows]
    try:
        frame = read_capture(args.capture)
    except ModuleNotFoundError:
        frame = None
        print(f"rows={len(rows)} columns={len(rows[0])} duration={times[-1]:.2f}s (stdlib fallback)")
    else:
        print(f"rows={len(frame)} columns={len(frame.columns)} duration={frame['blockTime'].iloc[-1]:.2f}s")
    if args.plot:
        if frame is None:
            raise SystemExit("--plot requires pandas and matplotlib")
        plt = importlib.import_module("matplotlib.pyplot")

        fingertip_distance = (
            (frame["L10PosX"].diff() ** 2)
            + (frame["L10PosY"].diff() ** 2)
            + (frame["L10PosZ"].diff() ** 2)
        ) ** 0.5
        figure, axes = plt.subplots(3, 1, sharex=True)
        axes[0].plot(frame["blockTime"], frame["headPosY"])
        axes[0].set_ylabel("Head Y (m)")
        axes[1].plot(frame["blockTime"], fingertip_distance)
        axes[1].set_ylabel("L index delta (m)")
        axes[2].plot(frame["blockTime"], frame["LGripScore"], label="Left")
        axes[2].plot(frame["blockTime"], frame["RGripScore"], label="Right")
        axes[2].set_ylabel("Grip score")
        axes[2].legend(loc="upper right")
        axes[2].set_xlabel("Block time (s)")
        figure.tight_layout()
        figure.savefig(args.plot)


if __name__ == "__main__":
    main()
