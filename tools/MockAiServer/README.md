# MockAiServer — 本地 OpenAI 兼容 AI 服务（测试用）

给 **stj-media-viewer** 的 AI 分类链路提供一个"不用真实 API Key、不连外网"的端到端验证服务：
真实 HTTP 往返、真实 OpenAI 响应结构、**同一张图永远返回同一分类结果**，方便 SelfCheck / 手动测试做稳定断言。

- 代码：`tools/MockAiServer/server.py`（纯 Python 标准库，无第三方依赖）
- 冒烟：`tools/MockAiServer/smoke.py`（起进程 → 真 HTTP → 断言 → 打印 PASS）
- 归属：`tools/MockAiServer/`（tooling）

> 只监听 `127.0.0.1`，不做鉴权、不做真实推理，仅用于本地测试，不要暴露到公网。

---

## 1. 快速开始

```powershell
# 默认 127.0.0.1:8787
python tools/MockAiServer/server.py

# 自定义端口 / 模拟延迟 / 20% 故障率（测重试）
python tools/MockAiServer/server.py --port 8790 --delay-ms 300 --fail-rate 0.2
```

启动后输出：

```
==================================================================
 Mock AI Server v1.0.0 已启动
   BaseUrl : http://127.0.0.1:8787/v1
   Models  : mock-vision-1, mock-text-1
   Chat    : POST http://127.0.0.1:8787/v1/chat/completions
   Health  : http://127.0.0.1:8787/health
   App 设置：BaseUrl 填上面的 BaseUrl，模型名填 mock-vision-1，ApiKey 随便填
==================================================================
```

### 在 App 设置里怎么填

| 设置项 | 值 |
|---|---|
| Provider / 协议 | OpenAI 兼容（OpenAiCompatibleClient） |
| **BaseUrl** | `http://127.0.0.1:8787/v1` |
| **ApiKey** | 任意非空字符串（如 `sk-mock`；服务器不校验） |
| **模型名** | `mock-vision-1`（或 `mock-text-1`；任意非空字符串都接受） |
| response_format | 建议开 `json_schema`（服务器两种都支持，见下） |

> 客户端请求会打到 `{BaseUrl}/chat/completions`，所以 BaseUrl 末尾必须带 `/v1`。

### 冒烟测试

```powershell
python tools/MockAiServer/smoke.py
# ...
# ALL CHECKS PASSED (9 项)
```

`smoke.py` 会自己起一个随机端口的 server 子进程，覆盖：models、OpenAI 响应形状、分类 JSON 结构、
确定性（同一图 3 次一致）、`json_schema`、`json_object`、`model` 为空 400、未知路径 404、
超大请求 413、`--fail-rate` 429/500。全部通过才返回 0。

---

## 2. 端点

| Method | Path | 说明 |
|---|---|---|
| `POST` | `/v1/chat/completions` | OpenAI Chat Completions（视觉分类） |
| `GET` | `/v1/models` | `{"object":"list","data":[{"id":"mock-vision-1",...},{"id":"mock-text-1",...}]}` |
| `GET` | `/v1/models/{id}` | 单模型信息；未知 id → 404 |
| `GET` | `/health` | 存活探针：`{"status":"ok","version":"1.0.0","deterministic":true}` |

错误统一用 OpenAI 形状：

```json
{"error": {"message": "...", "type": "invalid_request_error", "param": null, "code": null}}
```

| 场景 | 状态码 |
|---|---|
| `model` 缺失 / 空字符串 / 非字符串 | `400`（`code=model_required`） |
| `messages` 不是非空数组 | `400` |
| body 不是合法 JSON | `400` |
| 未知路径 | `404`（`type=not_found_error`） |
| 请求体超过 `--max-body-bytes`（默认 8 MiB） | `413`（`code=request_too_large`） |
| `--fail-rate` 命中故障注入 | `429`（`type=rate_limit_error`，带 `Retry-After: 1`）或 `500`（`type=server_error`） |

### `POST /v1/chat/completions`

请求（与 OpenAI 多模态完全一致，`image_url` 里放 data URI）：

