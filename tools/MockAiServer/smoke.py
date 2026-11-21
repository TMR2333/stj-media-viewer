#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Mock AI Server 冒烟测试。

做真事：
  1. 用当前 Python 起一个 server.py 子进程（随机空闲端口，真实 HTTP）；
  2. 发一次带 1×1 JPEG data URI 的 `POST /v1/chat/completions`；
  3. 断言 OpenAI 响应形状 + 业务 JSON 结构 + 确定性（同一张图两次结果一致）；
  4. 顺带验证 models / json_schema / json_object / model 为空 400 / 未知路径 404 / 超大请求 413 / --fail-rate。

只依赖标准库，不读 Pillow、不碰真实网络。运行：

    python tools/MockAiServer/smoke.py

成功输出 `ALL CHECKS PASSED` 并返回码 0；失败打印具体断言与服务器 stderr 并返回 1。
"""

from __future__ import annotations

import base64
import json
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
SERVER = HERE / "server.py"

# 1×1 JPEG（红 / 蓝），base64 内联，保证不依赖 Pillow 等第三方库
IMG_JPEG_RED_B64 = (
    "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAABAAEDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD50ooor8MP9Uz/2Q=="
)
IMG_JPEG_BLUE_B64 = (
    "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAABAAEDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDz+iiiv7jP46P/2Q=="
)

REQUIRED_CLASSIFICATION_KEYS = {
    "theme", "tone", "keywords", "subject", "mood", "is_log", "quality", "confidence",
}
THEME_IDS = {
    "nature", "humanities", "city", "street", "portrait", "candid", "art", "architecture",
    "stilllife", "landscape", "wildlife", "night", "travel", "sport", "macro", "documentary",
    "product", "food", "other",
}
TONE_IDS = {
    "highkey", "lowkey", "midkey", "hard", "soft", "cool", "warm", "mono", "lowsat",
    "hisat", "tealorange", "vintage", "cinematic", "neon", "natural", "other",
}
JSON_SCHEMA = {
    "type": "json_schema",
    "json_schema": {
        "name": "photo_classification",
        "strict": True,
        "schema": {
            "type": "object",
            "properties": {
                "theme": {"type": "string"},
                "tone": {"type": "string"},
                "keywords": {"type": "array", "items": {"type": "string"}},
                "subject": {"type": "string"},
                "mood": {"type": "string"},
                "is_log": {"type": "boolean"},
                "quality": {"type": "number"},
                "confidence": {"type": "number"},
            },
            "required": sorted(REQUIRED_CLASSIFICATION_KEYS),
            "additionalProperties": False,
        },
    },
}

CHECKS = []


# --------------------------------------------------------------------------------------
# HTTP 工具
# --------------------------------------------------------------------------------------
def http_json(method: str, url: str, payload=None, timeout: float = 15.0):
    """返回 (status, body_dict)；HTTP 错误码也读 JSON body，不是抛异常。"""
    data = None
    headers = {"Accept": "application/json"}
    if payload is not None:
        data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        headers["Content-Type"] = "application/json"
    request = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            raw = response.read().decode("utf-8")
            return response.status, json.loads(raw) if raw else {}
    except urllib.error.HTTPError as exc:
        raw = exc.read().decode("utf-8", "replace")
        try:
            return exc.code, json.loads(raw)
        except json.JSONDecodeError:
            return exc.code, {"raw": raw}
    except (urllib.error.URLError, ConnectionResetError, TimeoutError, socket.timeout) as exc:
        return -1, {"transport_error": str(exc)}


def free_port() -> int:
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]
    finally:
        sock.close()


def wait_ready(base_url: str, timeout: float = 15.0) -> bool:
    deadline = time.time() + timeout
    while time.time() < deadline:
        status, _body = http_json("GET", base_url + "/health", timeout=1.0)
        if status == 200:
            return True
        time.sleep(0.1)
    return False


class ServerProcess:
    def __init__(self, extra_args=None):
        self.port = free_port()
        self.base_url = f"http://127.0.0.1:{self.port}"
        cmd = [sys.executable, str(SERVER), "--port", str(self.port), "--quiet"]
        cmd.extend(extra_args or [])
        self.proc = subprocess.Popen(
            cmd, cwd=str(HERE),
            stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            env={**__import__("os").environ, "PYTHONIOENCODING": "utf-8"},
        )

    def start(self) -> "ServerProcess":
        if not wait_ready(self.base_url):
            err = b""
            try:
                self.proc.terminate()
                _out, err = self.proc.communicate(timeout=3)
            except Exception:
                self.proc.kill()
            raise RuntimeError(f"server 未能启动（port={self.port}）: {err.decode('utf-8', 'replace')[-800:]}")
        return self

    def stop(self) -> None:
        if self.proc.poll() is None:
            self.proc.terminate()
            try:
                self.proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.proc.kill()
                self.proc.wait(timeout=5)


# --------------------------------------------------------------------------------------
# 请求构造 / 断言
# --------------------------------------------------------------------------------------
def image_data_uri(b64: str) -> str:
    return f"data:image/jpeg;base64,{b64}"


def chat_payload(b64: str, *, model: str = "mock-vision-1", response_format=None,
                 prompt: str = "请分析这张照片的主题、影调与关键词，输出 JSON。") -> dict:
    payload = {
        "model": model,
        "temperature": 0.2,
        "messages": [{
            "role": "user",
            "content": [
                {"type": "text", "text": prompt},
                {"type": "image_url", "image_url": {"url": image_data_uri(b64), "detail": "low"}},
            ],
        }],
    }
    if response_format is not None:
        payload["response_format"] = response_format
    return payload


def parse_content(body: dict) -> dict:
    content = body["choices"][0]["message"]["content"]
    assert isinstance(content, str), f"content 必须是 JSON 字符串，实际 {type(content).__name__}"
    return json.loads(content)


def check(name: str, func) -> None:
    try:
        detail = func()
        CHECKS.append(name)
        print(f"  [PASS] {name}" + (f" — {detail}" if detail else ""))
    except AssertionError as exc:
        print(f"  [FAIL] {name} — {exc}")
        raise
    except Exception as exc:  # noqa: BLE001
        print(f"  [FAIL] {name} — 意外异常 {type(exc).__name__}: {exc}")
        raise


# --------------------------------------------------------------------------------------
# 主流程
# --------------------------------------------------------------------------------------
def main() -> int:
    print("Mock AI Server smoke test")
    print(f"  python  : {sys.executable}")
    print(f"  server  : {SERVER}")
    server = ServerProcess(["--max-body-bytes", "4096"])
    fail_server = None
    try:
        server.start()
        print(f"  endpoint: {server.base_url}/v1\n")
        base = server.base_url

        # 1) GET /v1/models
        def t_models():
            status, body = http_json("GET", base + "/v1/models")
            assert status == 200, f"status={status}"
            assert body.get("object") == "list", "object 应为 list"
            ids = [m.get("id") for m in body.get("data", [])]
            assert "mock-vision-1" in ids, f"缺少 mock-vision-1，实际 {ids}"
            return f"models={ids}"
        check("GET /v1/models 返回模型列表", t_models)

        # 2) POST /v1/chat/completions（OpenAI 形状 + 业务 JSON 结构）
        first_content = None

        def t_chat_shape():
            nonlocal first_content
            status, body = http_json("POST", base + "/v1/chat/completions", chat_payload(IMG_JPEG_RED_B64))
            assert status == 200, f"status={status}, body={body}"
            assert body.get("object") == "chat.completion", "object 应为 chat.completion"
            assert body.get("id", "").startswith("chatcmpl-"), f"id 形状异常: {body.get('id')}"
            assert body.get("model") == "mock-vision-1", f"model 应回显: {body.get('model')}"
            choice = body["choices"][0]
            assert choice.get("index") == 0 and choice.get("finish_reason") == "stop"
            message = choice["message"]
            assert message.get("role") == "assistant", "message.role 应为 assistant"
            classification = parse_content(body)
            missing = REQUIRED_CLASSIFICATION_KEYS - classification.keys()
            assert not missing, f"分类 JSON 缺字段 {missing}"
            assert classification["theme"] in THEME_IDS, f"theme 非法: {classification['theme']}"
            assert classification["tone"] in TONE_IDS, f"tone 非法: {classification['tone']}"
            assert isinstance(classification["keywords"], list) and classification["keywords"], "keywords 应为非空数组"
            assert all(isinstance(k, str) for k in classification["keywords"]), "keywords 必须全是字符串"
            assert isinstance(classification["subject"], str) and classification["subject"], "subject 应为非空字符串"
            assert isinstance(classification["mood"], str) and classification["mood"], "mood 应为非空字符串"
            assert classification["is_log"] is False, "is_log 应为 false"
            assert 0.0 <= classification["quality"] <= 1.0, f"quality 越界: {classification['quality']}"
            assert 0.7 <= classification["confidence"] <= 0.95, f"confidence 越界: {classification['confidence']}"
            for key in ("prompt_tokens", "completion_tokens", "total_tokens"):
                assert isinstance(body["usage"].get(key), int), f"usage.{key} 应为 int"
            first_content = message["content"]
            return f"theme={classification['theme']}, tone={classification['tone']}, q={classification['quality']}"
        check("POST /v1/chat/completions 返回合法 OpenAI 结构 + 合法分类 JSON", t_chat_shape)

        # 3) 确定性：同一张图两次 content 完全一致
        def t_deterministic():
            for _ in range(2):
                status, body = http_json("POST", base + "/v1/chat/completions", chat_payload(IMG_JPEG_RED_B64))
                assert status == 200, f"status={status}"
                assert body["choices"][0]["message"]["content"] == first_content, "同一张图两次结果不一致"
            return "同一图片 3 次请求 content 完全一致"
        check("确定性：同一图片重复请求结果相同", t_deterministic)

        # 4) response_format=json_schema 兼容
        def t_json_schema():
            status, body = http_json("POST", base + "/v1/chat/completions",
                                     chat_payload(IMG_JPEG_RED_B64, response_format=JSON_SCHEMA))
            assert status == 200, f"status={status}"
            classification = parse_content(body)
            assert classification["theme"] in THEME_IDS and classification["tone"] in TONE_IDS
            assert body["choices"][0]["message"]["content"] == first_content, "json_schema 下结果应与普通请求一致"
            return f"json_schema OK ({classification['theme']}/{classification['tone']})"
        check("response_format=json_schema 兼容", t_json_schema)

        # 5) response_format=json_object + 不同图片得到不同结果
        def t_json_object_other_image():
            status, body = http_json("POST", base + "/v1/chat/completions",
                                     chat_payload(IMG_JPEG_BLUE_B64, response_format={"type": "json_object"}))
            assert status == 200, f"status={status}"
            classification = parse_content(body)
            assert classification["theme"] in THEME_IDS
            assert body["choices"][0]["message"]["content"] != first_content, "不同图片应产生不同结果"
            return f"json_object OK，另一张图 theme={classification['theme']}"
        check("响应格式 json_object + 不同图片结果不同", t_json_object_other_image)

        # 6) model 为空 -> 400
        def t_model_empty():
            status, body = http_json("POST", base + "/v1/chat/completions",
                                     chat_payload(IMG_JPEG_RED_B64, model=""))
            assert status == 400, f"status={status}"
            assert "error" in body and body["error"].get("message"), f"缺少错误体: {body}"
            return body["error"]["message"]
        check("model 为空返回 400", t_model_empty)

        # 7) 未知路径 -> 404
        def t_404():
            status, body = http_json("GET", base + "/v1/embeddings")
            assert status == 404, f"status={status}"
            assert body.get("error", {}).get("type") == "not_found_error", f"错误类型异常: {body}"
            return "404 not_found_error"
        check("未知路径返回 404", t_404)

        # 8) 超大请求 -> 413（server 启动时 --max-body-bytes 4096）
        def t_413():
            big = {"model": "mock-vision-1", "messages": [{"role": "user", "content": "x" * 9000}]}
            status, body = http_json("POST", base + "/v1/chat/completions", big)
            assert status == 413, f"status={status}, body={body}"
            assert body.get("error", {}).get("code") == "request_too_large", f"错误码异常: {body}"
            return "413 request_too_large"
        check("超长请求返回 413", t_413)

        # 9) --fail-rate 故障注入（429/500）
        def t_fail_rate():
            fail_server.start()
            req = chat_payload(IMG_JPEG_RED_B64)
            statuses = []
            for _ in range(3):
                status, body = http_json("POST", fail_server.base_url + "/v1/chat/completions", req)
                assert status in (429, 500), f"fail-rate=1.0 时应返回 429/500，实际 {status}: {body}"
                assert body.get("error", {}).get("message"), "故障响应应带 error.message"
                statuses.append(status)
            return f"fail-rate=1.0 返回 {statuses}"
        fail_server = ServerProcess(["--fail-rate", "1.0"])
        check("--fail-rate 故障注入（429/500 供重试测试）", t_fail_rate)

        print(f"\nALL CHECKS PASSED ({len(CHECKS)} 项)\n")
        return 0
    except AssertionError:
        return 1
    except Exception as exc:  # noqa: BLE001
        print(f"\nSMOKE FAILED: {type(exc).__name__}: {exc}")
        return 1
    finally:
        if fail_server is not None:
            fail_server.stop()
        server.stop()


if __name__ == "__main__":
    sys.exit(main())