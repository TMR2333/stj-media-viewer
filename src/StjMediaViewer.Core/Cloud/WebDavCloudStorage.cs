using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using StjMediaViewer.Core.Abstractions;
using StjMediaViewer.Core.Models;
using StjMediaViewer.Core.Settings;

namespace StjMediaViewer.Core.Cloud;

/// <summary>
/// WebDAV 适配器：坚果云 / Nextcloud / ownCloud / 群晖 DSM / 威联通 QTS 等主流服务共用一套协议。
///
/// 实现要点（都是为了"各家服务器都能跑"而选的保守做法）：
/// <list type="bullet">
/// <item>鉴权走 HTTP Basic，交给 <see cref="SocketsHttpHandler.Credentials"/> 处理 challenge：
///    <c>PreAuthenticate = false</c>，第一次请求不带 Authorization，收到 401 + WWW-Authenticate 后由框架重投。</item>
/// <item>列目录只用 <c>PROPFIND + Depth: 1</c> 逐层下钻，不用 <c>Depth: infinity</c>（坚果云/群晖常拒绝或超时）。</item>
/// <item>路径按段 <see cref="Uri.EscapeDataString(string)"/> 编码，'/' 作为分隔符保留；PROPFIND 返回的 href 先 URL 解码再与同步根比较。</item>
/// <item>上传前逐级 MKCOL（405 = 已存在，视为成功），PUT 用 <see cref="FileStream"/> + <see cref="StreamContent"/> 并显式给 Content-Length，避免 chunked。</item>
/// <item>下载先落 <c>&lt;localPath&gt;.tmp</c> 再原子替换，杜绝断网留下半截文件。</item>
/// <item>不用 LOCK/UNLOCK：单机同步场景下不做多客户端互斥，服务端版本冲突交给上层策略。</item>
/// </list>
/// </summary>
public sealed class WebDavCloudStorage : ICloudStorage
{
    private const string DpapiPrefix = "dpapi:";
    private const int TimeoutSeconds = 100;
    private const int HttpMultiStatus = 207;
    private const int HttpMethodNotAllowed = 405;

    private static readonly HttpMethod PropFindMethod = new("PROPFIND");
    private static readonly HttpMethod MkColMethod = new("MKCOL");

    /// <summary>只申请三个属性：类型、大小、修改时间。属性越少兼容性越好。</summary>
    private const string PropFindBody =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<D:propfind xmlns:D=\"DAV:\"><D:prop>" +
        "<D:resourcetype/><D:getcontentlength/><D:getlastmodified/>" +
        "</D:prop></D:propfind>";

    private readonly CloudTarget _target;
    private readonly HttpClient _http;

    /// <summary>同步根的绝对 URI（已转义，末尾带 '/'）。</summary>
    private readonly string _rootAbsoluteUri = "";

    /// <summary>同步根在服务器上的路径（已 URL 解码，末尾带 '/'）。</summary>
    private readonly string _rootDecodedPath = "/";

    /// <summary>配置类问题（地址/密码）——不在构造时抛，留给 TestAsync 给出可读提示。</summary>
    private readonly string? _configError;

    /// <summary>本次会话内已经确认存在的目录，避免每个文件都重复 MKCOL。</summary>
    private readonly HashSet<string> _createdFolders = new(StringComparer.Ordinal);

