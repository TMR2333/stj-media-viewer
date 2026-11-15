#!/usr/bin/env python
# 元数据引擎边界用例生成器（仅用于 MetaSmoke 自测，不属于交付物）
# 生成：真实 ffmpeg 容器(mkv/webm/ogg/opus/aiff/m4a)、合成 TIFF(tag700+513/514)、
#       合成 HEIC、合成 CR3(+双扩展名边车)、错误扩展名、大体积 RAW(IFD 快路径/全扫描)。
import io, os, struct, subprocess, sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(ROOT, "tools", "MetaSmoke", "out", "extra")
FF = os.path.join(ROOT, "tools", "ffmpeg", "bin", "ffmpeg.exe")

from PIL import Image


def ensure_dir():
    os.makedirs(OUT, exist_ok=True)


def box(t: bytes, payload: bytes) -> bytes:
    return struct.pack(">I", 8 + len(payload)) + t + payload


def make_jpeg(w=1200, h=800, quality=70) -> bytes:
    im = Image.new("RGB", (w, h), (30, 60, 120))
    for x in range(0, w, 40):
        for y in range(0, h, 40):
            im.putpixel((x, y), (220, 180, 60))
    buf = io.BytesIO()
    im.save(buf, "JPEG", quality=quality)
    return buf.getvalue()


XMP_SAMPLE = """<?xpacket begin="\ufeff" id="W5M0MpCehiHzreSzNTczkc9d"?>
<x:xmpmeta xmlns:x="adobe:ns:meta/" x:xmptk="Adobe Lightroom">
 <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
  <rdf:Description rdf:about=""
    xmlns:xmp="http://ns.adobe.com/xap/1.0/"
    xmlns:dc="http://purl.org/dc/elements/1.1/"
    xmlns:crs="http://ns.adobe.com/camera-raw-settings/1.0/"
    xmlns:photoshop="http://ns.adobe.com/photoshop/1.0/"
    xmp:Rating="4" xmp:Label="Blue" xmp:CreatorTool="Adobe Lightroom"
    photoshop:City="上海" photoshop:Country="中国"
    crs:HasSettings="True" crs:ProcessVersion="15.4" crs:CameraProfile="Adobe Standard">
   <dc:title><rdf:Alt><rdf:li xml:lang="x-default">合成测试</rdf:li></rdf:Alt></dc:title>
   <dc:subject><rdf:Bag><rdf:li>合成</rdf:li><rdf:li>测试</rdf:li></rdf:Bag></dc:subject>
  </rdf:Description>
 </rdf:RDF>
</x:xmpmeta>
<?xpacket end="w"?>""".encode("utf-8")


def build_tiff(entries, data_blobs, little=True):
    """entries: [(tag, type, count, value_bytes_or_None)]；None 表示 value 指向 blob。
    data_blobs: [bytes] 依序追加，external entry 的 value 用其起始 offset。"""
    endian = "<" if little else ">"
    n = len(entries)
    ifd_off = 8
    ifd_size = 2 + n * 12 + 4
    data_start = ifd_off + ifd_size
    blob_offsets = []
    cursor = data_start
    for b in data_blobs:
        blob_offsets.append(cursor)
        cursor += len(b)
    out = bytearray()
    out += (b"II*\x00" if little else b"MM\x00*") + struct.pack(endian + "I", ifd_off)
    out += struct.pack(endian + "H", n)
    blob_idx = 0
    for tag, typ, count, value in entries:
        out += struct.pack(endian + "HHI", tag, typ, count)
        if value is None:
            out += struct.pack(endian + "I", blob_offsets[blob_idx])
            blob_idx += 1
        else:
            out += value.ljust(4, b"\x00")[:4]
    out += struct.pack(endian + "I", 0)
    for b in data_blobs:
        out += b
    return bytes(out)


