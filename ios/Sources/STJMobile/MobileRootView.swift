import STJCore
import SwiftUI

/// iPad P0 主界面：侧栏 + 网格 + 检视面板（iPhone 上侧栏自动折叠为列表页）。
struct MobileRootView: View {
    @ObservedObject var model: MobileLibraryModel
    @ObservedObject var settings: SettingsStore

    @State private var section: SidebarSection? = .all
    @StateObject private var photos = PhotoLibraryModel()
    @State private var showsPhotos = false
    @State private var showsFilters = false
    @State private var showsSettings = false
    @State private var isImporterPresented = false

    var body: some View {
        NavigationSplitView {
            sidebar
        } detail: {
            Group {
                if showsPhotos {
                    MobilePhotosView(photos: photos)
                        .navigationTitle("系统相册")
                        .navigationBarTitleDisplayMode(.inline)
                        .toolbar { photosToolbarContent }
                } else {
                    content
                        .navigationTitle(title)
                        .navigationBarTitleDisplayMode(.inline)
                        .searchable(text: $model.filter.searchText, placement: .navigationBarDrawer(displayMode: .always), prompt: "搜索文件名、城市、关键词")
                        .toolbar { toolbarContent }
                        .inspector(isPresented: $model.showsInspector) {
                            MobileInspectorView(model: model)
                                .inspectorColumnWidth(min: 260, ideal: 300, max: 360)
                        }
                }
            }
        }
        .task {
            await model.restoreLastLibrary()
            let defaults = UserDefaults.standard
            if defaults.bool(forKey: "openPhotosOnLaunch") {
                showsPhotos = true
                await photos.requestAccessAndLoad()
                if defaults.bool(forKey: "openPhotoPreview") {
                    photos.selectFirstIfNeeded()
                }
            }
        }
        .sheet(isPresented: $showsFilters) { MobileFilterView(model: model) }
        .sheet(isPresented: $showsSettings) { NavigationStack { MobileSettingsView(settings: settings, model: model) } }
        .sheet(isPresented: $isImporterPresented) {
            DocumentFolderPicker { url in
                isImporterPresented = false
                Task { await model.importLibrary(from: url) }
            } onCancel: {
                isImporterPresented = false
            }
        }
        .fullScreenCover(isPresented: Binding(
            get: { model.galleryAssetID != nil },
            set: { if !$0 { model.galleryAssetID = nil } }
        )) {
            MobileGalleryView(model: model)
        }
        .alert("出错了", isPresented: Binding(
            get: { model.errorMessage != nil },
            set: { if !$0 { model.errorMessage = nil } }
        )) {
            Button("好") { model.errorMessage = nil }
        } message: {
            Text(model.errorMessage ?? "")
        }
    }

    static let sortOptions: [SortOption] = [
        SortOption(title: "拍摄时间（新→旧）", descriptor: SortDescriptor(key: .captured, ascending: false)),
        SortOption(title: "拍摄时间（旧→新）", descriptor: SortDescriptor(key: .captured, ascending: true)),
        SortOption(title: "文件名", descriptor: SortDescriptor(key: .fileName, ascending: true)),
        SortOption(title: "最近修改", descriptor: SortDescriptor(key: .modified, ascending: false)),
        SortOption(title: "文件大小", descriptor: SortDescriptor(key: .size, ascending: false)),
        SortOption(title: "评分", descriptor: SortDescriptor(key: .rating, ascending: false)),
    ]

    private var title: String {
        switch section {
        case .images: return "图片素材"
        case .videos: return "视频素材"
        case .pick: return "精选"
        case .rejected: return "废片"
        case .uncategorized: return "未归类"
        default: return "全部素材"
        }
    }

    // MARK: - 侧栏

    private var sidebar: some View {
        List(selection: $section) {
            Section("素材库") {
                row(.all, title: "全部素材", symbol: "square.grid.2x2", count: model.facets.total)
                row(.images, title: "图片素材", symbol: "photo.on.rectangle", count: kindCount("1"))
                row(.videos, title: "视频素材", symbol: "film", count: kindCount("2"))
            }
            Section("智能分类") {
                row(.pick, title: "精选", symbol: "star", count: model.facets.picked)
                row(.rejected, title: "废片", symbol: "trash.slash", count: model.facets.rejected)
            }
            Section("系统相册") {
                Button {
                    showsPhotos = true
                } label: {
                    HStack {
                        Label("相册", systemImage: "photo.stack")
                        Spacer()
                        if showsPhotos {
                            Image(systemName: "checkmark").font(.caption.weight(.semibold))
                        }
                    }
                }
                .foregroundStyle(showsPhotos ? Color.accentColor : .primary)
            }
            if !model.collections.isEmpty {
                Section("合集") {
                    ForEach(model.collections) { collection in
                        Label(collection.name, systemImage: "rectangle.stack")
                            .badge(collection.count)
                    }
                }
            }
            Section {
                Button {
                    isImporterPresented = true
                } label: {
                    Label("打开 / 导入素材库…", systemImage: "folder.badge.plus")
                }
                Button {
                    showsSettings = true
                } label: {
                    Label("设置", systemImage: "gearshape")
                }
                if let path = model.directory?.lastPathComponent {
                    Text(path).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                }
            }
        }
        .navigationSplitViewColumnWidth(min: 240, ideal: 268, max: 300)
        .navigationTitle("STJ Media Viewer")
        .onChange(of: section) { _, newValue in applySection(newValue) }
    }

