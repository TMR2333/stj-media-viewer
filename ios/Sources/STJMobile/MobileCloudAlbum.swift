import STJCore
import SwiftUI

/// iPad / iPhone 云相册：WebDAV 远端相册浏览、预览、导入与上传。
@MainActor
final class MobileCloudAlbumModel: ObservableObject {
    @Published private(set) var items: [CloudAlbumItem] = []
    @Published private(set) var thumbnails: [String: CGImage] = [:]
    @Published var selection: Set<String> = []
    @Published private(set) var isLoading = false
    @Published private(set) var status: String?
    @Published private(set) var isImporting = false
    @Published private(set) var importProgress: (done: Int, total: Int)?

    private var client: WebDAVClient?
    private var pendingThumbnails: [CloudAlbumItem] = []
    private var inFlight = 0
    private let maxThumbnailTasks = 4

    var selectedItems: [CloudAlbumItem] { items.filter { selection.contains($0.id) } }

    func configure(credentials: WebDAVCredentials) {
        if client == nil { client = WebDAVClient(credentials: credentials) }
    }

    func load(credentials: WebDAVCredentials) {
        configure(credentials: credentials)
        guard let client else { return }
        if isLoading { return }
        isLoading = true
        status = "正在读取云端相册…"
        selection = []
        thumbnails = [:]
        items = []
        Task { [weak self] in
            do {
                let listed = try await CloudAlbum.listMedia(client, root: "")
                guard let self else { return }
                self.items = listed
                self.isLoading = false
                self.status = listed.isEmpty ? "云端相册里还没有图片或视频。" : nil
                if UserDefaults.standard.bool(forKey: "cloudAlbumSelectAllOnLaunch") {
                    self.selection = Set(listed.map(\.id))
                }
            } catch {
                guard let self else { return }
                self.isLoading = false
                self.status = "读取失败：\(error.localizedDescription)"
            }
        }
    }

    func requestThumbnail(_ item: CloudAlbumItem) {
        guard thumbnails[item.id] == nil, !pendingThumbnails.contains(where: { $0.id == item.id }) else { return }
        if inFlight < maxThumbnailTasks {
            startThumbnail(item)
        } else {
            pendingThumbnails.append(item)
        }
    }

    private func startThumbnail(_ item: CloudAlbumItem) {
        guard let client else { return }
        inFlight += 1
        Task { [weak self] in
            let image = await CloudAlbum.thumbnail(for: item, client: client, maxPixel: 420)
            guard let self else { return }
            self.inFlight -= 1
            if let image { self.thumbnails[item.id] = image }
            while self.inFlight < self.maxThumbnailTasks, !self.pendingThumbnails.isEmpty {
                let next = self.pendingThumbnails.removeFirst()
                if self.thumbnails[next.id] == nil { self.startThumbnail(next) }
            }
        }
    }

    func previewImage(_ item: CloudAlbumItem) async -> CGImage? {
        guard let client else { return nil }
        return await CloudAlbum.previewImage(for: item, client: client, maxPixel: 2400)
    }

    func importSelected() async throws -> CloudAlbumImportSummary {
        guard let client else { throw CloudAlbumError.noMedia }
        let items = selectedItems
        isImporting = true
        importProgress = (0, items.count)
        defer {
            isImporting = false
            importProgress = nil
        }
        return try await CloudAlbum.importItems(items,
                                                client: client,
                                                destinationRoot: CloudAlbum.downloadRoot,
                                                progress: { [weak self] progress in
            Task { @MainActor in
                self?.importProgress = (progress.completed, progress.total)
            }
        })
    }

    func upload(assets: [Asset], settings: SettingsStore) async -> CloudSyncResult? {
        guard let credentials = settings.webdavCredentials() else { return nil }
        return await CloudAlbum.export(assets: assets,
                                       credentials: credentials,
                                       layout: settings.cloudLayout)
    }
}

struct MobileCloudAlbumView: View {
    @ObservedObject var settings: SettingsStore
    @ObservedObject var library: MobileLibraryModel
    var onImported: (URL) -> Void

