import SwiftUI
import STJCore

/// 云相册窗口：远端 WebDAV 相册网格 + 预览 + 导入 + 上传。
struct CloudAlbumView: View {
    @ObservedObject var library: LibraryModel
    @ObservedObject var settings: SettingsStore
    @StateObject private var album = CloudAlbumModel()
    @Environment(\.dismiss) private var dismiss
    @Environment(\.openSettings) private var openSettings

    private var credentials: WebDAVCredentials? { settings.webdavCredentials() }

    private let columns = [GridItem(.adaptive(minimum: 132, maximum: 200), spacing: 12)]

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            content
            Divider()
            footer
        }
        .frame(minWidth: 860, idealWidth: 980, minHeight: 600, idealHeight: 680)
        .background(Color(nsColor: .windowBackgroundColor))
        .onAppear {
            if let credentials {
                album.configure(credentials: credentials)
                album.load(credentials: credentials)
            }
        }
        .onChange(of: album.items.count) { _, count in
            guard count > 0 else { return }
            previewAfterLoadIfNeeded()
        }
        .overlay {
            if let item = album.previewItem {
                previewOverlay(for: item)
            }
        }
    }

    // MARK: - 顶部

    private var header: some View {
        HStack(spacing: 12) {
            Label("云相册", systemImage: "photo.stack")
                .font(.headline)
            if let credentials {
                Text(remotePathText(credentials))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }
            Spacer()
            if album.isLoading {
                ProgressView().controlSize(.small)
            }
            Button {
                if let credentials { album.refresh(credentials: credentials) }
            } label: {
                Label("刷新", systemImage: "arrow.clockwise")
            }
            .disabled(credentials == nil || album.isLoading)
            Button("关闭") { dismiss() }
                .keyboardShortcut(.cancelAction)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
    }

    private func remotePathText(_ credentials: WebDAVCredentials) -> String {
        let root = credentials.remoteRoot.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        return root.isEmpty ? credentials.endpoint.absoluteString : "\(credentials.endpoint.absoluteString)/\(root)"
    }

    // MARK: - 内容

    @ViewBuilder
    private var content: some View {
        if credentials == nil {
            ContentUnavailableView {
                Label("还没有配置 WebDAV", systemImage: "externaldrive.badge.questionmark")
            } description: {
                Text("在设置 → 云同步里填写服务器地址、账号与口令后，这里会显示云端相册。")
            } actions: {
                Button("打开设置…") { openSettings() }
            }
        } else if album.items.isEmpty {
            if album.isLoading {
                ProgressView("正在读取云端相册…")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if let status = album.status, status.contains("失败") {
                ContentUnavailableView {
                    Label("无法读取云端相册", systemImage: "wifi.exclamationmark")
                } description: {
                    Text(status)
                } actions: {
                    Button("重试") { if let credentials { album.refresh(credentials: credentials) } }
                }
            } else {
                ContentUnavailableView {
                    Label("云端相册是空的", systemImage: "photo.on.rectangle.angled")
                } description: {
                    Text("把素材上传到 WebDAV 根目录，或换一个远端根目录。")
                }
            }
        } else {
            ScrollView {
                LazyVGrid(columns: columns, spacing: 12) {
                    ForEach(album.items) { item in
                        CloudAlbumCell(item: item,
                                       thumbnail: album.thumbnails[item.id],
                                       isSelected: album.selection.contains(item.id),
                                       onAppear: { album.requestThumbnail(item) },
                                       onTap: { toggleSelection(item) },
                                       onOpen: { album.openPreview(item) })
                    }
                }
                .padding(16)
            }

        }
    }

    private func toggleSelection(_ item: CloudAlbumItem) {
        if album.selection.contains(item.id) {
            album.selection.remove(item.id)
        } else {
            album.selection.insert(item.id)
        }
    }

    // MARK: - 底部

    private var footer: some View {
        HStack(spacing: 12) {
            Text("已选 \(album.selection.count) 张")
                .font(.callout)
                .foregroundStyle(.secondary)
            if let status = album.status {
                Text(status)
                    .font(.caption)
                    .foregroundStyle(.tertiary)
                    .lineLimit(1)
            }
            Spacer()
            if let progress = album.importProgress {
                ProgressView(value: Double(progress.done), total: Double(max(progress.total, 1)))
                    .frame(width: 120)
                Text("导入 \(progress.done)/\(progress.total)")
                    .font(.caption)
                    .monospacedDigit()
            } else if let progress = library.cloudSyncProgress {
                ProgressView(value: Double(progress.completed), total: Double(max(progress.total, 1)))
                    .frame(width: 120)
                Text("上传 \(progress.completed)/\(progress.total)")
                    .font(.caption)
                    .monospacedDigit()
            }
            Button {
                uploadLocalSelection()
            } label: {
                Label("上传本地所选 \(library.selection.count)", systemImage: "icloud.and.arrow.up")
            }
            .disabled(library.selection.isEmpty || library.isCloudSyncing || library.isImporting)
            .help("把当前素材库里选中的素材上传到这个云相册")

            Button {
                importSelection()
            } label: {
                Label("导入所选 \(album.selection.count)", systemImage: "square.and.arrow.down")
            }
            .keyboardShortcut(.defaultAction)
            .disabled(album.selection.isEmpty || album.isImporting || credentials == nil)
            .help("把云端选中的照片下载到本地并加入素材库")
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
    }

    // MARK: - 动作

    private func uploadLocalSelection() {
        library.uploadSelectionToWebDAV()
    }

    private func importSelection() {
        let selected = album.selectedItems
        guard !selected.isEmpty else { return }
        let destinationRoot = CloudAlbum.downloadRoot
        Task {
            do {
                let summary = try await album.download(selected, to: destinationRoot)
                library.showStatus("云相册导入：\(summary.descriptionText)")
                AppLog.breadcrumb("云相册导入：\(summary.downloaded) 个文件 → \(summary.directory.path)")
                if summary.downloaded > 0 {
                    await library.openFolder(summary.directory, quiet: true)
                    album.selection = []
                }
                if summary.failed > 0 {
                    library.presentedError = "有 \(summary.failed) 个文件下载失败：\(summary.failures.prefix(3).joined(separator: "、"))"
                }
            } catch {
                library.presentedError = error.localizedDescription
            }
        }
    }

    private func previewOverlay(for item: CloudAlbumItem) -> some View {
        ZStack {
            Color.black.opacity(0.94)
                .ignoresSafeArea()
                .onTapGesture { album.closePreview() }
            VStack(spacing: 12) {
                HStack {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(item.name).font(.headline).foregroundStyle(.white)
                        Text("\(item.sizeText)\(item.modifiedText.map { " · " + $0 } ?? "")")
                            .font(.caption)
                            .foregroundStyle(.white.opacity(0.7))
                    }
                    Spacer()
                    Button {
                        if album.selection.contains(item.id) { album.selection.remove(item.id) }
                        else { album.selection.insert(item.id) }
                    } label: {
                        Label(album.selection.contains(item.id) ? "已选" : "选中",
                              systemImage: album.selection.contains(item.id) ? "checkmark.circle.fill" : "circle")
                    }
                    .buttonStyle(.bordered)
                    Button("关闭") { album.closePreview() }
                        .keyboardShortcut(.cancelAction)
                }
                .padding(.horizontal, 24)

                Group {
                    if let image = album.previewImage {
                        Image(decorative: image, scale: 1)
                            .resizable()
                            .aspectRatio(contentMode: .fit)
                    } else {
                        ProgressView("正在加载预览…")
                            .foregroundStyle(.white)
                    }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .padding(.horizontal, 24)
                .padding(.bottom, 24)
            }
        }
    }

    /// 云端列表加载完成后再执行「预览第一张 / 全选导入」这类验收动作。
    private func previewAfterLoadIfNeeded() {
        let defaults = UserDefaults.standard
        if defaults.bool(forKey: "cloudAlbumSelectAllOnLaunch") {
            album.selection = Set(album.items.map(\.id))
        }
        if defaults.bool(forKey: "cloudAlbumPreviewFirstOnLaunch"), let first = album.items.first {
            album.openPreview(first)
        }
        if defaults.bool(forKey: "cloudAlbumImportAllOnLaunch") {
            album.selection = Set(album.items.map(\.id))
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.0) { importSelection() }
        }
    }
}

private struct CloudAlbumCell: View {
    let item: CloudAlbumItem
    let thumbnail: CGImage?
    let isSelected: Bool
    let onAppear: () -> Void
    let onTap: () -> Void
    let onOpen: () -> Void

    var body: some View {
        VStack(spacing: 6) {
            ZStack {
                RoundedRectangle(cornerRadius: 10, style: .continuous)
                    .fill(Color(nsColor: .quaternaryLabelColor).opacity(0.25))
                if let thumbnail {
                    Image(decorative: thumbnail, scale: 1)
                        .resizable()
                        .aspectRatio(contentMode: .fill)
                } else {
                    Image(systemName: item.isVideo ? "film" : "photo")
                        .font(.title2)
                        .foregroundStyle(.secondary)
                }
            }
            .frame(height: 116)
            .frame(maxWidth: .infinity)
            .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
            .overlay(alignment: .bottomTrailing) {
                if item.isVideo {
                    Image(systemName: "play.fill")
                        .font(.caption2)
                        .padding(4)
                        .background(.ultraThinMaterial, in: Circle())
                        .padding(6)
                }
            }
            .overlay {
                RoundedRectangle(cornerRadius: 10, style: .continuous)
                    .strokeBorder(isSelected ? Color.accentColor : Color.primary.opacity(0.08),
                                  lineWidth: isSelected ? 2 : 1)
            }
            .overlay(alignment: .topLeading) {
                if isSelected {
                    Image(systemName: "checkmark.circle.fill")
                        .font(.title3)
                        .foregroundStyle(Color.accentColor, Color.white)
                        .padding(6)
                }
            }

            Text(item.name)
                .font(.caption)
                .lineLimit(1)
                .truncationMode(.middle)
                .foregroundStyle(isSelected ? .primary : .secondary)
        }
        .contentShape(Rectangle())
        .onTapGesture(count: 2) { onOpen() }
        .onTapGesture { onTap() }
        .onAppear { onAppear() }
        .contextMenu {
            Button("预览") { onOpen() }
            Button(isSelected ? "取消选择" : "选择") { onTap() }
            Button("复制名称") {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(item.name, forType: .string)
            }
        }
    }
}