#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Mock AI Server — OpenAI 兼容的本地 AI 分类服务（纯标准库，无网络依赖）。

用途：在没有真实 API Key / 外网的情况下，用**真实 HTTP 往返**验证
stj-media-viewer 的 AI 分类链路（OpenAiCompatibleClient → AiClassificationService）。

特点：
  * `POST /v1/chat/completions`：按请求里图片字节的 SHA-1 确定性伪造一份"合理"的分类 JSON，
    同一张图永远返回同一结果、不同图结果不同，方便 SelfCheck 做稳定断言。
  * 结构对齐 OpenAI：`choices[0].message.content` 是 JSON 字符串 + `usage`；
    兼容 `response_format={"type":"json_schema"}` / `{"type":"json_object"}` / 缺省。
  * `GET /v1/models`、`GET /health`；未知路径 404、model 为空 400、超大请求 413。
  * `--delay-ms` 模拟网络延迟，`--fail-rate` 随机返回 429/500 用来测客户端重试/退避。

启动：
    python tools/MockAiServer/server.py                    # 127.0.0.1:8787
    python tools/MockAiServer/server.py --port 8790 --delay-ms 300 --fail-rate 0.2

App 设置：BaseUrl = http://127.0.0.1:8787/v1 ，模型名填 mock-vision-1（任意非空均可），ApiKey 随便填。