```json
{
  "model": "mock-vision-1",
  "temperature": 0.2,
  "response_format": {"type": "json_schema", "json_schema": {"name": "photo_classification", "strict": true, "schema": {"...": "..."}}},
  "messages": [
    {
      "role": "user",
      "content": [
        {"type": "text", "text": "请分析这张照片的主题、影调与关键词，输出 JSON。"},
        {"type": "image_url", "image_url": {"url": "data:image/jpeg;base64,/9j/4AAQ...", "detail": "low"}}
      ]
    }
  ]
}
```

响应（`choices[0].message.content` 是 **JSON 字符串**，`usage` 齐全）：

```json
{
  "id": "chatcmpl-mock-1eb3b0bf4e0e8d77",
  "object": "chat.completion",
  "created": 1763000000,
  "model": "mock-vision-1",
  "choices": [{
    "index": 0,
    "message": {"role": "assistant", "content": "{\"theme\":\"night\",\"tone\":\"lowkey\",\"keywords\":[\"night\",\"lowkey\",\"城市\",\"光影\",\"低饱和\"],\"subject\":\"夜色中的街道与灯牌\",\"mood\":\"神秘\",\"is_log\":false,\"quality\":0.83,\"confidence\":0.91}"},
    "finish_reason": "stop",
    "logprobs": null
  }],
  "usage": {"prompt_tokens": 93, "completion_tokens": 42, "total_tokens": 135}
}
```

`content` 里的分类字段（**严格这 8 个**，便于 `additionalProperties=false` 的 json_schema 校验）：

| 字段 | 类型 | 取值 |
|---|---|---|
| `theme` | string | `Vocabularies.Themes` 稳定 ID：nature/humanities/city/street/portrait/candid/art/architecture/stilllife/landscape/wildlife/night/travel/sport/macro/documentary/product/food/other |
| `tone` | string | `Vocabularies.Tones` 稳定 ID：highkey/lowkey/midkey/hard/soft/cool/warm/mono/lowsat/hisat/tealorange/vintage/cinematic/neon/natural/other |
| `keywords` | string[] | 非空，主题/影调 ID + 中文关键词混合 |
| `subject` | string | 一句话内容描述 |
| `mood` | string | 情绪词 |
| `is_log` | boolean | 恒 `false`（图片分类场景） |
| `quality` | number | `0.55 ~ 0.99` |
| `confidence` | number | `0.70 ~ 0.95` |

### 确定性（断言关键）

- 服务器对请求里的**图片字节**取 `sha1`，用 `sha1[:16]` 作为伪随机种子生成分类结果。
- **同一张图片 ⇒ 永远同一份分类 JSON**（跨请求、跨重启、跨进程都一致）；
  不同图片 ⇒ 结果不同；图片识别不到时退化用 `"NO_IMAGE:"+messages` 的 sha1。
- 响应 `id` 形如 `chatcmpl-mock-<sha1[:16]>`，可以直接从响应里核对图片指纹。
- 注意：`created` 是当前时间戳，测试请勿把整个响应体做等值比较，只比较 `message.content`。

### `response_format`

三种都支持，返回的 `message.content` 始终是可解析的 JSON 字符串：

| 请求 | 行为 |
|---|---|
| `{"type":"json_schema", "json_schema": {...}}` | 200，content 为符合字段约定的 JSON 字符串（不校验你传的 schema 细节） |
| `{"type":"json_object"}` | 200，同上 |
| 不传 | 200，同上（客户端按纯文本解析也能 `json.loads`） |

这正好用来验证客户端的"json_schema → json_object → 纯文本"降级链：三种请求都能成功，
可以分别断言三种分支都拿到了合法 JSON。

---

## 3. 命令行参数

| 参数 | 默认 | 说明 |
|---|---|---|
| `--host` | `127.0.0.1` | 监听地址（默认只绑本机） |
| `--port` | `8787` | 监听端口；`0` = 系统分配空闲端口（smoke 用） |
| `--delay-ms` | `0` | 每个 chat 请求的模拟延迟（毫秒），用于测客户端超时/耗时展示 |
| `--fail-rate` | `0.0` | `0~1`，每个 chat 请求按概率随机返回 429 或 500，用于测重试与指数退避 |
| `--max-body-bytes` | `8388608` | 请求体上限，超过返回 413（smoke 用 4096 测 413） |
| `--quiet` | 关 | 不打印逐请求访问日志 |