    public WebDavCloudStorage(CloudTarget target)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));

        var handler = new SocketsHttpHandler
        {
            // WebDAV 服务器要求先 401 challenge 再带凭据；不要自己抢着加 Authorization 头。
            PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var password = ResolvePassword(target.WebDavPasswordProtected, out var passwordError);
        if (!string.IsNullOrEmpty(target.WebDavUsername))
            handler.Credentials = new NetworkCredential(target.WebDavUsername, password);

        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
        };

        try
        {
            if (string.IsNullOrWhiteSpace(target.WebDavEndpoint))
                throw new InvalidOperationException("没有配置 WebDAV 地址（例如 https://dav.jianguoyun.com/dav/）");

            var endpoint = target.WebDavEndpoint.Trim();
            if (!endpoint.EndsWith('/')) endpoint += "/";
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
                (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException($"WebDAV 地址格式不正确：{target.WebDavEndpoint}");

            var subSegments = SplitSegments(target.RemoteSubPath);
            var rootEscaped = endpoint;
            if (subSegments.Count > 0)
                rootEscaped += string.Join("/", subSegments.Select(Uri.EscapeDataString)) + "/";

            if (!Uri.TryCreate(rootEscaped, UriKind.Absolute, out var rootUri))
                throw new InvalidOperationException($"WebDAV 同步路径格式不正确：{target.RemoteSubPath}");

            _rootAbsoluteUri = rootUri.AbsoluteUri;
            if (!_rootAbsoluteUri.EndsWith('/')) _rootAbsoluteUri += "/";

            var decoded = Uri.UnescapeDataString(rootUri.AbsolutePath);
            if (!decoded.EndsWith('/')) decoded += "/";
            _rootDecodedPath = decoded;

            if (passwordError is not null) _configError = passwordError;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UriFormatException or ArgumentException)
        {
            _configError = ex.Message;
        }
        catch (Exception ex)
        {
            _configError = $"WebDAV 配置有误：{ex.Message}";
        }
    }

    public CloudProviderKind Kind => CloudProviderKind.WebDav;

    /// <summary>创建本适配器所用的目标配置（只读回显，便于日志/调试）。</summary>
    public CloudTarget Target => _target;

    // ==================================================================================
    //  连通性 / 鉴权自检
    // ==================================================================================

    /// <summary>
    /// 接口签名带 <c>out</c>（契约冻结），而 C# 的 async 方法不能有 out 参数，
    /// 所以这里同步等待内部异步实现。实现内部全部 <c>ConfigureAwait(false)</c>，不会死锁 UI 线程。
    /// 调用方取消时仍然直接抛 <see cref="OperationCanceledException"/>。
    /// </summary>
    public Task<bool> TestAsync(out string? error, CancellationToken ct = default)
    {
        try
        {
            var (ok, message) = TestCoreAsync(ct).GetAwaiter().GetResult();
            error = message;
            return Task.FromResult(ok);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    private async Task<(bool Ok, string? Error)> TestCoreAsync(CancellationToken ct)
    {
        if (_configError is not null) return (false, _configError);

        try
        {
            using var request = CreateRequest(PropFindMethod, _rootAbsoluteUri, depth: "0");
            request.Content = new StringContent(PropFindBody, Encoding.UTF8, "application/xml");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            var code = (int)response.StatusCode;
            if (code == HttpMultiStatus || (code >= 200 && code < 300)) return (true, null);

            return code switch
            {
                401 => (false, "用户名或密码不对"),
                403 => (false, "没有权限访问该同步目录（HTTP 403）"),
                404 => (false, "同步路径不存在（可在云端新建该文件夹）"),
                405 => (false, "服务器不支持 WebDAV（PROPFIND 返回 HTTP 405）"),
                507 => (false, "云端空间不足（HTTP 507）"),
                _ => (false, $"服务器返回 HTTP {code} {response.ReasonPhrase}"),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient.Timeout 触发（不是调用方取消）：给用户一句能看懂的话。
            return (false, $"连接超时（{TimeoutSeconds} 秒内没有响应）");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, "无法连接 WebDAV 服务器：" + DescribeException(ex));
        }
    }

    // ==================================================================================
    //  列目录
    // ==================================================================================

    public async Task<IReadOnlyList<CloudEntry>> ListAllAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        EnsureConfigured();
        try
        {
            return await ListAllCoreAsync(progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { throw; }
        catch (Exception ex) { throw Wrap("列出云端文件", null, ex); }
    }

    private async Task<IReadOnlyList<CloudEntry>> ListAllCoreAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var entries = new List<CloudEntry>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { _rootDecodedPath };
        var queue = new Queue<(string Uri, string RelativeDir)>();
        queue.Enqueue((_rootAbsoluteUri, ""));

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (directoryUri, relativeDir) = queue.Dequeue();
            progress?.Report("正在列出 " + (relativeDir.Length == 0 ? "/" : relativeDir));

            using var request = CreateRequest(PropFindMethod, directoryUri, depth: "1");
            request.Content = new StringContent(PropFindBody, Encoding.UTF8, "application/xml");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if ((int)response.StatusCode != HttpMultiStatus)
                throw await BuildHttpErrorAsync(response, "列出云端目录", relativeDir, ct).ConfigureAwait(false);

            var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            foreach (var item in ParseMultiStatus(xml))
            {
                var relativePath = ToRelativePath(item.DecodedPath);
                if (relativePath is null || relativePath.Length == 0) continue; // 同步根本身

                if (item.IsCollection)
                {
                    // 记录已访问目录，防御服务器返回异常 href 造成的无限下钻。
                    if (visited.Add(relativePath))
                        queue.Enqueue((CombineUri(_rootAbsoluteUri, relativePath, isDirectory: true), relativePath));
                }
                else
                {
                    entries.Add(new CloudEntry
                    {
                        RelativePath = relativePath,
                        SizeBytes = item.Size,
                        ModifiedUtc = item.Modified,
                        IsDirectory = false,
                    });
                }
            }
        }

        // 顺序稳定便于上层对账 / UI 展示（与文件系统枚举顺序无关）。
        entries.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return entries;
    }

    // ==================================================================================
    //  下载 / 上传 / 建目录
    // ==================================================================================

    public async Task DownloadAsync(string relativePath, string localPath, CancellationToken ct = default)
    {
        EnsureConfigured();
        var uri = BuildFileUri(relativePath);
        var fullLocalPath = Path.GetFullPath(localPath);
        var tmp = fullLocalPath + ".tmp";

        try
        {
            using var request = CreateRequest(HttpMethod.Get, uri);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw await BuildHttpErrorAsync(response, "下载云端文件", relativePath, ct).ConfigureAwait(false);

            var directory = Path.GetDirectoryName(fullLocalPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            try
            {
                await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                                 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await input.CopyToAsync(output, 128 * 1024, ct).ConfigureAwait(false);
                    await output.FlushAsync(ct).ConfigureAwait(false);
                }
                File.Move(tmp, fullLocalPath, overwrite: true);
            }
            catch
            {
                TryDelete(tmp);
                throw;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { throw; }
        catch (Exception ex) { throw Wrap("下载云端文件", relativePath, ex); }
    }

    public async Task UploadAsync(string localPath, string relativePath, CancellationToken ct = default)
    {
        EnsureConfigured();
        if (!File.Exists(localPath))
            throw new IOException($"本地文件不存在：{localPath}");

        try
        {
            // 先保证父目录逐级存在（PUT 不会自动建目录，坚果云/群晖都返回 409）。
            await EnsureFolderCoreAsync(GetParentRelative(relativePath), ct).ConfigureAwait(false);

            using var request = CreateRequest(HttpMethod.Put, BuildFileUri(relativePath));
            await using var file = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var content = new StreamContent(file);
            // 显式给 Content-Length：大文件走 chunked 时部分 WebDAV 网关会 411/截断。
            content.Headers.ContentLength = file.Length;
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content = content;

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw await BuildHttpErrorAsync(response, "上传文件", relativePath, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { throw; }
        catch (Exception ex) { throw Wrap("上传文件", relativePath, ex); }
    }

    private static readonly HttpMethod MoveMethod = new("MOVE");

    public async Task MoveAsync(string fromRelativePath, string toRelativePath, CancellationToken ct = default)
    {
        EnsureConfigured();
        if (string.IsNullOrWhiteSpace(fromRelativePath) || string.IsNullOrWhiteSpace(toRelativePath))
            throw new IOException("移动路径不能为空");
        if (string.Equals(NormalizeRelative(fromRelativePath), NormalizeRelative(toRelativePath), StringComparison.Ordinal))
            return;

        try
        {
            var fromUri = BuildFileUri(fromRelativePath);
            var toUri = BuildFileUri(toRelativePath);
            using var request = CreateRequest(MoveMethod, fromUri);
            request.Headers.TryAddWithoutValidation("Destination", toUri);
            request.Headers.TryAddWithoutValidation("Overwrite", "F");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return;

            var status = (int)response.StatusCode;
            // 412 = 目标已存在；409 = 目标父目录不存在
            throw status switch
            {
                412 => new IOException("目标已存在"),
                409 => new IOException("目标目录不存在（请先建目录）"),
                404 => new IOException("源文件不存在"),
                401 or 403 => new IOException("没有权限移动该文件"),
                405 => new IOException("服务器不支持 MOVE（WebDAV 不完整）"),
                _ => new IOException($"移动失败：HTTP {status}"),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { throw; }
        catch (Exception ex) { throw Wrap("移动云端文件", fromRelativePath, ex); }
    }

    private static string NormalizeRelative(string relative) =>
        relative.Replace('\\', '/').Trim('/');

    public async Task EnsureFolderAsync(string relativePath, CancellationToken ct = default)
    {
        EnsureConfigured();
        try
        {
            await EnsureFolderCoreAsync(relativePath, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { throw; }
        catch (Exception ex) { throw Wrap("创建云端目录", relativePath, ex); }
    }

    private async Task EnsureFolderCoreAsync(string? relativePath, CancellationToken ct)
    {
        var segments = SplitSegments(relativePath);
        if (segments.Count == 0) return; // 同步根本身

        var prefix = new StringBuilder();
        foreach (var segment in segments)
        {
            if (prefix.Length > 0) prefix.Append('/');
            prefix.Append(segment);
            var relative = prefix.ToString();
            if (_createdFolders.Contains(relative)) continue;

            ct.ThrowIfCancellationRequested();
            using var request = CreateRequest(MkColMethod, CombineUri(_rootAbsoluteUri, relative, isDirectory: true));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            var code = (int)response.StatusCode;
            // 201 = 建好；405 = 已存在（WebDAV 标准做法）；2xx 其余也当成功。
            if ((code >= 200 && code < 300) || code == HttpMethodNotAllowed)
            {
                _createdFolders.Add(relative);
                continue;
            }
            throw await BuildHttpErrorAsync(response, "创建云端目录", relative, ct).ConfigureAwait(false);
        }
    }

    // ==================================================================================
    //  PROPFIND multistatus 解析
    // ==================================================================================

    private sealed class DavItem
    {
        public string DecodedPath { get; set; } = "";
        public bool IsCollection { get; set; }
        public long Size { get; set; }
        public DateTimeOffset Modified { get; set; }
    }

    /// <summary>
    /// 解析 <c>D:multistatus</c>。按 LocalName 匹配（前缀可能是 D:/d:/lp1:，只认命名空间 DAV: 的元素名），
    /// 忽略非 2xx 的 propstat（不少服务器对集合的 getcontentlength 返回 404 propstat）。
    /// </summary>
    private static List<DavItem> ParseMultiStatus(string xml)
    {
        var items = new List<DavItem>();
        if (string.IsNullOrWhiteSpace(xml)) return items;

        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new IOException($"解析 WebDAV 响应失败：{ex.Message}", ex);
        }

        foreach (var response in document.Descendants().Where(e => e.Name.LocalName == "response"))
        {
            var href = response.Elements().FirstOrDefault(e => e.Name.LocalName == "href")?.Value?.Trim();
            if (string.IsNullOrEmpty(href)) continue;

            // response 级别的 status（服务器对不存在的子项会这么回）不是 2xx 就跳过。
            var responseStatus = ParseStatusCode(
                response.Elements().FirstOrDefault(e => e.Name.LocalName == "status")?.Value);
            if (responseStatus is int wholeStatus && (wholeStatus < 200 || wholeStatus >= 300)) continue;

            var item = new DavItem { DecodedPath = HrefToDecodedPath(href) };
            foreach (var propstat in response.Elements().Where(e => e.Name.LocalName == "propstat"))
            {
                var status = ParseStatusCode(
                    propstat.Elements().FirstOrDefault(e => e.Name.LocalName == "status")?.Value);
                if (status is int code && (code < 200 || code >= 300)) continue;

                var prop = propstat.Elements().FirstOrDefault(e => e.Name.LocalName == "prop");
                if (prop is null) continue;

                foreach (var element in prop.Elements())
                {
                    switch (element.Name.LocalName)
                    {
                        case "resourcetype":
                            if (element.Elements().Any(e => e.Name.LocalName == "collection")) item.IsCollection = true;
                            break;
                        case "getcontentlength":
                            if (long.TryParse(element.Value.Trim(), NumberStyles.Integer,
                                    CultureInfo.InvariantCulture, out var size))
                                item.Size = size;
                            break;
                        case "getlastmodified":
                            if (TryParseHttpDate(element.Value, out var modified)) item.Modified = modified;
                            break;
                    }
                }
            }

            // 没给 resourcetype 的服务器：目录 href 以 '/' 结尾。
            if (!item.IsCollection && href.EndsWith('/')) item.IsCollection = true;
            items.Add(item);
        }

        return items;
    }

    private static int? ParseStatusCode(string? statusLine)
    {
        if (string.IsNullOrWhiteSpace(statusLine)) return null;
        // "HTTP/1.1 200 OK"
        var parts = statusLine.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) &&
                code is >= 100 and < 600)
                return code;
        }
        return null;
    }

    private static bool TryParseHttpDate(string? value, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        // RFC 1123（HTTP-date），兼容时区名缺失等少量变体。
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result))
            return true;
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dateTime))
        {
            result = new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc));
            return true;
        }
        return false;
    }

    /// <summary>href → 服务器路径的已解码形式（统一以 '/' 开头）。</summary>
    private static string HrefToDecodedPath(string href)
    {
        var value = href.Trim();
        var cut = value.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0) value = value[..cut];

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
            value = absolute.AbsolutePath;

        if (!value.StartsWith('/')) value = "/" + value;
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    /// <summary>把服务器路径换算成相对同步根的路径（'/' 分隔、无前导斜杠）；不在根下返回 null。</summary>
    private string? ToRelativePath(string decodedPath)
    {
        var path = decodedPath;
        if (!path.StartsWith('/')) path = "/" + path;

        var root = _rootDecodedPath;
        if (string.Equals(path.TrimEnd('/'), root.TrimEnd('/'), StringComparison.Ordinal)) return "";
        if (!path.StartsWith(root, StringComparison.Ordinal)) return null;
        return path[root.Length..].Trim('/');
    }

    // ==================================================================================
    //  URI / 路径工具
    // ==================================================================================

    private static HttpRequestMessage CreateRequest(HttpMethod method, string absoluteUri, string? depth = null)
    {
        var request = new HttpRequestMessage(method, absoluteUri);
        if (depth is not null) request.Headers.TryAddWithoutValidation("Depth", depth);
        request.Headers.TryAddWithoutValidation("User-Agent", "StjMediaViewer/1.0 (WebDAV sync)");
        return request;
    }

    private string BuildFileUri(string relativePath)
    {
        var segments = SplitSegments(relativePath);
        if (segments.Count == 0) throw new IOException("相对路径不能为空");
        return _rootAbsoluteUri + string.Join("/", segments.Select(Uri.EscapeDataString));
    }

    private static string CombineUri(string rootAbsoluteUri, string relativePath, bool isDirectory)
    {
        var segments = SplitSegments(relativePath);
        var uri = rootAbsoluteUri + string.Join("/", segments.Select(Uri.EscapeDataString));
        if (isDirectory && !uri.EndsWith('/')) uri += "/";
        return uri;
    }

    /// <summary>
    /// 相对路径切段：统一 '/'、过滤空段、拒绝 '.' / '..'（防目录穿越）。
    /// </summary>
    private static List<string> SplitSegments(string? relativePath)
    {
        var segments = new List<string>();
        if (string.IsNullOrWhiteSpace(relativePath)) return segments;

        foreach (var raw in relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw is "." or "..") throw new IOException($"非法同步路径：{relativePath}");
            if (raw.Contains('\0')) throw new IOException($"非法同步路径：{relativePath}");
            segments.Add(raw);
        }
        return segments;
    }

    private static string GetParentRelative(string relativePath)
    {
        var normalized = (relativePath ?? "").Replace('\\', '/').Trim('/');
        var index = normalized.LastIndexOf('/');
        return index <= 0 ? "" : normalized[..index];
    }

    // ==================================================================================
    //  错误描述
    // ==================================================================================

    private void EnsureConfigured()
    {
        if (_configError is not null) throw new IOException(_configError);
        if (string.IsNullOrEmpty(_rootAbsoluteUri)) throw new IOException("WebDAV 适配器未初始化");
    }

    private static async Task<IOException> BuildHttpErrorAsync(
        HttpResponseMessage response, string action, string? relativePath, CancellationToken ct)
    {
        var code = (int)response.StatusCode;
        var where = string.IsNullOrEmpty(relativePath) ? "" : $"（{relativePath}）";
        var message = code switch
        {
            401 => $"{action}失败：用户名或密码不对{where}",
            403 => $"{action}失败：没有权限{where}",
            404 => $"{action}失败：同步路径不存在（可在云端新建该文件夹）{where}",
            405 => $"{action}失败：服务器不允许该操作{where}",
            409 => $"{action}失败：父目录不存在{where}",
            423 => $"{action}失败：文件被服务器锁定{where}",
            507 => $"{action}失败：云端空间不足{where}",
            _ => $"{action}失败：HTTP {code} {response.ReasonPhrase}{where}",
        };

        // 典型鉴权/路径错误没必要读 body；其它错误附一小段响应，方便定位是哪个网关在拦。
        if (code is not (401 or 403 or 404))
        {
            try
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(body))
                {
                    body = body.Trim().Replace('\r', ' ').Replace('\n', ' ');
                    if (body.Length > 300) body = body[..300] + "…";
                    message += "：" + body;
                }
            }
            catch
            {
                // 读错误响应体失败不影响主流程。
            }
        }

        return new IOException(message);
    }

    private static IOException Wrap(string action, string? relativePath, Exception ex)
    {
        var where = string.IsNullOrEmpty(relativePath) ? "" : $"（{relativePath}）";
        return new IOException($"{action}失败{where}：{DescribeException(ex)}", ex);
    }

    /// <summary>取最内层异常信息：SocketException/HttpRequestException 的外层消息往往很含糊。</summary>
    private static string DescribeException(Exception ex)
    {
        var current = ex;
        var message = ex.Message;
        while (current.InnerException is not null && !ReferenceEquals(current.InnerException, current))
        {
            current = current.InnerException;
            if (!string.IsNullOrWhiteSpace(current.Message)) message = current.Message;
        }
        return string.IsNullOrWhiteSpace(message) ? ex.GetType().Name : message;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 清理临时文件失败不掩盖原始异常。
        }
    }

    // ==================================================================================
    //  密码（DPAPI）
    // ==================================================================================

    /// <summary>
    /// 解出明文密码：契约里存的是 <c>dpapi:&lt;base64&gt;</c>；兼容旧数据里的裸密文与明文。
    /// 返回 null 表示"没有密码/解密失败"，错误信息写入 <paramref name="error"/>。
    /// </summary>
    private static string? ResolvePassword(string? packed, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(packed)) return null;

        var value = packed.Trim();
        string cipher;
        if (value.StartsWith(DpapiPrefix, StringComparison.OrdinalIgnoreCase))
            cipher = value[DpapiPrefix.Length..].Trim();
        else if (Dpapi.LooksLikeProtectedBlob(value))
            cipher = value;
        else
            return value; // 明文（DPAPI 不可用时的降级配置）

        var plain = Dpapi.Unprotect(cipher);
        if (plain is not null) return plain;

        // 不是有效 DPAPI 密文（例如非 Windows 环境或手写的假值）：按明文处理，别把用户挡在门外。
        if (!Dpapi.LooksLikeProtectedBlob(cipher)) return cipher;

        error = "WebDAV 密码解密失败（DPAPI）：请重新输入密码";
        return null;
    }

    // ==================================================================================
    //  生命周期
    // ==================================================================================

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}