def make_tiff_xmp(path):
    jpeg = make_jpeg(1200, 800)
    make = b"SONY\x00"
    model = b"ILCE-7M4\x00"
    xmp = XMP_SAMPLE
    data_start_guess = 8 + (2 + 7 * 12 + 4)
    # entries 排序后写；外部数据顺序：make, model, xmp, jpeg
    blobs = [make, model, xmp, jpeg]
    preview_off = data_start_guess + len(make) + len(model) + len(xmp)
    entries = [
        (0x0100, 4, 1, struct.pack("<I", 1200)),
        (0x0101, 4, 1, struct.pack("<I", 800)),
        (0x010F, 2, len(make), None),
        (0x0110, 2, len(model), None),
        (0x02BC, 1, len(xmp), None),          # TIFF tag 700 = XMP
        (0x0201, 4, 1, struct.pack("<I", preview_off)),
        (0x0202, 4, 1, struct.pack("<I", len(jpeg))),
    ]
    tiff = build_tiff(entries, blobs)
    open(path, "wb").write(tiff)
    print("TIFF", path, len(tiff), "preview@", preview_off, len(jpeg))


def make_big_raw(path, size_mb=60, with_ifd=True, preview_at_end=True):
    jpeg = make_jpeg(1600, 1067)
    total = size_mb * 1024 * 1024
    preview_off = total - len(jpeg) if preview_at_end else 4096
    if with_ifd:
        data_start_guess = 8 + (2 + 3 * 12 + 4) + 6
        tiff = bytearray()
        # Make + IFD(513/514/256) 内联；预览在文件末尾
        make = b"SONY\x00"
        n = 4
        ifd_size = 2 + n * 12 + 4
        make_off = 8 + ifd_size
        entries = [
            (0x0100, 4, 1, struct.pack("<I", 1600)),
            (0x010F, 2, len(make), struct.pack("<I", make_off)),
            (0x0201, 4, 1, struct.pack("<I", preview_off)),
            (0x0202, 4, 1, struct.pack("<I", len(jpeg))),
        ]
        tiff += b"II*\x00" + struct.pack("<I", 8) + struct.pack("<H", n)
        for tag, typ, count, value in entries:
            tiff += struct.pack("<HHI", tag, typ, count) + value.ljust(4, b"\x00")[:4]
        tiff += struct.pack("<I", 0) + make
        with open(path, "wb") as f:
            f.write(tiff)
            f.seek(total - len(jpeg))
            f.write(jpeg)
    else:
        with open(path, "wb") as f:
            f.write(b"II*\x00" + struct.pack("<I", 8) + struct.pack("<H", 0) + struct.pack("<I", 0))
            f.seek(total - len(jpeg))
            f.write(jpeg)
    print("BIGRAW", path, os.path.getsize(path), "preview@", preview_off, len(jpeg))


def make_heic(path):
    ftyp = box(b"ftyp", b"heic" + b"\x00\x00\x00\x00" + b"mif1" + b"heic")
    hdlr = box(b"hdlr", b"\x00" * 8 + b"pict" + b"\x00" * 12 + b"\x00")
    ispe = box(b"ispe", b"\x00" * 4 + struct.pack(">II", 1024, 768))
    colr = box(b"colr", b"nclx" + struct.pack(">HHH", 9, 18, 9) + b"\x01")
    ipco = box(b"ipco", ispe + colr)
    meta = box(b"meta", b"\x00" * 4 + hdlr + box(b"iprp", ipco))
    xmp_packet = XMP_SAMPLE
    mdat = box(b"mdat", struct.pack(">I", 0) + xmp_packet)
    data = ftyp + meta + mdat
    open(path, "wb").write(data)
    print("HEIC", path, len(data))