    private func row(_ value: SidebarSection, title: String, symbol: String, count: Int) -> some View {
        Label(title, systemImage: symbol)
            .badge(count)
            .tag(value)
    }

    private func kindCount(_ kind: String) -> Int {
        model.facets.kinds.first { $0.value == kind }?.count ?? 0
    }

    private func applySection(_ newValue: SidebarSection?) {
        showsPhotos = false
        var next = model.filter
        next.kinds = []
        next.pick = nil
        switch newValue {
        case .images: next.kinds = [1]
        case .videos: next.kinds = [2]
        case .pick: next.pick = 1
        case .rejected: next.pick = 0
        default: break
        }
        model.filter = next
    }

    // MARK: - 网格

    @ViewBuilder
    private var content: some View {
        if model.catalog == nil {
            ContentUnavailableView {
                Label("还没有打开素材库", systemImage: "externaldrive.badge.questionmark")
            } description: {
                Text("用「文件」App 选一个含 catalog.db 的目录，或者把整个素材目录导入新建库。")
            } actions: {
                Button("打开 / 导入素材库…") { isImporterPresented = true }
                    .buttonStyle(.borderedProminent)
            }
        } else if model.assets.isEmpty && !model.isLoading {
            ContentUnavailableView {
                Label("没有匹配的素材", systemImage: "photo.on.rectangle.angled")
            } description: {
                Text(model.filter.isEmpty ? "这个库里还没有素材。" : "换个筛选条件试试。")
            } actions: {
                if !model.filter.isEmpty {
                    Button("清除筛选") { model.filter = AssetFilter() }
                }
            }
        } else {
            grid
        }
    }

    private var grid: some View {
        ScrollView {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: model.density.thumbnailMinWidth),
                                        spacing: 12)],
                      spacing: 12) {
                ForEach(model.assets) { asset in
                    MobileAssetCard(model: model, asset: asset)
                }
            }
            .padding(12)
        }
        .overlay(alignment: .bottom) { statusBar }
    }

    private var statusBar: some View {
        HStack(spacing: 10) {
            if model.isImporting {
                ProgressView().controlSize(.small)
                Text(model.importHint).lineLimit(1)
            } else {
                Text("共 \(model.totalCount) 张")
                if model.filter.activeCount > 0 {
                    Text("筛选 \(model.assets.count)").foregroundStyle(Color.accentColor)
                }
                if !model.selection.isEmpty {
                    Text("已选 \(model.selection.count)")
                }
            }
            Spacer()
            if model.isLoading { ProgressView().controlSize(.small) }
        }
        .font(.caption)
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
        .background(.bar)
    }

    @ToolbarContentBuilder
    private var photosToolbarContent: some ToolbarContent {
        ToolbarItem(placement: .topBarTrailing) {
            Button {
                Task { await photos.refresh() }
            } label: {
                Label("刷新", systemImage: "arrow.clockwise")
            }
            .disabled(photos.isLoading)
        }
        ToolbarItem(placement: .topBarTrailing) {
            Text("共 \(photos.items.count) 张").font(.caption).foregroundStyle(.secondary)
        }
    }

    @ToolbarContentBuilder
    private var toolbarContent: some ToolbarContent {
        ToolbarItemGroup(placement: .primaryAction) {
            Menu {
                ForEach(Self.sortOptions) { option in
                    Button(option.title) { model.sort = option.descriptor }
                }
            } label: {
                Label("排序", systemImage: "arrow.up.arrow.down")
            }

            Button { showsFilters = true } label: {
                Label("筛选", systemImage: model.filter.activeCount > 0
                      ? "line.3.horizontal.decrease.circle.fill"
                      : "line.3.horizontal.decrease.circle")
            }

            Button {
                model.showsInspector.toggle()
            } label: {
                Label("检视", systemImage: "sidebar.trailing")
            }

            if let primary = model.primarySelection {
                Button {
                    model.galleryAssetID = primary.id
                } label: {
                    Label("全屏", systemImage: "arrow.up.left.and.arrow.down.right")
                }
            }
        }
    }
}

