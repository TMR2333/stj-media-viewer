#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Mock WebDAV Server — 最小可用的 WebDAV 服务（纯 Python 标准库，无第三方依赖）。

用途：在没有坚果云 / Nextcloud / 群晖账号、也不联网的情况下，用**真实 HTTP 往返**验证
stj-media-viewer 的 WebDAV 同步链路（WebDavCloudStorage：PROPFIND 下钻 / PUT / GET / MKCOL）。

支持的方法
----------
    OPTIONS   返回 DAV: 1 能力声明
    PROPFIND  Depth 0 / 1，返回 D:multistatus XML（href 已 URL 编码，含
              getlastmodified / getcontentlength / resourcetype）
    GET/HEAD  下载文件
    PUT       传（支持 Content-Length 与 chunked），先写临时文件再原子替换
    MKCOL     建目录（已存在 405，父目录不存在 409）
    DELETE    删文件 / 递归删目录

鉴权
----
    HTTP Basic。凭据错误 / 缺失一律 401 + `WWW-Authenticate: Basic realm="mock"`。

启动
----
    python tools/MockWebDav/server.py --port 8899 --root <dir> --user test --password test123
    python tools/MockWebDav/server.py --port 0 --root ./tmp-dav        # 端口 0 = 随机
    python tools/MockWebDav/server.py --help

冒烟测试（起服务器 → 真实 HTTP PUT/PROPFIND/GET/MKCOL → 断言）：
    python tools/MockWebDav/smoke.py