def make_cr3(path, sidecar_path):
    # CMT1 里放一个 TIFF（Make/Model/DateTimeOriginal）
    make = b"Canon\x00"
    model = b"Canon EOS R5\x00"
    dt = b"2024:07:01 10:20:30\x00"
    blobs = [make, model, dt]
    ifd_size = 2 + 3 * 12 + 4
    data_start = 8 + ifd_size
    entries = [
        (0x010F, 2, len(make), struct.pack("<I", data_start)),
        (0x0110, 2, len(model), struct.pack("<I", data_start + len(make))),
        (0x9003, 2, len(dt), struct.pack("<I", data_start + len(make) + len(model))),
    ]
    tiff = bytearray(b"II*\x00" + struct.pack("<I", 8) + struct.pack("<H", len(entries)))
    for tag, typ, count, value in entries:
        tiff += struct.pack("<HHI", tag, typ, count) + value
    tiff += struct.pack("<I", 0)
    tiff += b"".join(blobs)

    ftyp = box(b"ftyp", b"crx " + b"\x00\x00\x00\x00" + b"crx " + b"isom")
    hdlr = box(b"hdlr", b"\x00" * 8 + b"pict" + b"\x00" * 12 + b"\x00")
    ispe = box(b"ispe", b"\x00" * 4 + struct.pack(">II", 6000, 4000))
    ipco = box(b"ipco", ispe)
    meta = box(b"meta", b"\x00" * 4 + hdlr + box(b"iprp", ipco))
    uuid_cmt = box(b"uuid", b"12345678" + b"90abcdef" + b"1234" + b"5678" + b"90abcdef1234" + box(b"CMT1", tiff))
    # 规范文本字节序的 XMP UUID：BE7ACFCB-97A9-42E8-9C71-999491E3AFAC
    xmp_uuid = bytes.fromhex("BE7ACFCB97A942E89C71999491E3AFAC")
    uuid_xmp = box(b"uuid", xmp_uuid + XMP_SAMPLE)
    moov = box(b"moov", meta + uuid_cmt + uuid_xmp)
    data = ftyp + moov + box(b"mdat", b"")
    open(path, "wb").write(data)
    open(sidecar_path, "wb").write(XMP_SAMPLE)
    print("CR3", path, len(data), "sidecar", sidecar_path)


def run_ffmpeg(args, out):
    cmd = [FF, "-hide_banner", "-loglevel", "error", "-y"] + args + [out]
    r = subprocess.run(cmd, capture_output=True)
    if r.returncode != 0:
        print("FFMPEG FAIL", os.path.basename(out), r.stderr.decode("utf-8", "replace")[:300])
    else:
        print("FFMPEG", os.path.basename(out), os.path.getsize(out))


def main():
    ensure_dir()
    c = lambda name: os.path.join(OUT, name)
    v = ["-f", "lavfi", "-i", "testsrc=size=160x90:rate=25:duration=2"]
    run_ffmpeg(v + ["-c:v", "libx264", "-pix_fmt", "yuv420p"], c("video_h264.mkv"))
    run_ffmpeg(v + ["-c:v", "libvpx-vp9", "-b:v", "200k"], c("video_vp9.webm"))
    a = ["-f", "lavfi", "-i", "sine=frequency=440:duration=1.5"]
    run_ffmpeg(a + ["-c:a", "libopus", "-b:a", "64k"], c("audio_opus.ogg"))
    run_ffmpeg(a + ["-c:a", "libopus", "-b:a", "64k"], c("audio_opus.opus"))
    run_ffmpeg(a + ["-c:a", "pcm_s16le"], c("audio_pcm.aiff"))
    run_ffmpeg(a + ["-c:a", "aac", "-b:a", "96k"], c("audio_aac.m4a"))
    run_ffmpeg(v + a + ["-c:v", "libx264", "-c:a", "aac", "-shortest"], c("video_with_audio.mp4"))

    make_tiff_xmp(c("tiff_xmp.tif"))
    make_heic(c("heic_synth.heic"))
    make_cr3(c("DSC0999.CR3"), c("DSC0999.CR3.xmp"))
    make_big_raw(c("big_ifd.raw"), 60, with_ifd=True)
    make_big_raw(c("big_scan.raw"), 60, with_ifd=False)

    # 错误扩展名：真实 MP4 改名 .jpg
    mp4 = c("wrong_ext.jpg")
    open(mp4, "wb").write(open(c("video_with_audio.mp4"), "rb").read())
    print("WRONGEXT", mp4)
    # 文本文件冒充 wav
    open(c("text_as_wav.wav"), "wb").write(b"this is not a wav file at all\n" * 20)
    print("TEXTWAV done")


if __name__ == "__main__":
    main()