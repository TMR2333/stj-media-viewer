# Mock WebDAV Server

给 `stj-media-viewer` 云同步自检用的**最小可用 WebDAV 服务**：纯 Python 3 标准库，零第三方依赖、零外网。

它不是完整 WebDAV 实现，只覆盖云同步真正用到的六个方法，但走的是真实 TCP/HTTP：

| 方法 | 说明 |
|---|---|
| `OPTIONS` | 返回 `DAV: 1` 与 `Allow` 能力声明 |
| `PROPFIND` | `Depth: 0/1`，返回 `D:multistatus` XML；`href` 已按段 URL 编码；含 `getlastmodified` / `getcontentlength` / `resourcetype`；`Depth: infinity` 明确返回 403（跟坚果云/群晖一致） |
| `GET` / `HEAD` | 下载文件（`Content-Length` 正确） |
| `PUT` | 上传（支持 `Content-Length` 和 chunked；写临时文件后原子替换；新建 201、覆盖 204） |
| `MKCOL` | 建目录（已存在 405、父目录不存在 409） |
| `DELETE` | 删文件 / 递归删目录 |

鉴权：HTTP Basic。凭据错误或缺失一律 `401` + `WWW-Authenticate: Basic realm="mock"`。

## 启动

```bash
# 默认端口 8899，账号 test / test123
python tools/MockWebDav/server.py --port 8899 --root <同步根目录> --user test --password test123

# 端口 0 = 系统分配（测试里推荐，避免端口冲突）
python tools/MockWebDav/server.py --port 0 --root ./tmp-dav
```

`--root` 指定的目录就是 WebDAV 同步根（URL 里的 `/`），不存在会自动创建。
启动后按提示把 `Endpoint` 填进 App 的 WebDAV 目标配置即可：

```
Endpoint : http://127.0.0.1:8899/
Username : test
Password : test123
```

## 冒烟测试

```bash
python tools/MockWebDav/smoke.py
```

脚本会自己起一个子进程服务器（随机空闲端口 + 临时目录），用 `urllib` 真实走一遍
`OPTIONS / MKCOL / PUT / HEAD / PROPFIND / GET / DELETE` 以及鉴权失败、404、405、409、
深目录、目录穿越等错误路径，逐项打印 `PASS/FAIL`，最后打印 `ALL CHECKS PASSED`：

* 退出码 `0` = 全部通过；
* 退出码 `1` = 有失败项（会附带服务器 stderr）。

C# 侧由 `tools/SelfCheck` 的 `CheckWebDav` 段落拉起同一个服务器完成端到端验证。

## 安全警告

**仅供本机自检使用，不要暴露到公网！**

* Basic 认证是明文口令，没有 TLS，抓包即得账号密码；
* 默认只绑定 `127.0.0.1`，不要改成 `0.0.0.0`；
* 服务器不做并发写保护、不做配额/锁定，只适合单客户端测试；
* 测试数据请放在临时目录，别指向真实照片库。