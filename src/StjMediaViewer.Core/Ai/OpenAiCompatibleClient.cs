using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Settings;
using StjMediaViewer.Core.Util;

namespace StjMediaViewer.Core.Ai;

/// <summary>
/// OpenAI 兼容协议的底层客户端（OpenAI / Azure OpenAI / Ollama / 第三方网关）。
/// <para>POST <c>{BaseUrl}/chat/completions</c>，多模态 <c>image_url</c> + base64 data URI，
/// <c>response_format</c> 支持 json_schema → json_object → 纯文本三级自动回退，429/5xx/超时按
/// <see cref="AiSettings.MaxRetries"/> 指数退避重试。</para>
/// <para><b>任何异常都会转成 <see cref="AiRawResponse.Fail"/>，绝不向外抛。</b></para>
/// </summary>
public sealed class OpenAiCompatibleClient : IAiClient
{
    /// <summary>Azure 未显式配置 api-version 时使用的默认稳定版本。</summary>
    public const string AzureDefaultApiVersion = "2024-06-01";

    private const int MaxRawJsonChars = 1_000_000;

    private static readonly HttpClient SharedHttpClient = CreateSharedHttpClient();

    /// <summary>请求体序列化：中文直接输出 UTF-8（不转 \uXXXX），便于抓包调试。</summary>
    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _http;
    private readonly ISettingsService? _settingsService;

    /// <param name="settingsService">用于 DPAPI 解密 API Key、读取设置；可空（此时仅支持明文/ExtraHeaders 里的 Key）。</param>
    /// <param name="httpClient">可注入 HttpClient（测试/代理）；为空时使用进程内共享实例。</param>
    public OpenAiCompatibleClient(ISettingsService? settingsService = null, HttpClient? httpClient = null)
    {
        _settingsService = settingsService;
        _http = httpClient ?? SharedHttpClient;
    }