    @StateObject private var album = MobileCloudAlbumModel()
    @Environment(\.dismiss) private var dismiss
    @State private var previewItem: CloudAlbumItem?
    @State private var errorMessage: String?
    @State private var isUploading = false

    private var credentials: WebDAVCredentials? { settings.webdavCredentials() }
    private let columns = [GridItem(.adaptive(minimum: 104, maximum: 160), spacing: 8)]

    var body: some View {
        NavigationStack {
            Group {
                if credentials == nil {
                    ContentUnavailableView {
                        Label("还没有配置 WebDAV", systemImage: "externaldrive.badge.questionmark")
                    } description: {
                        Text("在设置 → 云相册里填写服务器地址、账号与口令。")
                    }
                } else if album.items.isEmpty {
                    if album.isLoading {
                        ProgressView("正在读取云端相册…")
                    } else {
                        ContentUnavailableView {
                            Label("云端相册是空的", systemImage: "photo.on.rectangle.angled")
                        } description: {
                            Text(album.status ?? "把素材上传到 WebDAV 根目录后刷新。")
                        }
                    }
                } else {
                    grid
                }
            }
            .navigationTitle("云相册")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar { toolbarContent }
            .safeAreaInset(edge: .bottom) { bottomBar }
            .fullScreenCover(item: $previewItem) { item in
                MobileCloudPhotoViewer(item: item, album: album)
            }
            .alert("云相册", isPresented: Binding(get: { errorMessage != nil },
                                                 set: { if !$0 { errorMessage = nil } })) {
                Button("好") { errorMessage = nil }
            } message: {
                Text(errorMessage ?? "")
            }
            .onAppear {
                if let credentials {
                    album.configure(credentials: credentials)
                    album.load(credentials: credentials)
                }
            }
            .onChange(of: album.items.count) { _, count in
                guard count > 0, UserDefaults.standard.bool(forKey: "cloudAlbumImportAllOnLaunch") else { return }
                album.selection = Set(album.items.map(\.id))
                DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) { importSelection() }
            }
        }
    }

    private var grid: some View {
        ScrollView {
            LazyVGrid(columns: columns, spacing: 8) {
                ForEach(album.items) { item in
                    MobileCloudCell(item: item,
                                    thumbnail: album.thumbnails[item.id],
                                    isSelected: album.selection.contains(item.id))
                        .onAppear { album.requestThumbnail(item) }
                        .onTapGesture {
                            if album.selection.contains(item.id) { album.selection.remove(item.id) }
                            else { album.selection.insert(item.id) }
                        }
                        .onLongPressGesture { previewItem = item }
                }
            }
            .padding(10)
        }
    }

    @ToolbarContentBuilder
    private var toolbarContent: some ToolbarContent {
        ToolbarItem(placement: .cancellationAction) {
            Button("关闭") { dismiss() }
        }
        ToolbarItemGroup(placement: .primaryAction) {
            Button {
                if album.selection.count == album.items.count {
                    album.selection = []
                } else {
                    album.selection = Set(album.items.map(\.id))
                }
            } label: {
                Image(systemName: album.selection.count == album.items.count ? "checkmark.circle.fill" : "checkmark.circle")
            }
            .disabled(album.items.isEmpty)
            Button {
                if let credentials { album.load(credentials: credentials) }
            } label: {
                Image(systemName: "arrow.clockwise")
            }
            .disabled(album.isLoading)
        }
    }

    private var bottomBar: some View {
        HStack(spacing: 12) {
            Text("已选 \(album.selection.count) / \(album.items.count)")
                .font(.footnote)
                .foregroundStyle(.secondary)
            if let progress = album.importProgress {
                ProgressView(value: Double(progress.done), total: Double(max(progress.total, 1)))
                    .frame(width: 90)
            }
            Spacer()
            Button {
                uploadLocalSelection()
            } label: {
                Label("上传本地 \(library.selection.count)", systemImage: "icloud.and.arrow.up")
            }
            .buttonStyle(.bordered)
            .disabled(library.selection.isEmpty || isUploading)
            Button {
                importSelection()
            } label: {
                if album.isImporting {
                    ProgressView()
                } else {
                    Label("导入 \(album.selection.count)", systemImage: "square.and.arrow.down")
                }
            }
            .buttonStyle(.borderedProminent)
            .disabled(album.selection.isEmpty || album.isImporting)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .background(.bar)
    }

    private func importSelection() {
        Task {
            do {
                let summary = try await album.importSelected()
                if summary.downloaded > 0 {
                    onImported(summary.directory)
                    dismiss()
                }
                if summary.failed > 0 {
                    errorMessage = "有 \(summary.failed) 个文件下载失败"
                }
            } catch {
                errorMessage = error.localizedDescription
            }
        }
    }

    private func uploadLocalSelection() {
        let assets = library.selectedAssets
        guard !assets.isEmpty else { return }
        isUploading = true
        Task {
            let result = await album.upload(assets: assets, settings: settings)
            isUploading = false
            if let result {
                errorMessage = "上传 \(result.uploaded) 个 · 失败 \(result.failed) 个"
            } else {
                errorMessage = "请先在设置里配置 WebDAV"
            }
        }
    }
}

