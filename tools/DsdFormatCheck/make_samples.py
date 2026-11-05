#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""生成最小合法的 DSF / DFF 样本，供 DsdFormatCheck 做解析自检。
数据全是固定字节模式（ch0=0x11, ch1=0x22, ch2=0x33 …），方便断言 L/R 交织与多声道取前两。
"""
import os
import struct

OUT = os.path.dirname(os.path.abspath(__file__))
RATE = 2822400
BLOCK = 4096


def write(name, data):
    with open(os.path.join(OUT, name), "wb") as f:
        f.write(data)
    print(name, len(data), "bytes")


def dsf(channels=2, blocks=2):
    data = bytearray()
    for _ in range(blocks):
        for c in range(channels):
            data += bytes([0x11 + c * 0x11]) * BLOCK

    out = bytearray()
    out += b"DSD "
    out += struct.pack("<Q", 28)
    out += struct.pack("<Q", 0)
    out += struct.pack("<Q", 0)
    out += b"fmt "
    out += struct.pack("<Q", 52)
    out += struct.pack("<I", 1)
    out += struct.pack("<I", 0)
    out += struct.pack("<I", 0)
    out += struct.pack("<I", channels)
    out += struct.pack("<I", RATE)
    out += struct.pack("<I", 1)  # bitsPerSample=1 -> LSB-first
    out += struct.pack("<Q", len(data) * 8 // channels)
    out += struct.pack("<I", BLOCK)
    out += struct.pack("<I", 0)  # reserved
    out += b"data"
    out += struct.pack("<Q", len(data))
    out += data
    return bytes(out)


def iff_chunk(cid, payload):
    pad = b"\x00" if len(payload) % 2 else b""
    return cid + struct.pack(">Q", len(payload)) + payload + pad


def dff(channels=2, frames=4096):
    fs = iff_chunk(b"FS  ", struct.pack(">I", RATE))
    chnl_ids = {
        1: b"SLFT", 2: b"SLFTSRGT", 6: b"SLFTSRGT"
                                         b"C   L   "
                                         b"LS  RS  ",
    }
    ids = chnl_ids.get(channels)
    if ids is None:
        ids = b"".join([b"C%03d" % i for i in range(channels)])
    chnl = iff_chunk(b"CHNL", struct.pack(">H", channels) + ids)
    cmpr = iff_chunk(b"CMPR", b"DSD " + struct.pack(">I", 0) + b"\x00\x00")
    prop = iff_chunk(b"PROP", b"SND " + fs + chnl + cmpr)
    fver = iff_chunk(b"FVER", bytes([1, 5, 0, 0]))

    data = bytearray()
    for _ in range(frames):
        for c in range(channels):
            data.append(0x11 + c * 0x11)
    dsd = iff_chunk(b"DSD ", bytes(data))

    body = b"DSD " + fver + prop + dsd
    return b"FRM8" + struct.pack(">Q", len(body)) + body


write("sample.dsf", dsf())
write("sample.dff", dff())
write("sample6.dff", dff(channels=6, frames=2048))