// MARK: - 卡片

private struct MobileAssetCard: View {
    @ObservedObject var model: MobileLibraryModel
    let asset: Asset
    @State private var hoveringHighlight = false

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            MobileThumbnailView(asset: asset, targetSize: CGFloat(model.density.thumbnailMinWidth) * 2)
                .frame(height: CGFloat(model.density.thumbnailMinWidth) * 0.75)
                .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
                .overlay(alignment: .topLeading) { badges }
                .overlay {
                    if model.selection.contains(asset.id) {
                        RoundedRectangle(cornerRadius: 8, style: .continuous)
                            .strokeBorder(Color.accentColor, lineWidth: 3)
                    }
                }
            Text(asset.fileName)
                .font(.caption)
                .lineLimit(1)
                .truncationMode(.middle)
        }
        .contentShape(Rectangle())
        .onTapGesture { model.select(asset) }
        .onTapGesture(count: 2) { model.galleryAssetID = asset.id }
        .contextMenu {
            ForEach(0...5, id: \.self) { rating in
                Button {
                    model.selection = [asset.id]
                    model.setRating(rating)
                } label: {
                    Label(rating == 0 ? "清除评分" : String(repeating: "★", count: rating), systemImage: "star")
                }
            }
            Divider()
            Button { model.selection = [asset.id]; model.setPick(1) } label: { Label("标记精选", systemImage: "star.circle") }
            Button { model.selection = [asset.id]; model.setPick(0) } label: { Label("标记废片", systemImage: "xmark.circle") }
        }
    }

    @ViewBuilder
    private var badges: some View {
        HStack(spacing: 4) {
            if asset.hasJpegSibling {
                badge("RAW+JPG", .orange)
            } else if asset.isRaw {
                badge("RAW", .orange)
            }
            if asset.isVideo {
                badge(asset.durationText ?? "", .black.opacity(0.6))
            }
            if asset.rating > 0 {
                badge(String(repeating: "★", count: asset.rating), .black.opacity(0.55))
            }
        }
        .padding(4)
    }

    private func badge(_ text: String, _ color: Color) -> some View {
        Text(text)
            .font(.system(size: 9, weight: .semibold))
            .foregroundStyle(.white)
            .padding(.horizontal, 5)
            .padding(.vertical, 2)
            .background(color, in: Capsule())
    }
}

// MARK: - 缩略图

struct MobileThumbnailView: View {
    let asset: Asset
    let targetSize: CGFloat
    @State private var image: CGImage?

    var body: some View {
        ZStack {
            Color.secondary.opacity(0.12)
            if let image {
                Image(decorative: image, scale: 1)
                    .resizable()
                    .scaledToFill()
            } else {
                Image(systemName: asset.isVideo ? "film" : "photo")
                    .font(.title3)
                    .foregroundStyle(.secondary)
            }
        }
        .clipped()
        .task(id: asset.id) {
            image = await ThumbnailStore.shared.image(for: asset, targetSize: targetSize)?.image
        }
    }
}

// MARK: - 目录选择（UIDocumentPickerViewController 包一层，选文件夹）

struct DocumentFolderPicker: UIViewControllerRepresentable {
    var onPick: (URL) -> Void
    var onCancel: () -> Void

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    func makeUIViewController(context: Context) -> UIDocumentPickerViewController {
        let picker = UIDocumentPickerViewController(forOpeningContentTypes: [.folder], asCopy: false)
        picker.delegate = context.coordinator
        picker.allowsMultipleSelection = false
        return picker
    }

    func updateUIViewController(_ uiViewController: UIDocumentPickerViewController, context: Context) {}

    final class Coordinator: NSObject, UIDocumentPickerDelegate {
        let parent: DocumentFolderPicker
        init(_ parent: DocumentFolderPicker) { self.parent = parent }

        func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) {
            guard let url = urls.first else { return parent.onCancel() }
            parent.onPick(url)
        }

        func documentPickerWasCancelled(_ controller: UIDocumentPickerViewController) {
            parent.onCancel()
        }
    }
}


/// 排序菜单项（SortDescriptor 不是 Hashable，菜单用外层结构体）。
struct SortOption: Identifiable {
    let title: String
    let descriptor: STJCore.SortDescriptor
    var id: String { title }
}