private struct MobileCloudCell: View {
    let item: CloudAlbumItem
    let thumbnail: CGImage?
    let isSelected: Bool

    var body: some View {
        ZStack {
            Color.clear
                .aspectRatio(1, contentMode: .fit)
                .overlay {
                    ZStack {
                        Rectangle().fill(Color(uiColor: .tertiarySystemFill))
                        if let thumbnail {
                            Image(decorative: thumbnail, scale: 1)
                                .resizable()
                                .aspectRatio(contentMode: .fill)
                        } else {
                            Image(systemName: item.isVideo ? "film" : "photo")
                                .font(.title3)
                                .foregroundStyle(.secondary)
                        }
                    }
                    .clipped()
                }
                .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
                .overlay {
                    RoundedRectangle(cornerRadius: 8, style: .continuous)
                        .strokeBorder(isSelected ? Color.accentColor : Color.primary.opacity(0.06),
                                      lineWidth: isSelected ? 2 : 1)
                }
                .overlay(alignment: .topLeading) {
                    if isSelected {
                        Image(systemName: "checkmark.circle.fill")
                            .foregroundStyle(Color.accentColor, Color.white)
                            .padding(4)
                    }
                }
                .overlay(alignment: .bottomTrailing) {
                    if item.isVideo {
                        Image(systemName: "play.fill")
                            .font(.caption2)
                            .padding(3)
                            .background(.ultraThinMaterial, in: Circle())
                            .padding(4)
                    }
                }
        }
        .contextMenu {
            Button("预览") { }
        }
        .accessibilityLabel(item.name)
    }
}

private struct MobileCloudPhotoViewer: View {
    let item: CloudAlbumItem
    @ObservedObject var album: MobileCloudAlbumModel
    @Environment(\.dismiss) private var dismiss
    @State private var image: CGImage?

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()
            if let image {
                Image(decorative: image, scale: 1)
                    .resizable()
                    .aspectRatio(contentMode: .fit)
                    .ignoresSafeArea()
            } else {
                ProgressView().tint(.white)
            }
            VStack {
                HStack {
                    Button {
                        dismiss()
                    } label: {
                        Image(systemName: "xmark")
                            .font(.headline)
                            .padding(10)
                            .background(.ultraThinMaterial, in: Circle())
                    }
                    Spacer()
                    Text(item.name)
                        .font(.footnote)
                        .foregroundStyle(.white)
                        .lineLimit(1)
                        .padding(8)
                        .background(.ultraThinMaterial, in: Capsule())
                }
                .padding()
                Spacer()
                Text("\(item.sizeText)\(item.modifiedText.map { " · " + $0 } ?? "")")
                    .font(.caption)
                    .foregroundStyle(.white)
                    .padding(8)
                    .background(.ultraThinMaterial, in: Capsule())
                    .padding(.bottom, 16)
            }
        }
        .task(id: item.id) {
            image = await album.previewImage(item)
        }
    }
}