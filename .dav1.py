import io

# ============ 1) WebDAV PUT：带 Expect: 100-continue（123pan 必须），失败自动回退 ============
p = 'src/StjMediaViewer.Core/Cloud/WebDavCloudStorage.cs'
s = io.open(p, encoding='utf-8').read()
old = '''            using var request = CreateRequest(HttpMethod.Put, BuildFileUri(relativePath));
            await using var file = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var content = new StreamContent(file);
            // 显式给 Content-Length：大文件走 chunked 时部分 WebDAV 网关会 411/截断。
            content.Headers.ContentLength = file.Length;
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content = content;

            using var response = await SendLoggedAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw await BuildHttpErrorAsync(response, "上传文件", relativePath, ct).ConfigureAwait(false);'''
new = '''            // 123pan 的 WebDAV 网关对没有 Expect: 100-continue 的 PUT 会直接断开连接
            // （小文件也许侥幸成功，JPG/RAW 这种几 MB 起的一律 "Response ended prematurely"）。
            // 先带 Expect 发；服务器若明确拒绝（417）再回退成普通请求，兼容老服务器。
            var uploaded = await PutOnceAsync(localPath, relativePath, expectContinue: true, ct).ConfigureAwait(false);
            if (uploaded is null)
                return;

            if (uploaded.StatusCode == HttpStatusCode.ExpectationFailed)
            {
                uploaded.Dispose();
                uploaded = await PutOnceAsync(localPath, relativePath, expectContinue: false, ct).ConfigureAwait(false);
                if (uploaded is null) return;
            }

            using (uploaded)
            {
                if (!uploaded.IsSuccessStatusCode)
                    throw await BuildHttpErrorAsync(uploaded, "上传文件", relativePath, ct).ConfigureAwait(false);
            }'''
assert old in s
s = s.replace(old, new, 1)

anchor = '''    /// <summary>
    /// 统一发请求并记录一行 [webdav] 日志：方法 / 完整 URL / 状态码 / 耗时 / 上传字节数。'''
add = '''    /// <summary>
    /// 发一次 PUT。返回 null 表示"带 Expect 被对端在响应前断开"（部分服务器不认 100-continue），
    /// 调用方可以决定是否回退；其它异常照常抛。
    /// </summary>
    private async Task<HttpResponseMessage?> PutOnceAsync(
        string localPath, string relativePath, bool expectContinue, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Put, BuildFileUri(relativePath));
        await using var file = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var content = new StreamContent(file);
        // 显式给 Content-Length：大文件走 chunked 时部分 WebDAV 网关会 411/截断。
        content.Headers.ContentLength = file.Length;
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content = content;
        if (expectContinue) request.Headers.ExpectContinue = true;

        try
        {
            return await SendLoggedAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (expectContinue)
        {
            // 对端在 100-continue 阶段直接断开：回退成普通 PUT 再试一次
            Log.Warn($"[webdav] PUT 带 Expect 失败，回退重试：{Path.GetFileName(localPath)}（{ex.Message}）");
            return null;
        }
    }

''' + anchor
assert anchor in s
s = s.replace(anchor, add, 1)
io.open(p, 'w', encoding='utf-8').write(s)
print('webdav expect-continue')

# ============ 2) ICloudSyncService：按筛选上传 ============
p = 'src/StjMediaViewer.Core/Abstractions/Interfaces.cs'
s = io.open(p, encoding='utf-8').read()
if 'UploadFilteredAsync' not in s:
    anchor = '''    Task<CloudSyncReport> SyncAsync(CloudTarget target, bool dryRun = false,'''
    add = '''    /// <summary>
    /// 只上传"符合筛选条件"的素材：按 <see cref="AssetQuery"/> 在索引里选出素材，
    /// 把它们的本地文件 + 边车 PUT 到云端对应相对路径（已存在则覆盖）。
    /// </summary>
    Task<CloudSyncReport> UploadFilteredAsync(
        CloudTarget target, AssetQuery query, IProgress<CloudSyncProgress>? progress = null,
        CancellationToken ct = default);

''' + anchor
    assert anchor in s
    s = s.replace(anchor, add, 1)
    io.open(p, 'w', encoding='utf-8').write(s)
    print('interface: UploadFilteredAsync')
PYEOF_MARK = 1