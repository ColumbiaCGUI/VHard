"""Minimal binary-FBX reader: enough to pull each Geometry node's name and vertices.

Exists so the seating gate in verify_hold_seating.py can measure the normalised hold
meshes without opening Unity. Handles FBX 7.x binary (both the pre-7500 32-bit and the
7500+ 64-bit node headers) and the zlib-deflated array encoding Unity writes.
"""
from __future__ import annotations

import struct
import zlib
from pathlib import Path

MAGIC = b"Kaydara FBX Binary  \x00"

_ARRAY_CODES = {"f": ("<f", 4), "d": ("<d", 8), "l": ("<q", 8), "i": ("<i", 4), "b": ("<b", 1)}
_SCALAR_CODES = {"Y": ("<h", 2), "C": ("<?", 1), "I": ("<i", 4), "F": ("<f", 4), "D": ("<d", 8), "L": ("<q", 8)}


class Node:
    __slots__ = ("name", "props", "children")

    def __init__(self, name: str):
        self.name = name
        self.props: list = []
        self.children: list["Node"] = []

    def find_all(self, name: str, out: list | None = None) -> list["Node"]:
        if out is None:
            out = []
        for child in self.children:
            if child.name == name:
                out.append(child)
            child.find_all(name, out)
        return out


def _read_property(buf: bytes, off: int):
    code = chr(buf[off])
    off += 1
    if code in _SCALAR_CODES:
        fmt, size = _SCALAR_CODES[code]
        return struct.unpack_from(fmt, buf, off)[0], off + size
    if code in ("S", "R"):
        (length,) = struct.unpack_from("<I", buf, off)
        off += 4
        return buf[off:off + length], off + length
    if code in _ARRAY_CODES:
        fmt, size = _ARRAY_CODES[code]
        count, encoding, comp_len = struct.unpack_from("<III", buf, off)
        off += 12
        raw = buf[off:off + comp_len]
        off += comp_len
        if encoding == 1:
            raw = zlib.decompress(raw)
        return struct.unpack_from("<%d%s" % (count, fmt[1]), raw, 0), off
    raise ValueError("unsupported FBX property code %r at offset %d" % (code, off - 1))


def _read_node(buf: bytes, off: int, wide: bool):
    if wide:
        end_offset, num_props, _prop_len = struct.unpack_from("<QQQ", buf, off)
        off += 24
    else:
        end_offset, num_props, _prop_len = struct.unpack_from("<III", buf, off)
        off += 12
    name_len = buf[off]
    off += 1
    name = buf[off:off + name_len].decode("utf-8", "replace")
    off += name_len
    if end_offset == 0:                      # null record terminates a sibling list
        return None, off
    node = Node(name)
    for _ in range(num_props):
        value, off = _read_property(buf, off)
        node.props.append(value)
    while off < end_offset:
        child, off = _read_node(buf, off, wide)
        if child is None:
            break
        node.children.append(child)
    return node, end_offset


def parse(path: str | Path) -> Node:
    buf = Path(path).read_bytes()
    if not buf.startswith(MAGIC):
        raise ValueError("not a binary FBX: %s" % path)
    # 21-byte magic, then a 2-byte [0x1A, 0x00] pad, then the uint32 version.
    (version,) = struct.unpack_from("<I", buf, len(MAGIC) + 2)
    wide = version >= 7500
    off = len(MAGIC) + 2 + 4
    root = Node("__root__")
    while off < len(buf) - 160:
        node, off = _read_node(buf, off, wide)
        if node is None:
            break
        root.children.append(node)
    return root


def geometry_vertices(path: str | Path) -> dict[str, tuple]:
    """Map each Geometry node's mesh name -> its flat vertex tuple (x,y,z,x,y,z,...).

    FBX names Geometry nodes "<name>\\x00\\x01Geometry"; we keep the leading name, which
    for this asset is the source file the child was built from.
    """
    meshes: dict[str, tuple] = {}
    for geom in parse(path).find_all("Geometry"):
        label = None
        for prop in geom.props:
            if isinstance(prop, bytes) and b"Geometry" in prop:
                label = prop.split(b"\x00")[0].decode("utf-8", "replace").strip()
                break
        if label is None:
            continue
        for child in geom.children:
            if child.name == "Vertices" and child.props:
                meshes[label] = child.props[0]
                break
    return meshes


def extent_mm(vertices: tuple) -> tuple[float, float, float]:
    """Axis-aligned extents in millimetres. Mesh units x 1000 = mm for this asset."""
    xs = vertices[0::3]
    ys = vertices[1::3]
    zs = vertices[2::3]
    return (
        (max(xs) - min(xs)) * 1000.0,
        (max(ys) - min(ys)) * 1000.0,
        (max(zs) - min(zs)) * 1000.0,
    )