常见测试姿势：

```powershell
# 1) 测重试：客户端 MaxRetries=3 时，--fail-rate 0.2 下最终应成功
python tools/MockAiServer/server.py --fail-rate 0.2

# 2) 测超时：server 每次拖 5s，客户端超时设 2s -> 应返回 Fail 而不是崩溃
python tools/MockAiServer/server.py --delay-ms 5000

# 3) 测 413：故意发超大 base64
python tools/MockAiServer/server.py --max-body-bytes 65536
```

---

## 4. 用 curl / PowerShell 手动验证

```powershell
# 健康 + 模型
curl.exe http://127.0.0.1:8787/health
curl.exe http://127.0.0.1:8787/v1/models

# 发一次分类请求（把 <BASE64> 换成任意 1x1 JPEG 的 base64；
# tools/MockAiServer/smoke.py 里有可直接复制的常量）
curl.exe -X POST http://127.0.0.1:8787/v1/chat/completions `
  -H "Content-Type: application/json" `
  -d '{\"model\":\"mock-vision-1\",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"classify\"},{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/jpeg;base64,<BASE64>\"}}]}]}'
```

PowerShell 里引号转义较麻烦时，用 Python 一行发请求更省事：

```powershell
python -c "import base64,json,urllib.request,pathlib; img=next(pathlib.Path('tools/SampleLibrary/out/library').rglob('*.JPG')).read_bytes(); body=json.dumps({'model':'mock-vision-1','messages':[{'role':'user','content':[{'type':'image_url','image_url':{'url':'data:image/jpeg;base64,'+base64.b64encode(img).decode()}}]}]}).encode(); print(urllib.request.urlopen(urllib.request.Request('http://127.0.0.1:8787/v1/chat/completions',body,{'Content-Type':'application/json'})).read().decode())"
```

---

## 5. 与 SelfCheck / App 的配合

- **SelfCheck 里做真 HTTP 验证**：用进程内 `subprocess`（或 `MockAiServer` 类）起 server，
  创建 `OpenAiCompatibleClient(BaseUrl="http://127.0.0.1:<port>/v1", ApiKey="sk-mock", Model="mock-vision-1")`，
  对 `tools/SampleLibrary/out/library` 里的 JPEG/RAW 预览跑 `RunBatchAsync`，断言：
  - 成功数 == 输入数、`AiSuggestion.Theme/Tone` 落在词表内、`Confidence` 在 0.7~0.95；
  - 同一图片两次批量运行结果一致（确定性）；
  - 打开 `--fail-rate` 时验证重试与 `Failed` 统计、不抛异常；
  - 人工 Manual 结果不被覆盖（这个与 mock 无关，但可以放在同一条批量链路里验）。
- **手动验证 App**：按第 1 节填设置 → "测试连接"应显示成功与延迟 → 选几张图跑 AI 分类 →
  重新加载后分类/关键词/评分仍在。
- **不需要真实 Key**：ApiKey 随便填；服务器不校验 Authorization 头。

---

## 6. 限制与排障

- 不是真实 AI：只按 sha1 编结果，**别拿它的分类质量做功能验收**，只用来验证链路/协议/重试/写回。
- 单进程 `ThreadingHTTPServer`：多并发 OK，但没有持久化状态、没有鉴权、不校验请求 schema。
- 若 `--port` 被占用：换端口（`--port 8790`），或先 `netstat -ano | findstr :8787` 找占用进程。
- `smoke.py` 用 `sys.executable` 启子进程：用哪个 Python 跑 smoke，就用哪个 Python 起服务；
  本仓库约定 Python 为 `C:\Users\Administrator\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe`。
- 确定性依赖 `hashlib.sha1` + `random.Random(int)`：同一 CPython 大版本内稳定；
  跨大版本 `random` 采样实现若有变化，理论上结果可能变（不影响结构断言，只影响"固定 theme"这类断言）。