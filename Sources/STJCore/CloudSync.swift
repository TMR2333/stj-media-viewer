import Foundation

// MARK: - 云同步（WebDAV 上传）

/// 云端目录布局（规格里的「云端一/二/三级目录」）。
public enum CloudLayout: String, CaseIterable, Sendable, Identifiable {
    case flat
    case byDate
    case byLocation
    case byDateAndDevice

    public var id: String { rawValue }

    public var title: String {
        switch self {
        case .flat: return "平铺（一级）"
        case .byDate: return "按年月（二级）"
        case .byLocation: return "按地点（三级）"
        case .byDateAndDevice: return "按年月 + 设备（三级）"
        }
    }

    /// 设置页展示用的路径示例（含文件名），便于理解布局。
    public var remotePathExample: String {
        switch self {
        case .flat: return "示例：远端根目录/IMG_0001.jpg"
        case .byDate: return "示例：远端根目录/2026/08/IMG_0001.jpg"
        case .byLocation: return "示例：远端根目录/中国/上海/IMG_0001.jpg"
        case .byDateAndDevice: return "示例：远端根目录/2026/08/ILCE-7RM5/IMG_0001.jpg"
        }
    }

    /// 远端目录前缀（不含文件名）。
    public func directory(for asset: Asset) -> String {
        let year = asset.capturedDay.flatMap { $0.split(separator: "-").first.map(String.init) } ?? "未分类"
        let month = asset.capturedDay.flatMap { day -> String? in
            let parts = day.split(separator: "-")
            return parts.count >= 2 ? String(parts[1]) : nil
        } ?? "00"
        switch self {
        case .flat:
            return ""
        case .byDate:
            return "\(year)/\(month)"
        case .byLocation:
            return [asset.country, asset.city].compactMap { $0?.nilIfBlank }.joined(separator: "/")
        case .byDateAndDevice:
            return "\(year)/\(month)/\(asset.deviceKey?.nilIfBlank ?? "未知设备")"
        }
    }
}

private extension String {
    var nilIfBlank: String? {
        let trimmed = trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? nil : trimmed
    }
}

/// 一条待上传计划：主文件 + 可选 XMP 边车。
public struct CloudSyncItem: Sendable, Identifiable {
    public var id: String { asset.id }
    public var asset: Asset
    public var remotePath: String
    public var sidecarURL: URL?

    public var sidecarRemotePath: String? {
        guard sidecarURL != nil else { return nil }
        let base = (remotePath as NSString).deletingPathExtension
        let ext = (remotePath as NSString).pathExtension
        let name = base + "." + (ext.isEmpty ? "xmp" : "xmp")
        return name
    }
}

public struct CloudSyncProgress: Sendable {
    public var completed: Int
    public var total: Int
    public var currentItem: String
    public var bytesUploaded: Int64
    public var failed: Int

    public init(completed: Int, total: Int, currentItem: String, bytesUploaded: Int64, failed: Int) {
        self.completed = completed
        self.total = total
        self.currentItem = currentItem
        self.bytesUploaded = bytesUploaded
        self.failed = failed
    }
}

public struct CloudSyncResult: Sendable {
    public var uploaded: [String] = []
    public var skipped: [String] = []
    public var failed: [(name: String, message: String)] = []
    public var bytesUploaded: Int64 = 0
    public var startedAt = Date()
    public var duration: TimeInterval = 0

    public var summaryText: String {
        "上传 \(uploaded.count) · 跳过 \(skipped.count) · 失败 \(failed.count) · \(ByteCountFormatter.string(fromByteCount: bytesUploaded, countStyle: .file))"
    }
}

/// 云同步计划与执行。所有网络调用经同一个 `WebDAVClient` actor，天然全局串行。
public enum CloudSync {
    /// 生成上传计划：只含「文件存在」的素材；跳过列表单独返回。
    public static func plan(assets: [Asset],
                            layout: CloudLayout,
                            fileManager: FileManager = .default) -> ([CloudSyncItem], skipped: [String]) {
        var items: [CloudSyncItem] = []
        var skipped: [String] = []
        for asset in assets {
            let path = asset.fileURL.path
            guard fileManager.fileExists(atPath: path) else {
                skipped.append(asset.fileName)
                continue
            }
            let directory = layout.directory(for: asset)
            let fileName = sanitized(asset.fileName)
            let remotePath = directory.isEmpty ? fileName : "\(directory)/\(fileName)"
            items.append(CloudSyncItem(asset: asset,
                                       remotePath: remotePath,
                                       sidecarURL: XMPReader.sidecarURL(for: asset, fileManager: fileManager)))
        }
        return (items, skipped)
    }

    /// 去掉路径里不能当文件名的字符，避免云端建出奇怪目录。
    public static func sanitized(_ component: String) -> String {
        let forbidden = CharacterSet(charactersIn: "/\\:*?\"<>|")
            .union(.newlines)
            .union(.controlCharacters)
        let cleaned = component
            .components(separatedBy: forbidden)
            .joined(separator: "-")
            .trimmingCharacters(in: .whitespacesAndNewlines)
        return cleaned.isEmpty ? "未命名" : cleaned
    }

    /// 顺序上传（全局串行）。任务取消在文件之间检查，返回已完成的部分结果。
    public static func upload(items: [CloudSyncItem],
                              credentials: WebDAVCredentials,
                              configuration: URLSessionConfiguration = .ephemeral,
                              progress: (@Sendable (CloudSyncProgress) -> Void)? = nil,
                              interItemDelay: Duration = .zero) async -> CloudSyncResult {
        var result = CloudSyncResult()
        let client = WebDAVClient(credentials: credentials, configuration: configuration)
        var completed = 0

        for item in items {
            if Task.isCancelled { break }
            if interItemDelay > .zero {
                try? await Task.sleep(for: interItemDelay)
            }
            completed += 1
            progress?(CloudSyncProgress(completed: completed - 1,
                                        total: items.count,
                                        currentItem: item.asset.fileName,
                                        bytesUploaded: result.bytesUploaded,
                                        failed: result.failed.count))
            do {
                let directory = (item.remotePath as NSString).deletingLastPathComponent
                if directory.isEmpty {
                    // 平铺布局也要确保远端根目录存在
                    try await client.createDirectories("")
                } else {
                    try await client.createDirectories(directory)
                }
                let bytes = try await client.upload(fileURL: item.asset.fileURL, to: item.remotePath)
                result.bytesUploaded += bytes
                result.uploaded.append(item.remotePath)

                // 传输后核验
                if let remote = try await client.remoteSize(item.remotePath), remote != bytes {
                    result.failed.append((item.asset.fileName, "远端大小 \(remote) ≠ 本地 \(bytes)"))
                    continue
                }

                if let sidecar = item.sidecarURL, let sidecarRemote = item.sidecarRemotePath {
                    let sidecarBytes = try await client.upload(fileURL: sidecar, to: sidecarRemote)
                    result.bytesUploaded += sidecarBytes
                    result.uploaded.append(sidecarRemote)
                }
            } catch let error as WebDAVError {
                result.failed.append((item.asset.fileName, error.localizedDescription))
            } catch {
                result.failed.append((item.asset.fileName, error.localizedDescription))
            }
        }

        result.duration = Date().timeIntervalSince(result.startedAt)
        progress?(CloudSyncProgress(completed: items.count,
                                    total: items.count,
                                    currentItem: "",
                                    bytesUploaded: result.bytesUploaded,
                                    failed: result.failed.count))
        return result
    }
}