    private static HttpClient CreateSharedHttpClient()
    {
        // 超时由每次请求的 CancellationTokenSource 控制，故 HttpClient 不设超时。
        var client = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "StjMediaViewer/1.0 (+AI classification)");
        return client;
    }

    // ============================ CompleteAsync ============================

    public async Task<AiRawResponse> CompleteAsync(AiRequest request, AiSettings settings, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await CompleteCoreAsync(request, settings, stopwatch, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return AiRawResponse.Fail("AI 请求已取消或超时", 0);
        }
        catch (Exception ex)
        {
            Log.Warn("AI 请求出现未处理异常（已降级为失败响应）", ex);
            return AiRawResponse.Fail("AI 请求异常: " + ex.Message, 0);
        }
    }

    private async Task<AiRawResponse> CompleteCoreAsync(AiRequest request, AiSettings settings, Stopwatch stopwatch,
        CancellationToken ct)
    {
        settings ??= new AiSettings();
        request ??= new AiRequest();

        var model = ResolveModel(request.Model, settings);
        if (settings.DryRun) return DryRunComplete(request, model);

        if (request.Messages.Count == 0) return AiRawResponse.Fail("AI 请求消息为空");

        var endpoint = BuildEndpointUrl(settings);
        if (endpoint is null) return AiRawResponse.Fail("AI BaseUrl 未配置，请在设置中填写接口地址");

        var apiKey = ResolveApiKey(settings);
        if (apiKey is null && RequiresApiKey(settings))
        {
            return AiRawResponse.Fail(
                "AI API Key 未配置或解密失败（请在设置中保存 Key；本地模型可在 ExtraHeaders 里提供，或开启 DryRun）");
        }

        var maxRetries = Math.Clamp(settings.MaxRetries, 0, 8);
        var timeoutSeconds = Math.Clamp(settings.TimeoutSeconds <= 0 ? 90 : settings.TimeoutSeconds, 5, 600);
        var formatLevel = DetermineInitialFormatLevel(request, settings);

        string? lastError = null;
        var lastStatus = 0;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            var result = await SendOnceAsync(request, settings, endpoint, apiKey, model, formatLevel,
                timeoutSeconds, ct).ConfigureAwait(false);

            if (result.Response is { } response)
            {
                response.Latency = stopwatch.Elapsed;
                return response;
            }

            lastError = result.Error;
            lastStatus = result.Status;

            // response_format 不被支持：降级一级后立即重发，不消耗重试次数。
            if (result.FormatUnsupported && formatLevel < 2)
            {
                formatLevel++;
                Log.Info($"AI 服务端不支持当前 response_format（level {formatLevel - 1}），自动回退到 level {formatLevel}");
                attempt--;
                continue;
            }

            if (!result.Retryable || attempt >= maxRetries)
                return AiRawResponse.Fail(lastError ?? "AI 请求失败", lastStatus);

            Log.Info($"AI 请求失败（HTTP {lastStatus}），{attempt + 1}/{maxRetries} 次重试: {Truncate(lastError, 200)}");
            await DelayForRetryAsync(attempt, result.RetryAfter, ct).ConfigureAwait(false);
        }

        return AiRawResponse.Fail(lastError ?? "AI 请求失败（重试已用尽）", lastStatus);
    }

    private sealed record AttemptResult(
        AiRawResponse? Response,
        string? Error,
        int Status,
        bool Retryable,
        bool FormatUnsupported,
        TimeSpan? RetryAfter);

    private async Task<AttemptResult> SendOnceAsync(
        AiRequest request,
        AiSettings settings,
        string endpoint,
        string? apiKey,
        string model,
        int formatLevel,
        int timeoutSeconds,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        byte[] body;
        try
        {
            body = BuildRequestBody(request, settings, model, formatLevel);
        }
        catch (Exception ex)
        {
            return new AttemptResult(AiRawResponse.Fail("构造 AI 请求失败: " + ex.Message), ex.Message, 0, false, false, null);
        }

        using var httpRequest = BuildHttpRequest(endpoint, settings, apiKey, body);
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attemptCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            using var httpResponse = await _http
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token)
                .ConfigureAwait(false);

            var status = (int)httpResponse.StatusCode;
            string text;
            try
            {
                text = await httpResponse.Content.ReadAsStringAsync(attemptCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new AttemptResult(null, $"读取响应超时（{timeoutSeconds}s）", status, true, false, null);
            }

            if (httpResponse.IsSuccessStatusCode)
            {
                var parsed = ParseSuccessBody(text, model, status);
                if (!parsed.Success && formatLevel < 2 && request.RequireJson && IsEmptyContent(parsed.Error))
                {
                    // 空内容常见于部分网关不支持 json_schema（静默忽略约束）：降级再试。
                    return new AttemptResult(null, parsed.Error, status, true, true, null);
                }
                return new AttemptResult(parsed, parsed.Error, status, false, false, null);
            }

            var error = BuildHttpError(status, httpResponse.ReasonPhrase, text);
            var formatUnsupported = request.RequireJson && IsFormatUnsupported(status, text);
            var retryable = IsRetryableStatus(status);
            return new AttemptResult(null, error, status, retryable, formatUnsupported, GetRetryAfter(httpResponse));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new AttemptResult(null, $"AI 请求超时（{timeoutSeconds}s）", 0, true, false, null);
        }
        catch (HttpRequestException ex)
        {
            return new AttemptResult(null, "AI 网络请求失败: " + ex.Message, 0, true, false, null);
        }
        catch (Exception ex)
        {
            return new AttemptResult(null, "AI 请求异常: " + ex.Message, 0, false, false, null);
        }
    }

    private static bool IsEmptyContent(string? error) =>
        error is not null && (error.Contains("空内容", StringComparison.Ordinal) ||
                              error.Contains("empty", StringComparison.OrdinalIgnoreCase));

    private static bool IsRetryableStatus(int status) =>
        status is 408 or 409 or 425 or 429 or 500 or 502 or 503 or 504 or 529;

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        try
        {
            if (response.Headers.RetryAfter is { } header)
            {
                if (header.Delta is { } delta) return delta;
                if (header.Date is { } date)
                {
                    var wait = date - DateTimeOffset.UtcNow;
                    return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
                }
            }
        }
        catch { }
        return null;
    }

    private static async Task DelayForRetryAsync(int attempt, TimeSpan? retryAfter, CancellationToken ct)
    {
        double ms;
        if (retryAfter is { } ra)
        {
            ms = Math.Clamp(ra.TotalMilliseconds, 200, 20_000);
        }
        else
        {
            var exponential = 600.0 * Math.Pow(2, attempt);
            ms = Math.Min(8_000, exponential) + Random.Shared.Next(50, 350);
        }
        if (ms > 0) await Task.Delay(TimeSpan.FromMilliseconds(ms), ct).ConfigureAwait(false);
    }

    // ============================ TestConnectionAsync ============================

    public async Task<AiConnectionTestResult> TestConnectionAsync(AiSettings settings, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        settings ??= new AiSettings();
        try
        {
            if (settings.DryRun)
            {
                // DryRun 绝不联网：只做本地配置形状校验，返回假成功（SelfCheck 依赖这一点）。
                var modelEcho = string.IsNullOrWhiteSpace(settings.VisionModel) ? "(未设置模型)" : settings.VisionModel.Trim();
                var provider = AbstractionsName(settings);
                return new AiConnectionTestResult
                {
                    Success = true,
                    Message = $"DryRun 模式：未发起网络请求（Provider={provider}，BaseUrl={(string.IsNullOrWhiteSpace(settings.BaseUrl) ? "(未设置)" : settings.BaseUrl.Trim())}）",
                    ModelEcho = modelEcho,
                    LatencyMs = Math.Max(1, stopwatch.ElapsedMilliseconds),
                };
            }

            var request = new AiRequest
            {
                Model = ResolveModel(null, settings),
                RequireJson = false,
                Temperature = 0,
                MaxTokens = 24,
                Messages = { new AiChatMessage { Role = "user", Text = "ping，请只回复 pong" } },
            };

            var response = await CompleteAsync(request, settings, ct).ConfigureAwait(false);
            stopwatch.Stop();

            if (!response.Success)
            {
                return new AiConnectionTestResult
                {
                    Success = false,
                    Message = response.Error ?? "连接失败（未知原因）",
                    ModelEcho = response.Model,
                    LatencyMs = stopwatch.ElapsedMilliseconds,
                };
            }

            var reply = Truncate(response.Text, 80);
            return new AiConnectionTestResult
            {
                Success = true,
                Message = $"连接成功（{providerDisplay(settings)}），模型回复：{reply}",
                ModelEcho = string.IsNullOrWhiteSpace(response.Model) ? settings.VisionModel : response.Model,
                LatencyMs = response.Latency > TimeSpan.Zero ? (long)response.Latency.TotalMilliseconds : stopwatch.ElapsedMilliseconds,
            };
        }
        catch (Exception ex)
        {
            return new AiConnectionTestResult
            {
                Success = false,
                Message = "连接测试异常: " + ex.Message,
                LatencyMs = stopwatch.ElapsedMilliseconds,
            };
        }

        static string providerDisplay(AiSettings s) => AbstractionsName(s);
        static string AbstractionsName(AiSettings s) =>
            string.IsNullOrWhiteSpace(s.ProviderId) ? "openai" : s.ProviderId.Trim();
    }

    // ============================ 请求构造 ============================

    private static string ResolveModel(string? explicitModel, AiSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(explicitModel)) return explicitModel.Trim();
        if (!string.IsNullOrWhiteSpace(settings.VisionModel)) return settings.VisionModel.Trim();
        if (!string.IsNullOrWhiteSpace(settings.TextModel)) return settings.TextModel.Trim();
        return "gpt-4o-mini";
    }

    private static int DetermineInitialFormatLevel(AiRequest request, AiSettings settings)
    {
        if (!request.RequireJson) return 2;
        if (settings.UseJsonSchema && !string.IsNullOrWhiteSpace(request.JsonSchema)) return 0;
        return 1;
    }

    private static byte[] BuildRequestBody(AiRequest request, AiSettings settings, string model, int formatLevel)
    {
        var root = new JsonObject
        {
            ["model"] = model,
            ["stream"] = false,
        };

        var messages = new JsonArray();
        foreach (var message in request.Messages)
        {
            var role = string.IsNullOrWhiteSpace(message.Role) ? "user" : message.Role.Trim();
            var node = new JsonObject { ["role"] = role };

            if (message.ImageBytes is { Length: > 0 } && settings.VisionEnabled)
            {
                var parts = new JsonArray();
                if (!string.IsNullOrWhiteSpace(message.Text))
                    parts.Add(new JsonObject { ["type"] = "text", ["text"] = message.Text });

                var mime = string.IsNullOrWhiteSpace(message.ImageMime) ? "image/jpeg" : message.ImageMime.Trim();
                var dataUri = "data:" + mime + ";base64," + Convert.ToBase64String(message.ImageBytes);
                parts.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject
                    {
                        ["url"] = dataUri,
                        ["detail"] = "high",
                    },
                });
                node["content"] = parts;
            }
            else
            {
                node["content"] = message.Text ?? "";
            }
            messages.Add(node);
        }
        root["messages"] = messages;

        if (request.Temperature is { } temperature)
            root["temperature"] = Math.Round(Math.Clamp(temperature, 0, 2), 3);
        if (request.MaxTokens is > 0) root["max_tokens"] = request.MaxTokens.Value;

        if (formatLevel == 0 && !string.IsNullOrWhiteSpace(request.JsonSchema))
        {
            JsonNode? schema = null;
            try { schema = JsonNode.Parse(request.JsonSchema); } catch { schema = null; }
            if (schema is not null)
            {
                root["response_format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject
                    {
                        ["name"] = string.IsNullOrWhiteSpace(request.JsonSchemaName) ? PromptLibrary.SchemaName : request.JsonSchemaName,
                        ["strict"] = true,
                        ["schema"] = schema,
                    },
                };
            }
        }
        else if (formatLevel == 1)
        {
            root["response_format"] = new JsonObject { ["type"] = "json_object" };
        }

        return JsonSerializer.SerializeToUtf8Bytes(root, RequestJsonOptions);
    }

    private static HttpRequestMessage BuildHttpRequest(string endpoint, AiSettings settings, string? apiKey, byte[] body)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(body),
        };
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        httpRequest.Headers.TryAddWithoutValidation("Accept", "application/json");

        var authHeaderProvided = false;
        if (settings.ExtraHeaders is { Count: > 0 })
        {
            foreach (var (rawKey, value) in settings.ExtraHeaders)
            {
                if (string.IsNullOrWhiteSpace(rawKey) || string.IsNullOrWhiteSpace(value)) continue;
                var name = rawKey.Trim();
                // api-version 走 query（Azure），不重复作为 header。
                if (name.Equals("api-version", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;

                var added = httpRequest.Headers.TryAddWithoutValidation(name, value.Trim());
                if (!added && httpRequest.Content is not null)
                    httpRequest.Content.Headers.TryAddWithoutValidation(name, value.Trim());
                if (added && IsAuthHeaderName(name)) authHeaderProvided = true;
            }
        }

        if (!authHeaderProvided && !string.IsNullOrWhiteSpace(apiKey))
        {
            if (IsAzureProvider(settings))
                httpRequest.Headers.TryAddWithoutValidation("api-key", apiKey);
            else
                httpRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        }
        return httpRequest;
    }

    private static bool IsAuthHeaderName(string name) =>
        name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("api-key", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("x-api-key", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("x-goog-api-key", StringComparison.OrdinalIgnoreCase);

    private static string? BuildEndpointUrl(AiSettings settings)
    {
        var raw = settings.BaseUrl?.Trim();
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            raw = "https://" + raw.TrimStart('/');
        }

        var baseUrl = raw.TrimEnd('/');
        if (IsOllama(settings) && !baseUrl.Contains("/v1", StringComparison.OrdinalIgnoreCase))
            baseUrl += "/v1";

        var endpoint = baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : baseUrl + "/chat/completions";

        if (IsAzureProvider(settings) && !endpoint.Contains("api-version=", StringComparison.OrdinalIgnoreCase))
        {
            var version = AzureDefaultApiVersion;
            if (settings.ExtraHeaders is { Count: > 0 })
            {
                foreach (var (key, value) in settings.ExtraHeaders)
                {
                    if (key.Equals("api-version", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(value))
                    {
                        version = value.Trim();
                        break;
                    }
                }
            }
            var separator = endpoint.Contains('?') ? '&' : '?';
            endpoint += separator + "api-version=" + Uri.EscapeDataString(version);
        }

        return endpoint;
    }

    private static bool IsAzureProvider(AiSettings settings)
    {
        var provider = settings.ProviderId ?? "";
        if (provider.Contains("azure", StringComparison.OrdinalIgnoreCase)) return true;
        var baseUrl = settings.BaseUrl ?? "";
        return baseUrl.Contains("openai.azure.com", StringComparison.OrdinalIgnoreCase) ||
               baseUrl.Contains("/deployments/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOllama(AiSettings settings) =>
        string.Equals(settings.ProviderId, "ollama", StringComparison.OrdinalIgnoreCase) ||
        (settings.BaseUrl?.Contains(":11434", StringComparison.Ordinal) ?? false);

    private string? ResolveApiKey(AiSettings settings)
    {
        var packed = settings.ProtectedApiKey;
        if (string.IsNullOrWhiteSpace(packed)) return null;

        string? plain = null;
        try { plain = _settingsService?.UnprotectSecret(packed); }
        catch { }
        plain ??= Dpapi.Unprotect(packed);

        if (!string.IsNullOrWhiteSpace(plain)) return plain.Trim();

        // 用户可能直接把明文 Key 粘贴进 ProtectedApiKey；不像 DPAPI 密文就按明文使用。
        return Dpapi.LooksLikeProtectedBlob(packed) ? null : packed.Trim();
    }

    private static bool RequiresApiKey(AiSettings settings)
    {
        if (IsOllama(settings)) return false;
        var baseUrl = settings.BaseUrl ?? "";
        if (baseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
            baseUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            baseUrl.Contains("0.0.0.0", StringComparison.OrdinalIgnoreCase) ||
            baseUrl.Contains("192.168.", StringComparison.Ordinal))
            return false;
        if (settings.ExtraHeaders is { Count: > 0 } &&
            settings.ExtraHeaders.Keys.Any(IsAuthHeaderName))
            return false;
        return true;
    }

    // ============================ 响应解析 ============================

    private static AiRawResponse ParseSuccessBody(string body, string? requestedModel, int status)
    {
        if (string.IsNullOrWhiteSpace(body))
            return AiRawResponse.Fail("AI 服务端返回空响应体", status) is var fail
                ? new AiRawResponse
                {
                    Success = false,
                    Error = fail.Error,
                    HttpStatus = status,
                    Model = requestedModel,
                }
                : fail;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return new AiRawResponse
                {
                    Success = false,
                    Error = "AI 响应不是 JSON 对象",
                    HttpStatus = status,
                    Model = requestedModel,
                    RawJson = Truncate(body, MaxRawJsonChars),
                };

            if (root.TryGetProperty("error", out var errorNode) && !root.TryGetProperty("choices", out _))
            {
                return new AiRawResponse
                {
                    Success = false,
                    Error = ExtractErrorText(errorNode) ?? "AI 服务端返回 error",
                    HttpStatus = status,
                    Model = requestedModel,
                    RawJson = Truncate(body, MaxRawJsonChars),
                };
            }

            string? content = null;
            string? finishReason = null;
            if (root.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.ValueKind == JsonValueKind.Object)
                {
                    if (first.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
                    {
                        if (message.TryGetProperty("content", out var contentNode))
                            content = ReadContent(contentNode);
                    }
                    if (string.IsNullOrWhiteSpace(content) &&
                        first.TryGetProperty("text", out var legacyText) &&
                        legacyText.ValueKind == JsonValueKind.String)
                    {
                        content = legacyText.GetString();
                    }
                    if (first.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
                        finishReason = finish.GetString();
                }
            }

            var model = root.TryGetProperty("model", out var modelNode) && modelNode.ValueKind == JsonValueKind.String
                ? modelNode.GetString()
                : requestedModel;

            var promptTokens = 0;
            var completionTokens = 0;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                promptTokens = GetInt(usage, "prompt_tokens");
                completionTokens = GetInt(usage, "completion_tokens");
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                var hint = string.IsNullOrWhiteSpace(finishReason) ? "" : $"（finish_reason={finishReason}）";
                return new AiRawResponse
                {
                    Success = false,
                    Error = $"模型返回空内容{hint}",
                    HttpStatus = status,
                    RawJson = Truncate(body, MaxRawJsonChars),
                    Model = model,
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens,
                };
            }

            return new AiRawResponse
            {
                Success = true,
                Text = content.Trim(),
                HttpStatus = status,
                RawJson = Truncate(body, MaxRawJsonChars),
                Model = model,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
            };
        }
        catch (JsonException ex)
        {
            return new AiRawResponse
            {
                Success = false,
                Error = "AI 响应 JSON 解析失败: " + ex.Message,
                HttpStatus = status,
                RawJson = Truncate(body, MaxRawJsonChars),
                Model = requestedModel,
            };
        }
    }

    /// <summary>message.content 可能是字符串，也可能是 [{"type":"text","text":...}] 数组。</summary>
    private static string? ReadContent(JsonElement content)
    {
        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                return content.GetString();
            case JsonValueKind.Array:
            {
                var parts = new List<string>();
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.String)
                    {
                        parts.Add(part.GetString() ?? "");
                        continue;
                    }
                    if (part.ValueKind != JsonValueKind.Object) continue;
                    if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                        parts.Add(text.GetString() ?? "");
                    else if (part.TryGetProperty("content", out var inner))
                        parts.Add(ReadContent(inner) ?? "");
                }
                return string.Join("", parts);
            }
            case JsonValueKind.Object:
                if (content.TryGetProperty("text", out var objText) && objText.ValueKind == JsonValueKind.String)
                    return objText.GetString();
                if (content.TryGetProperty("content", out var objContent))
                    return ReadContent(objContent);
                return null;
            default:
                return null;
        }
    }

    private static int GetInt(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var node)) return 0;
        if (node.ValueKind == JsonValueKind.Number && node.TryGetInt32(out var value)) return value;
        if (node.ValueKind == JsonValueKind.String &&
            int.TryParse(node.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return 0;
    }

    private static string? ExtractErrorText(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String) return error.GetString();

        if (error.ValueKind == JsonValueKind.Object)
        {
            if (error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                return message.GetString();
            if (error.TryGetProperty("code", out var code))
            {
                var codeText = code.ValueKind == JsonValueKind.String ? code.GetString() : code.ToString();
                if (!string.IsNullOrWhiteSpace(codeText)) return "服务端错误码: " + codeText;
            }
            return Truncate(error.ToString(), 500);
        }

        if (error.ValueKind == JsonValueKind.Array && error.GetArrayLength() > 0)
            return ExtractErrorText(error[0]);

        return Truncate(error.ToString(), 500);
    }

    private static string BuildHttpError(int status, string? reason, string? body)
    {
        var detail = ExtractErrorFromBody(body);
        var baseMessage = status switch
        {
            401 => "API Key 无效或无权限（HTTP 401）",
            403 => "访问被拒绝（HTTP 403），请检查 Key 权限或模型访问策略",
            404 => "接口或模型不存在（HTTP 404），请检查 BaseUrl / 模型名 / Azure 部署名",
            429 => "请求过于频繁或额度用尽（HTTP 429）",
            >= 500 => $"AI 服务端错误（HTTP {status}）",
            _ => $"AI 请求失败（HTTP {status}{(string.IsNullOrWhiteSpace(reason) ? "" : " " + reason)}）",
        };
        return string.IsNullOrWhiteSpace(detail) ? baseMessage : baseMessage + "：" + detail;
    }

    private static string? ExtractErrorFromBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var error)) return ExtractErrorText(error);
                if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                    return message.GetString();
                if (root.TryGetProperty("detail", out var detail)) return ExtractErrorText(detail);
            }
        }
        catch { }
        return Truncate(body.Trim(), 300);
    }

    private static bool IsFormatUnsupported(int status, string? body)
    {
        if (status is not (400 or 404 or 422 or 500 or 501)) return false;
        if (string.IsNullOrWhiteSpace(body)) return false;
        var text = body.ToLowerInvariant();
        var mentionsFormat = text.Contains("response_format") || text.Contains("json_schema") ||
                             text.Contains("json schema") || text.Contains("structured output") ||
                             text.Contains("structured_output") || text.Contains("json_object") ||
                             text.Contains("json mode") || text.Contains("grammar");
        var mentionsUnsupported = text.Contains("unsupported") || text.Contains("not support") ||
                                  text.Contains("does not support") || text.Contains("unknown") ||
                                  text.Contains("invalid") || text.Contains("unrecognized") ||
                                  text.Contains("extra fields") || text.Contains("not allowed");
        return mentionsFormat && mentionsUnsupported;
    }

    // ============================ DryRun ============================

    private static AiRawResponse DryRunComplete(AiRequest request, string model)
    {
        var text = request.RequireJson ? PromptLibrary.FakeClassificationJson : PromptLibrary.FakeConnectionText;
        return new AiRawResponse
        {
            Success = true,
            Text = text,
            HttpStatus = 200,
            Model = model,
            Latency = TimeSpan.FromMilliseconds(Random.Shared.Next(8, 25)),
            PromptTokens = 128,
            CompletionTokens = 64,
            RawJson = "{\"dryRun\":true,\"note\":\"未发起网络请求，返回本地合成响应\"}",
        };
    }

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= max ? text : text[..max];
    }
}