安全提醒
--------
**仅供本机自检使用！** 默认只绑定 127.0.0.1；Basic 认证是明文口令 + 无 TLS，
绝对不要 --host 0.0.0.0 暴露到公网或不受信任的网络。
"""

from __future__ import annotations

import argparse
import base64
import email.utils
import hmac
import os
import shutil
import sys
import urllib.parse
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from xml.sax.saxutils import escape

VERSION = "1.0.0"
DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 8899
DEFAULT_USER = "test"
DEFAULT_PASSWORD = "test123"

# WebDAV XML 元素在本服务里统一用 D: 前缀；请求体不做严格校验（各家客户端五花八门）。
XML_DECL = '<?xml version="1.0" encoding="utf-8"?>\n'
DAV_NS = "DAV:"


class DavHttpError(Exception):
    """带 HTTP 状态码的内部异常，供方法实现里提前退出。"""

    def __init__(self, status: int, message: str = ""):
        super().__init__(message or HTTPStatus(status).phrase)
        self.status = status
        self.message = message or HTTPStatus(status).phrase


def _now_http_date(mtime: float) -> str:
    """POSIX 时间戳 → HTTP-date（RFC 1123，GMT）。"""
    return email.utils.formatdate(mtime, usegmt=True)


def _href_for(root: Path, target: Path, is_dir: bool) -> str:
    """文件系统路径 → URL 编码的 href（以 '/' 开头，目录带末尾 '/'）。"""
    try:
        relative = target.relative_to(root)
    except ValueError:
        raise DavHttpError(403, "path escapes root")
    parts = [urllib.parse.quote(part, safe="") for part in relative.parts]
    href = "/" + "/".join(parts) if parts else "/"
    if is_dir and not href.endswith("/"):
        href += "/"
    return href


class MockWebDavHandler(BaseHTTPRequestHandler):
    server_version = f"MockWebDav/{VERSION}"
    protocol_version = "HTTP/1.1"

    # ------------------------------------------------------------------ 基础输出

    def log_message(self, fmt, *args):  # noqa: A003
        if getattr(self.server, "quiet", False):
            return
        sys.stderr.write("[mock-webdav] %s - %s\n" % (self.address_string(), fmt % args))

    def _send(self, status: int, body: bytes = b"", content_type: str = "text/plain; charset=utf-8",
              headers: dict | None = None, close: bool = False) -> None:
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        for key, value in (headers or {}).items():
            self.send_header(key, value)
        if close:
            self.send_header("Connection", "close")
            self.close_connection = True
        self.end_headers()
        if body and self.command != "HEAD":
            try:
                self.wfile.write(body)
            except (BrokenPipeError, ConnectionResetError):
                pass

    def _send_error_text(self, status: int, message: str) -> None:
        self._send(status, (message + "\n").encode("utf-8"), close=True)

    def _send_dav_error(self, status: int, tag: str) -> None:
        body = (XML_DECL + f'<D:error xmlns:D="{DAV_NS}"><D:{tag}/></D:error>').encode("utf-8")
        self._send(status, body, content_type='application/xml; charset="utf-8"', close=True)

    # ------------------------------------------------------------------ 认证

    def _authorized(self) -> bool:
        header = self.headers.get("Authorization", "")
        if not header.lower().startswith("basic "):
            return False
        try:
            raw = base64.b64decode(header[6:].strip(), validate=True).decode("utf-8")
        except (ValueError, UnicodeDecodeError):
            return False
        user, sep, password = raw.partition(":")
        if not sep:
            return False
        return hmac.compare_digest(user, self.server.username) and hmac.compare_digest(
            password, self.server.password)

    def _require_auth(self) -> bool:
        """未通过鉴权时直接回 401（带 WWW-Authenticate），返回 False。"""
        if self._authorized():
            return True
        # 有些方法带请求体（PUT/PROPFIND）：先读掉再回 401，避免客户端还没写完 body
        # 就收到响应 + 连接被关，从而把 401 变成 broken pipe。
        try:
            self._read_body()
        except Exception:  # noqa: BLE001 - 鉴权失败时 body 是什么样的都不重要
            pass
        self._send(
            HTTPStatus.UNAUTHORIZED,
            b"unauthorized\n",
            headers={"WWW-Authenticate": 'Basic realm="mock"'},
            close=True,
        )
        return False

    # ------------------------------------------------------------------ 请求体 / 路径

    def _read_body(self) -> bytes:
        transfer_encoding = (self.headers.get("Transfer-Encoding") or "").lower()
        if "chunked" in transfer_encoding:
            return self._read_chunked_body()

        try:
            length = int(self.headers.get("Content-Length") or 0)
        except ValueError:
            raise DavHttpError(400, "invalid Content-Length")
        if length <= 0:
            return b""
        chunks = []
        remaining = length
        while remaining > 0:
            chunk = self.rfile.read(min(remaining, 1024 * 1024))
            if not chunk:
                break
            chunks.append(chunk)
            remaining -= len(chunk)
        return b"".join(chunks)

    def _read_chunked_body(self) -> bytes:
        out = bytearray()
        while True:
            line = self.rfile.readline(1024)
            if not line:
                break
            try:
                size = int(line.split(b";")[0].strip() or b"0", 16)
            except ValueError:
                raise DavHttpError(400, "invalid chunk size")
            if size == 0:
                while True:  # 吃掉 trailer
                    trailer = self.rfile.readline(1024)
                    if trailer in (b"\r\n", b"\n", b""):
                        break
                break
            out += self.rfile.read(size)
            self.rfile.read(2)  # 每个 chunk 后面的 CRLF
        return bytes(out)

    def _target_path(self) -> Path:
        """URL → 文件系统路径；拒绝目录穿越，越界返回 403。"""
        raw = urllib.parse.urlsplit(self.path).path
        try:
            decoded = urllib.parse.unquote(raw, errors="strict")
        except UnicodeDecodeError:
            raise DavHttpError(400, "invalid percent-encoding")
        if "\\" in decoded or "\x00" in decoded:
            raise DavHttpError(400, "invalid path")
        segments = [s for s in decoded.split("/") if s]
        if any(s in (".", "..") for s in segments):
            raise DavHttpError(403, "path traversal is forbidden")

        root = self.server.root
        target = root.joinpath(*segments)
        try:
            resolved = target.resolve(strict=False)
        except OSError:
            raise DavHttpError(400, "invalid path")
        if resolved != root and root not in resolved.parents:
            raise DavHttpError(403, "path escapes root")
        return resolved

    def _depth(self) -> str:
        depth = (self.headers.get("Depth") or "0").strip().lower()
        return depth or "0"

    # ------------------------------------------------------------------ PROPFIND

    def _prop_response_xml(self, root: Path, target: Path, depth: str) -> str:
        entries: list[Path] = [target]
        if depth == "1" and target.is_dir():
            try:
                children = sorted(target.iterdir(), key=lambda p: p.name.lower())
            except OSError:
                children = []
            entries.extend(children)

        parts = [XML_DECL, f'<D:multistatus xmlns:D="{DAV_NS}">']
        for entry in entries:
            try:
                is_dir = entry.is_dir()
                stat = entry.stat()
            except OSError:
                continue
            parts.append("<D:response>")
            parts.append(f"<D:href>{escape(_href_for(root, entry, is_dir))}</D:href>")
            parts.append("<D:propstat><D:prop>")
            if is_dir:
                parts.append("<D:resourcetype><D:collection/></D:resourcetype>")
            else:
                parts.append("<D:resourcetype/>")
                parts.append(f"<D:getcontentlength>{stat.st_size}</D:getcontentlength>")
            parts.append(f"<D:getlastmodified>{_now_http_date(stat.st_mtime)}</D:getlastmodified>")
            parts.append("</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat>")
            parts.append("</D:response>")
        parts.append("</D:multistatus>")
        return "".join(parts)

    def do_PROPFIND(self):  # noqa: N802
        if not self._require_auth():
            return
        try:
            target = self._target_path()
            _ = self._read_body()  # 请求体（属性清单）不解析，但必须读掉
        except DavHttpError as exc:
            self._send_error_text(exc.status, exc.message)
            return

        if not target.exists():
            self._send_error_text(HTTPStatus.NOT_FOUND, "not found")
            return

        depth = self._depth()
        if depth == "infinity":
            # 和坚果云/群晖一致：不支持无限深度，明确拒绝而不是卡死。
            self._send_dav_error(HTTPStatus.FORBIDDEN, "propfind-finite-depth")
            return
        if depth not in ("0", "1"):
            self._send_error_text(HTTPStatus.BAD_REQUEST, "invalid Depth")
            return

        xml = self._prop_response_xml(self.server.root, target, depth)
        self._send(HTTPStatus.MULTI_STATUS, xml.encode("utf-8"),
                   content_type='application/xml; charset="utf-8"')

    # ------------------------------------------------------------------ GET / HEAD

    def _serve_file(self, with_body: bool) -> None:
        if not self._require_auth():
            return
        try:
            target = self._target_path()
        except DavHttpError as exc:
            self._send_error_text(exc.status, exc.message)
            return

        if not target.exists():
            self._send_error_text(HTTPStatus.NOT_FOUND, "not found")
            return
        if target.is_dir():
            self._send_error_text(HTTPStatus.METHOD_NOT_ALLOWED, "target is a collection")
            return

        try:
            stat = target.stat()
            file = target.open("rb")
        except OSError as exc:
            self._send_error_text(HTTPStatus.INTERNAL_SERVER_ERROR, f"cannot read: {exc}")
            return

        with file:
            self.send_response(HTTPStatus.OK)
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Content-Length", str(stat.st_size))
            self.send_header("Last-Modified", _now_http_date(stat.st_mtime))
            self.end_headers()
            if not with_body:
                return
            try:
                shutil.copyfileobj(file, self.wfile, length=256 * 1024)
            except (BrokenPipeError, ConnectionResetError):
                pass

    def do_GET(self):  # noqa: N802
        self._serve_file(with_body=True)

    def do_HEAD(self):  # noqa: N802
        self._serve_file(with_body=False)

    # ------------------------------------------------------------------ PUT

    def do_PUT(self):  # noqa: N802
        if not self._require_auth():
            return
        try:
            target = self._target_path()
            body = self._read_body()
        except DavHttpError as exc:
            self._send_error_text(exc.status, exc.message)
            return

        if target.is_dir():
            self._send_error_text(HTTPStatus.METHOD_NOT_ALLOWED, "target is a collection")
            return
        parent = target.parent
        if not parent.is_dir():
            self._send_error_text(HTTPStatus.CONFLICT, "parent collection does not exist")
            return

        existed = target.exists()
        tmp = parent / f".{target.name}.{os.getpid()}.tmp"
        try:
            with open(tmp, "wb") as fh:
                fh.write(body)
                fh.flush()
                os.fsync(fh.fileno())
            os.replace(tmp, target)
        except OSError as exc:
            try:
                tmp.unlink(missing_ok=True)
            except OSError:
                pass
            self._send_error_text(HTTPStatus.INTERNAL_SERVER_ERROR, f"cannot write: {exc}")
            return

        self._send(HTTPStatus.NO_CONTENT if existed else HTTPStatus.CREATED, b"")

    # ------------------------------------------------------------------ MKCOL

    def do_MKCOL(self):  # noqa: N802
        if not self._require_auth():
            return
        try:
            target = self._target_path()
            _ = self._read_body()
        except DavHttpError as exc:
            self._send_error_text(exc.status, exc.message)
            return

        if target.exists():
            self._send_error_text(HTTPStatus.METHOD_NOT_ALLOWED, "resource already exists")
            return
        if not target.parent.is_dir():
            self._send_error_text(HTTPStatus.CONFLICT, "parent collection does not exist")
            return
        try:
            target.mkdir()
        except OSError as exc:
            self._send_error_text(HTTPStatus.INTERNAL_SERVER_ERROR, f"cannot create: {exc}")
            return
        self._send(HTTPStatus.CREATED, b"")

    # ------------------------------------------------------------------ DELETE

    def do_DELETE(self):  # noqa: N802
        if not self._require_auth():
            return
        try:
            target = self._target_path()
            _ = self._read_body()
        except DavHttpError as exc:
            self._send_error_text(exc.status, exc.message)
            return

        if target == self.server.root:
            self._send_error_text(HTTPStatus.FORBIDDEN, "cannot delete sync root")
            return
        if not target.exists():
            self._send_error_text(HTTPStatus.NOT_FOUND, "not found")
            return
        try:
            if target.is_dir():
                shutil.rmtree(target)
            else:
                target.unlink()
        except OSError as exc:
            self._send_error_text(HTTPStatus.INTERNAL_SERVER_ERROR, f"cannot delete: {exc}")
            return
        self._send(HTTPStatus.NO_CONTENT, b"")

    # ------------------------------------------------------------------ OPTIONS

    def do_OPTIONS(self):  # noqa: N802
        # 不强制鉴权：浏览器/客户端能力探测通常不带凭据。
        self._send(HTTPStatus.OK, b"", headers={
            "DAV": "1",
            "Allow": "OPTIONS, PROPFIND, GET, HEAD, PUT, MKCOL, DELETE",
            "MS-Author-Via": "DAV",
        })


class MockWebDavServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True

    def __init__(self, address, handler, *, root: Path, username: str, password: str, quiet: bool):
        super().__init__(address, handler)
        self.root = root
        self.username = username
        self.password = password
        self.quiet = quiet


def create_server(host: str = DEFAULT_HOST, port: int = DEFAULT_PORT, *, root: Path,
                  username: str = DEFAULT_USER, password: str = DEFAULT_PASSWORD,
                  quiet: bool = False) -> MockWebDavServer:
    root = Path(root).expanduser().resolve()
    root.mkdir(parents=True, exist_ok=True)
    return MockWebDavServer((host, port), MockWebDavHandler, root=root,
                            username=username, password=password, quiet=quiet)


def parse_args(argv=None):
    parser = argparse.ArgumentParser(
        description="最小 WebDAV mock 服务器（stj-media-viewer 自检用，勿暴露公网）")
    parser.add_argument("--host", default=DEFAULT_HOST,
                        help=f"监听地址（默认 {DEFAULT_HOST}，只绑本机；请勿用 0.0.0.0）")
    parser.add_argument("--port", type=int, default=DEFAULT_PORT,
                        help=f"监听端口（默认 {DEFAULT_PORT}，0 = 由系统分配）")
    parser.add_argument("--root", required=True,
                        help="同步根目录在磁盘上的位置（不存在会自动创建）")
    parser.add_argument("--user", default=DEFAULT_USER, help=f"Basic 用户名（默认 {DEFAULT_USER}）")
    parser.add_argument("--password", default=DEFAULT_PASSWORD,
                        help=f"Basic 密码（默认 {DEFAULT_PASSWORD}）")
    parser.add_argument("--quiet", action="store_true", help="不打印每个请求的访问日志")
    args = parser.parse_args(argv)
    if not 0 <= args.port <= 65535:
        parser.error("--port 必须在 0 ~ 65535 之间")
    if not args.user or not args.password:
        parser.error("--user / --password 不能为空")
    return args


def main(argv=None) -> int:
    args = parse_args(argv)
    try:
        server = create_server(args.host, args.port, root=Path(args.root),
                               username=args.user, password=args.password, quiet=args.quiet)
    except OSError as exc:
        print(f"[mock-webdav] 无法监听 {args.host}:{args.port} -> {exc}", file=sys.stderr)
        print("[mock-webdav] 端口被占用时可以换一个：--port 8899 / --port 0", file=sys.stderr)
        return 2

    host, port = server.server_address[:2]
    print("=" * 68)
    print(f" Mock WebDAV Server v{VERSION} 已启动")
    print(f"   Endpoint : http://{host}:{port}/")
    print(f"   Root     : {server.root}")
    print(f"   Auth     : {args.user} / {args.password}（Basic）")
    print("   请勿把本服务暴露到公网：Basic 是明文口令且没有 TLS")
    print("   Ctrl+C 停止")
    print("=" * 68, flush=True)
    try:
        server.serve_forever(poll_interval=0.2)
    except KeyboardInterrupt:
        print("\n[mock-webdav] 已停止", flush=True)
    finally:
        server.server_close()
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(130)