注意：仅供本地测试，不校验鉴权、不处理并发写、不做任何真实推理。
"""

from __future__ import annotations

import argparse
import base64
import binascii
import hashlib
import json
import random
import re
import sys
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import unquote_to_bytes, urlparse

VERSION = "1.0.0"
DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 8787
DEFAULT_MAX_BODY_BYTES = 8 * 1024 * 1024  # 8 MiB，超过返回 413

MODEL_VISION = "mock-vision-1"
MODEL_TEXT = "mock-text-1"
MODEL_IDS = [MODEL_VISION, MODEL_TEXT]

# ---- 分类词表：稳定 ID 必须与 src/StjMediaViewer.Core/Models/Vocabularies.cs 一致 ----
THEMES = [
    "nature", "humanities", "city", "street", "portrait", "candid", "art",
    "architecture", "stilllife", "landscape", "wildlife", "night", "travel",
    "sport", "macro", "documentary", "product", "food", "other",
]
TONES = [
    "highkey", "lowkey", "midkey", "hard", "soft", "cool", "warm", "mono",
    "lowsat", "hisat", "tealorange", "vintage", "cinematic", "neon", "natural", "other",
]
MOODS = ["平静", "温暖", "孤寂", "热烈", "神秘", "轻盈", "怀旧", "克制", "辽阔", "喧闹"]
SUBJECTS = [
    "城市街头的光影与行人",
    "山野与天空的层次",
    "夜色中的街道与灯牌",
    "建筑立面的几何线条",
    "旅途中的风景记录",
    "晨光下的静物",
    "海边的浪与礁石",
    "室内暖光下的日常",
    "云层之上的航拍视角",
    "雪地里的逆光轮廓",
]
KEYWORD_POOL = [
    "城市", "夜景", "建筑", "街拍", "旅行", "自然", "光影", "蓝色调", "暖色",
    "构图", "倒影", "人物", "天空", "细节", "低饱和", "冷调", "胶片感", "氛围",
]
_REQUIRED_FIELDS = ("theme", "tone", "keywords", "subject", "mood", "is_log", "quality", "confidence")


# --------------------------------------------------------------------------------------
# 确定性伪造逻辑
# --------------------------------------------------------------------------------------
def extract_image_bytes(messages) -> bytes | None:
    """从 OpenAI messages 里取第一张图片的字节。

    兼容：
      * {"role":"user","content":[{"type":"image_url","image_url":{"url":"data:image/jpeg;base64,..."}}]}
      * {"type":"input_image","image_url":"data:..."}（Responses API 形状）
      * {"type":"image_url","image_url":"data:..."}
      * 纯文本里出现的 data URI（兜底扫描）
    """
    if isinstance(messages, list):
        for msg in messages:
            if not isinstance(msg, dict):
                continue
            content = msg.get("content")
            candidate_urls = []
            if isinstance(content, str):
                candidate_urls.append(content)
            elif isinstance(content, list):
                for part in content:
                    if not isinstance(part, dict):
                        continue
                    if part.get("type") not in (None, "image_url", "input_image", "image"):
                        continue
                    iu = part.get("image_url")
                    if isinstance(iu, dict):
                        candidate_urls.append(iu.get("url"))
                    elif isinstance(iu, str):
                        candidate_urls.append(iu)
                    inp = part.get("input_image")
                    if isinstance(inp, str):
                        candidate_urls.append(inp)
            data = msg.get("image_url")
            if isinstance(data, dict):
                candidate_urls.append(data.get("url"))
            elif isinstance(data, str):
                candidate_urls.append(data)
            for url in candidate_urls:
                raw = decode_data_uri(url)
                if raw is not None:
                    return raw
    return None


def decode_data_uri(url) -> bytes | None:
    """解码 data:[mime][;base64],payload；不是 data URI 返回 None。"""
    if not isinstance(url, str) or not url.startswith("data:"):
        return None
    head, sep, payload = url.partition(",")
    if not sep:
        return None
    try:
        if ";base64" in head.lower():
            cleaned = re.sub(r"\s+", "", payload)
            return base64.b64decode(cleaned, validate=False)
        return unquote_to_bytes(payload)
    except (binascii.Error, ValueError):
        return None


def scan_data_uri_in_text(text: str) -> bytes | None:
    """兜底：在原始请求文本里找 data:image/...;base64,...."""
    if not isinstance(text, str) or "data:image" not in text:
        return None
    match = re.search(r"data:image/[A-Za-z0-9.+-]+;base64,([A-Za-z0-9+/=\s]+)", text)
    if not match:
        return None
    try:
        return base64.b64decode(re.sub(r"\s+", "", match.group(1)), validate=False)
    except (binascii.Error, ValueError):
        return None


def fake_classification(image_bytes: bytes | None, fallback_text: str = "") -> tuple[dict, str]:
    """由图片字节 sha1 确定性生成分类结果；同一输入永远同一输出。"""
    if image_bytes:
        digest = hashlib.sha1(image_bytes).hexdigest()
    else:
        digest = hashlib.sha1(("NO_IMAGE:" + fallback_text).encode("utf-8")).hexdigest()
    rng = random.Random(int(digest[:16], 16))
    theme = THEMES[rng.randrange(len(THEMES))]
    tone = TONES[rng.randrange(len(TONES))]
    mood = MOODS[rng.randrange(len(MOODS))]
    subject = SUBJECTS[rng.randrange(len(SUBJECTS))]
    keywords = list(dict.fromkeys([theme, tone, *rng.sample(KEYWORD_POOL, 3)]))[:6]
    result = {
        "theme": theme,
        "tone": tone,
        "keywords": keywords,
        "subject": subject,
        "mood": mood,
        "is_log": False,
        "quality": round(0.55 + rng.random() * 0.44, 2),   # 0.55 ~ 0.99
        "confidence": round(0.70 + rng.random() * 0.25, 2),  # 0.70 ~ 0.95
    }
    return result, digest


def estimate_prompt_tokens(payload: dict) -> int:
    """粗略估算 prompt tokens：文本字符/4 + 每张 low-detail 图 85 tokens（不能把 base64 当字数）。"""
    chars = 0
    images = 0
    for msg in payload.get("messages") or []:
        if not isinstance(msg, dict):
            continue
        content = msg.get("content")
        if isinstance(content, str):
            chars += len(content)
        elif isinstance(content, list):
            for part in content:
                if not isinstance(part, dict):
                    continue
                if part.get("type") == "text":
                    chars += len(part.get("text") or "")
                elif part.get("type") in ("image_url", "input_image", "image"):
                    images += 1
        if msg.get("image_url") or msg.get("input_image"):
            images += 1
    return max(1, chars // 4 + images * 85 + 8)


def build_completion(payload: dict, result: dict, digest: str) -> dict:
    """组装 OpenAI `chat.completion` 响应。content 始终是 JSON 字符串。"""
    model = payload.get("model") or MODEL_VISION
    content = json.dumps(result, ensure_ascii=False, separators=(",", ":"))
    prompt_tokens = estimate_prompt_tokens(payload)
    completion_tokens = max(1, (len(content) + 3) // 4)
    return {
        "id": f"chatcmpl-mock-{digest[:16]}",
        "object": "chat.completion",
        "created": int(time.time()),
        "model": model,
        "choices": [
            {
                "index": 0,
                "message": {"role": "assistant", "content": content},
                "finish_reason": "stop",
                # 一些网关会在 choice 上回显 logprobs，OpenAI 默认是 null
                "logprobs": None,
            }
        ],
        "usage": {
            "prompt_tokens": prompt_tokens,
            "completion_tokens": completion_tokens,
            "total_tokens": prompt_tokens + completion_tokens,
        },
    }


def error_body(message: str, err_type: str = "invalid_request_error", code: str | None = None) -> dict:
    return {"error": {"message": message, "type": err_type, "param": None, "code": code}}


# --------------------------------------------------------------------------------------
# HTTP 服务
# --------------------------------------------------------------------------------------
class MockAiHandler(BaseHTTPRequestHandler):
    server_version = f"MockAiServer/{VERSION}"
    protocol_version = "HTTP/1.1"

    # ---- 基础输出 ----
    def _send_json(self, status: int, obj, extra_headers: dict | None = None, close: bool = False) -> None:
        body = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("X-Mock-Ai-Server", VERSION)
        for key, value in (extra_headers or {}).items():
            self.send_header(key, value)
        if close:
            self.send_header("Connection", "close")
            self.close_connection = True
        self.end_headers()
        try:
            self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError):
            pass

    def _send_error_json(self, status: int, message: str, err_type: str = "invalid_request_error",
                         code: str | None = None, close: bool = False) -> None:
        self._send_json(status, error_body(message, err_type, code), close=close)

    def log_message(self, fmt, *args):  # noqa: A003
        if getattr(self.server, "quiet", False):
            return
        sys.stderr.write("[mock-ai] %s - %s\n" % (self.address_string(), fmt % args))

    # ---- 路由 ----
    def do_GET(self):  # noqa: N802
        path = urlparse(self.path).path.rstrip("/") or "/"
        if path == "/health":
            self._send_json(200, {
                "status": "ok",
                "service": "mock-ai-server",
                "version": VERSION,
                "deterministic": True,
                "models": MODEL_IDS,
            })
        elif path == "/v1/models":
            self._send_json(200, {
                "object": "list",
                "data": [
                    {"id": model_id, "object": "model", "created": 0, "owned_by": "mock-ai-server"}
                    for model_id in MODEL_IDS
                ],
            })
        elif path.startswith("/v1/models/"):
            model_id = path[len("/v1/models/"):]
            if model_id in MODEL_IDS:
                self._send_json(200, {"id": model_id, "object": "model", "created": 0,
                                      "owned_by": "mock-ai-server"})
            else:
                self._send_error_json(404, f"model '{model_id}' not found", "not_found_error")
        else:
            self._send_error_json(404, f"unknown path: {path}", "not_found_error")

    def do_POST(self):  # noqa: N802
        path = urlparse(self.path).path.rstrip("/") or "/"
        if path != "/v1/chat/completions":
            self._send_error_json(404, f"unknown path: {path}", "not_found_error", close=True)
            return

        # 1) 读取并限制请求体（413 由 --max-body-bytes 控制）
        try:
            length = int(self.headers.get("Content-Length") or 0)
        except ValueError:
            length = 0
        max_body = int(getattr(self.server, "max_body_bytes", DEFAULT_MAX_BODY_BYTES))
        if length <= 0:
            self._send_error_json(400, "missing or empty request body", close=True)
            return
        if length > max_body:
            self._send_error_json(
                413, f"request body too large: {length} > {max_body} bytes",
                "invalid_request_error", code="request_too_large", close=True)
            return
        try:
            raw = self.rfile.read(length)
        except (ConnectionResetError, TimeoutError):
            return

        # 2) 故障注入（--fail-rate）：随机 429 / 500，方便测客户端重试与退避
        fail_rate = float(getattr(self.server, "fail_rate", 0.0))
        if fail_rate > 0 and random.random() < fail_rate:
            if random.random() < 0.5:
                self._send_json(429, error_body("mock transient rate limit", "rate_limit_error",
                                                code="rate_limit_exceeded"),
                                extra_headers={"Retry-After": "1"})
            else:
                self._send_json(500, error_body("mock transient server error", "server_error"))
            return

        # 3) 解析 JSON
        try:
            payload = json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            self._send_error_json(400, f"invalid JSON body: {exc}")
            return
        if not isinstance(payload, dict):
            self._send_error_json(400, "request body must be a JSON object")
            return

        # 4) model 校验：空/缺省/非字符串 -> 400
        model = payload.get("model")
        if not isinstance(model, str) or not model.strip():
            self._send_error_json(400, "model is required and must be a non-empty string",
                                  code="model_required")
            return
        messages = payload.get("messages")
        if not isinstance(messages, list) or not messages:
            self._send_error_json(400, "messages must be a non-empty array")
            return

        # 5) 可选延迟（模拟真实 API 网络/推理耗时）
        delay_ms = int(getattr(self.server, "delay_ms", 0))
        if delay_ms > 0:
            time.sleep(delay_ms / 1000.0)

        # 6) 取图 -> 确定性伪造
        image_bytes = extract_image_bytes(messages)
        if image_bytes is None:
            image_bytes = scan_data_uri_in_text(raw.decode("utf-8", "replace"))
        result, digest = fake_classification(image_bytes, fallback_text=json.dumps(payload, ensure_ascii=False))
        self._send_json(200, build_completion(payload, result, digest))


def create_server(host: str = DEFAULT_HOST, port: int = DEFAULT_PORT, *,
                  delay_ms: int = 0, fail_rate: float = 0.0,
                  max_body_bytes: int = DEFAULT_MAX_BODY_BYTES,
                  quiet: bool = False) -> ThreadingHTTPServer:
    """创建（未启动的）服务器；允许 port=0 让系统分配空闲端口。"""
    server = ThreadingHTTPServer((host, port), MockAiHandler)
    server.daemon_threads = True
    server.delay_ms = delay_ms
    server.fail_rate = fail_rate
    server.max_body_bytes = max_body_bytes
    server.quiet = quiet
    return server


def parse_args(argv=None):
    parser = argparse.ArgumentParser(description="OpenAI 兼容的本地 Mock AI 服务器（stj-media-viewer 测试用）")
    parser.add_argument("--host", default=DEFAULT_HOST, help=f"监听地址（默认 {DEFAULT_HOST}，只绑本机）")
    parser.add_argument("--port", type=int, default=DEFAULT_PORT, help=f"监听端口（默认 {DEFAULT_PORT}，0=随机）")
    parser.add_argument("--delay-ms", type=int, default=0, help="每个 chat 请求的模拟延迟（毫秒）")
    parser.add_argument("--fail-rate", type=float, default=0.0,
                        help="0.0~1.0，随机返回 429/500 的比例（默认 0，用于测重试/退避）")
    parser.add_argument("--max-body-bytes", type=int, default=DEFAULT_MAX_BODY_BYTES,
                        help=f"请求体上限，超过返回 413（默认 {DEFAULT_MAX_BODY_BYTES}）")
    parser.add_argument("--quiet", action="store_true", help="不打印每个请求的访问日志")
    args = parser.parse_args(argv)
    if not 0.0 <= args.fail_rate <= 1.0:
        parser.error("--fail-rate 必须在 0.0 ~ 1.0 之间")
    if args.delay_ms < 0:
        parser.error("--delay-ms 不能为负数")
    return args


def main(argv=None) -> int:
    args = parse_args(argv)
    try:
        server = create_server(args.host, args.port, delay_ms=args.delay_ms,
                               fail_rate=args.fail_rate, max_body_bytes=args.max_body_bytes,
                               quiet=args.quiet)
    except OSError as exc:
        print(f"[mock-ai] 无法监听 {args.host}:{args.port} -> {exc}", file=sys.stderr)
        print("[mock-ai] 端口被占用时可以换一个：--port 8790", file=sys.stderr)
        return 2
    host, port = server.server_address[:2]
    base = f"http://{host}:{port}/v1"
    print("=" * 66)
    print(f" Mock AI Server v{VERSION} 已启动")
    print(f"   BaseUrl : {base}")
    print(f"   Models  : {', '.join(MODEL_IDS)}")
    print(f"   Chat    : POST {base}/chat/completions")
    print(f"   Health  : http://{host}:{port}/health")
    print(f"   delay-ms: {args.delay_ms}   fail-rate: {args.fail_rate:.2f}"
          f"   max-body: {args.max_body_bytes} bytes")
    print("   App 设置：BaseUrl 填上面的 BaseUrl，模型名填 mock-vision-1，ApiKey 随便填")
    print("   Ctrl+C 停止")
    print("=" * 66, flush=True)
    try:
        server.serve_forever(poll_interval=0.2)
    except KeyboardInterrupt:
        print("\n[mock-ai] 已停止", flush=True)
    finally:
        server.server_close()
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(130)