#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Mock WebDAV Server 冒烟测试（纯标准库）。

做真事：
  1. 用当前 Python 起一个 tools/MockWebDav/server.py 子进程（随机空闲端口，真实 TCP/HTTP）；
  2. 用 urllib 走一遍真实的 WebDAV 往返：OPTIONS / MKCOL / PUT / HEAD / PROPFIND(Depth 0/1) /
     GET / DELETE，外加 Basic 鉴权失败、404、405、409、深度 infinity、目录穿越等错误路径；
  3. 断言 multistatus XML 的结构、href 的 URL 编码、getcontentlength / getlastmodified /
     resourcetype，以及磁盘上的文件内容。

运行：
    python tools/MockWebDav/smoke.py

成功输出 `ALL CHECKS PASSED` 并返回码 0；失败打印具体断言与服务器 stderr 并返回 1。
"""

from __future__ import annotations

import base64
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

HERE = Path(__file__).resolve().parent
SERVER = HERE / "server.py"

DAV = "{DAV:}"
USER = "test"
PASSWORD = "test123"
HTTP_DATE_RE = re.compile(r"^[A-Z][a-z]{2}, \d{2} [A-Z][a-z]{2} \d{4} \d{2}:\d{2}:\d{2} GMT$")

RESULTS: list[tuple[str, bool, str]] = []


def check(name: str, ok: bool, detail: str = "") -> None:
    RESULTS.append((name, ok, detail))
    mark = "PASS" if ok else "FAIL"
    print(f"[{mark}] {name}" + (f"  [{detail}]" if detail else ""))


# --------------------------------------------------------------------------------------
# HTTP 工具（绕过系统 http_proxy，localhost 请求必须直连）
# --------------------------------------------------------------------------------------
_OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def http(method: str, url: str, *, body: bytes | None = None,
         headers: dict | None = None, auth: tuple[str, str] | None | bool = True,
         timeout: float = 10.0) -> tuple[int, dict, bytes]:
    request = urllib.request.Request(url, data=body, method=method)
    for key, value in (headers or {}).items():
        request.add_header(key, value)
    if auth is True:
        token = base64.b64encode(f"{USER}:{PASSWORD}".encode("utf-8")).decode("ascii")
        request.add_header("Authorization", "Basic " + token)
    elif isinstance(auth, tuple) and auth:
        token = base64.b64encode(f"{auth[0]}:{auth[1]}".encode("utf-8")).decode("ascii")
        request.add_header("Authorization", "Basic " + token)
    try:
        with _OPENER.open(request, timeout=timeout) as response:
            return response.status, dict(response.headers), response.read()
    except urllib.error.HTTPError as exc:
        return exc.code, dict(exc.headers), exc.read()


def quote_path(relative: str) -> str:
    """按段 URL 编码，保留 '/'。"""
    return "/".join(urllib.parse.quote(part, safe="") for part in relative.split("/") if part)


def wait_ready(port: int, timeout: float = 15.0) -> bool:
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            with socket.create_connection(("127.0.0.1", port), timeout=0.3):
                return True
        except OSError:
            time.sleep(0.05)
    return False


def find_free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def propfind_entries(xml_bytes: bytes) -> list[dict]:
    """解析 multistatus，返回 [{href, is_collection, size, modified}]。"""
    root = ET.fromstring(xml_bytes)
    entries = []
    for response in root.findall(f"{DAV}response"):
        href_el = response.find(f"{DAV}href")
        if href_el is None or not (href_el.text or "").strip():
            continue
        entry = {
            "href": href_el.text.strip(),
            "is_collection": False,
            "size": None,
            "modified": None,
        }
        for propstat in response.findall(f"{DAV}propstat"):
            prop = propstat.find(f"{DAV}prop")
            if prop is None:
                continue
            resourcetype = prop.find(f"{DAV}resourcetype")
            if resourcetype is not None and resourcetype.find(f"{DAV}collection") is not None:
                entry["is_collection"] = True
            length = prop.find(f"{DAV}getcontentlength")
            if length is not None and (length.text or "").strip():
                entry["size"] = int(length.text.strip())
            modified = prop.find(f"{DAV}getlastmodified")
            if modified is not None and (modified.text or "").strip():
                entry["modified"] = modified.text.strip()
        entries.append(entry)
    return entries


# --------------------------------------------------------------------------------------
# 冒烟流程
# --------------------------------------------------------------------------------------
def run_checks(port: int, disk_root: Path) -> None:
    base = f"http://127.0.0.1:{port}"
    dir_rel = "测试目录"
    file_rel = f"{dir_rel}/测试 照片.jpg"
    dir_url = f"{base}/{quote_path(dir_rel)}/"
    file_url = f"{base}/{quote_path(file_rel)}"
    payload = b"STJ-WEBDAV-SMOKE-" + bytes(range(256)) * 4

    # 1) OPTIONS：能力声明
    status, headers, _ = http("OPTIONS", base + "/")
    check("OPTIONS 200", status == 200, f"HTTP {status}")
    check("OPTIONS 声明 DAV: 1", (headers.get("DAV") or "").strip() == "1", headers.get("DAV", ""))

    # 2) 鉴权：错误密码 / 无凭据
    status, headers, _ = http("PROPFIND", base + "/", auth=(USER, "wrong-password"),
                              headers={"Depth": "0"})
    check("错误密码 → 401", status == 401, f"HTTP {status}")
    check("401 带 WWW-Authenticate: Basic realm=\"mock\"",
          'Basic realm="mock"' in (headers.get("WWW-Authenticate") or ""),
          headers.get("WWW-Authenticate", ""))
    status, _, _ = http("PROPFIND", base + "/", auth=None, headers={"Depth": "0"})
    check("无凭据 → 401", status == 401, f"HTTP {status}")

    # 3) PROPFIND 同步根 Depth 0
    status, _, body = http("PROPFIND", base + "/", headers={"Depth": "0"})
    check("PROPFIND 根 Depth:0 → 207", status == 207, f"HTTP {status}")
    entries = propfind_entries(body)
    check("Depth:0 只返回一行", len(entries) == 1, f"{len(entries)} 行")
    check("根是 collection", bool(entries) and entries[0]["is_collection"], entries[0]["href"] if entries else "")
    check("根 href 是 /", bool(entries) and entries[0]["href"] == "/", entries[0]["href"] if entries else "")
    check("根带 getlastmodified（RFC 1123）",
          bool(entries) and HTTP_DATE_RE.match(entries[0]["modified"] or "") is not None,
          entries[0]["modified"] if entries else "")

    # 4) MKCOL：新建 / 重复 / 父目录不存在
    status, _, _ = http("MKCOL", dir_url)
    check("MKCOL 新目录 → 201", status == 201, f"HTTP {status}")
    status, _, _ = http("MKCOL", dir_url)
    check("MKCOL 已存在 → 405", status == 405, f"HTTP {status}")
    status, _, _ = http("MKCOL", f"{base}/{quote_path('不存在的父/子目录')}/")
    check("MKCOL 父目录不存在 → 409", status == 409, f"HTTP {status}")

    # 5) PUT：新建 201 / 覆盖 204，并核对磁盘
    status, _, _ = http("PUT", file_url, body=payload)
    check("PUT 新文件 → 201", status == 201, f"HTTP {status}")
    disk_file = disk_root / dir_rel / "测试 照片.jpg"
    check("PUT 已落盘且内容一致", disk_file.is_file() and disk_file.read_bytes() == payload,
          f"{disk_file.stat().st_size if disk_file.is_file() else -1} 字节")
    status, _, _ = http("PUT", file_url, body=payload + b"-v2")
    check("PUT 覆盖 → 204", status == 204, f"HTTP {status}")
    payload = payload + b"-v2"
    check("覆盖后磁盘内容更新", disk_file.read_bytes() == payload, "")

    # 6) HEAD
    status, headers, body = http("HEAD", file_url)
    check("HEAD → 200", status == 200, f"HTTP {status}")
    check("HEAD Content-Length 正确", headers.get("Content-Length") == str(len(payload)),
          headers.get("Content-Length", ""))
    check("HEAD 无响应体", body == b"", f"{len(body)} 字节")

    # 7) PROPFIND 目录 Depth:1：目录自身 + 文件都在，href 已 URL 编码
    status, _, body = http("PROPFIND", dir_url, headers={"Depth": "1"})
    check("PROPFIND 目录 Depth:1 → 207", status == 207, f"HTTP {status}")
    text = body.decode("utf-8", "replace")
    expected_file_href = "/" + quote_path(file_rel)
    dir_href = "/" + quote_path(dir_rel) + "/"
    entries = propfind_entries(body)
    by_href = {entry["href"]: entry for entry in entries}
    check("Depth:1 包含自身与文件", len(entries) == 2, f"{sorted(by_href)}")
    check("文件 href 已 URL 编码（无中文原文）",
          expected_file_href in by_href and "%E6%B5%8B%E8%AF%95" in text and "测试" not in text,
          expected_file_href)
    file_entry = by_href.get(expected_file_href, {})
    check("文件 getcontentlength 正确", file_entry.get("size") == len(payload),
          str(file_entry.get("size")))
    check("文件 getlastmodified 是 RFC 1123",
          HTTP_DATE_RE.match(file_entry.get("modified") or "") is not None,
          file_entry.get("modified") or "")
    check("文件不是 collection", file_entry.get("is_collection") is False, "")
    check("目录是 collection", by_href.get(dir_href, {}).get("is_collection") is True, "")

    # 根 Depth:1 只应列出目录（文件在下一层，证明是逐层下钻而非 infinity）
    status, _, body = http("PROPFIND", base + "/", headers={"Depth": "1"})
    root_entries = propfind_entries(body)
    check("根 Depth:1 只含根与一级目录", status == 207 and len(root_entries) == 2,
          f"HTTP {status} / {len(root_entries)} 行")

    # 8) PROPFIND 文件 Depth:0
    status, _, body = http("PROPFIND", file_url, headers={"Depth": "0"})
    entries = propfind_entries(body)
    check("PROPFIND 文件 Depth:0 有长度",
          status == 207 and len(entries) == 1 and entries[0]["size"] == len(payload),
          f"HTTP {status}")

    # 9) Depth: infinity 明确拒绝
    status, _, _ = http("PROPFIND", base + "/", headers={"Depth": "infinity"})
    check("Depth: infinity → 403", status == 403, f"HTTP {status}")

    # 10) GET
    status, _, body = http("GET", file_url)
    check("GET → 200", status == 200, f"HTTP {status}")
    check("GET 内容一致", body == payload, f"{len(body)} 字节")
    status, _, _ = http("GET", f"{base}/nope.jpg")
    check("GET 不存在 → 404", status == 404, f"HTTP {status}")
    status, _, _ = http("GET", f"{base}/%2e%2e/%2e%2e/etc/passwd")
    check("目录穿越 → 403", status == 403, f"HTTP {status}")

    # 11) PROPFIND 不存在路径 → 404
    status, _, _ = http("PROPFIND", f"{base}/{quote_path('不存在的目录')}/", headers={"Depth": "0"})
    check("PROPFIND 不存在 → 404", status == 404, f"HTTP {status}")

    # 12) DELETE
    status, _, _ = http("DELETE", file_url)
    check("DELETE 文件 → 204", status == 204, f"HTTP {status}")
    check("DELETE 后磁盘已删除", not disk_file.exists(), str(disk_file))
    status, _, _ = http("DELETE", file_url)
    check("DELETE 不存在 → 404", status == 404, f"HTTP {status}")
    status, _, _ = http("GET", file_url)
    check("删除后 GET → 404", status == 404, f"HTTP {status}")


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

    if not SERVER.is_file():
        print(f"!! 找不到 {SERVER}")
        return 1

    port = find_free_port()
    workdir = Path(tempfile.mkdtemp(prefix="stj-mock-webdav-smoke-"))
    disk_root = workdir / "root"
    disk_root.mkdir(parents=True, exist_ok=True)
    print("=" * 68)
    print(" Mock WebDAV Server smoke")
    print(f"   python     : {sys.executable}")
    print(f"   endpoint   : http://127.0.0.1:{port}/")
    print(f"   disk root  : {disk_root}")
    print("=" * 68, flush=True)

    proc = subprocess.Popen(
        [sys.executable, str(SERVER), "--port", str(port), "--root", str(disk_root),
         "--user", USER, "--password", PASSWORD, "--quiet"],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
        cwd=str(HERE),
    )
    try:
        if not wait_ready(port):
            print("!! mock server 没能在 15 秒内监听端口")
            return 1
        run_checks(port, disk_root)
    except Exception as exc:  # noqa: BLE001 - 冒烟脚本要把任何异常变成 FAIL 而不是崩栈
        check("冒烟流程无异常", False, f"{type(exc).__name__}: {exc}")
    finally:
        if proc.poll() is None:
            proc.terminate()
            try:
                proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait(timeout=5)
        try:
            stderr = proc.stderr.read().decode("utf-8", "replace") if proc.stderr else ""
        except (OSError, ValueError):
            stderr = ""
        shutil.rmtree(workdir, ignore_errors=True)

    passed = sum(1 for _, ok, _ in RESULTS if ok)
    failed = len(RESULTS) - passed
    print()
    print("=" * 68)
    if failed == 0 and passed > 0:
        print(f" ALL CHECKS PASSED  ({passed} 项)")
    else:
        print(f" {passed} passed / {failed} failed")
        if stderr.strip():
            print("--- mock server stderr ---")
            print(stderr.strip()[-2000:])
    print("=" * 68)
    return 0 if failed == 0 and passed > 0 else 1


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(130)