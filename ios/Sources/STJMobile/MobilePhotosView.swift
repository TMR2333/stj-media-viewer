import SwiftUI
import UIKit

/// iPad/iPhone：系统相册网格。
struct MobilePhotosView: View {
    @ObservedObject var photos: PhotoLibraryModel
    var onImport: ((URL) -> Void)? = nil

    @State private var previewItem: PhotoItem?
    @State private var isSelecting = false
    @State private var selectedIDs: Set<String> = []
    @State private var isExporting = false
    @State private var exportNote: String?

    private var columns: [GridItem] {
        [GridItem(.adaptive(minimum: 104, maximum: 144), spacing: 8)]
    }

    var body: some View {
        Group {
            switch photos.access {
            case .unknown:
                ProgressView("正在请求相册权限…")
            case .denied:
                ContentUnavailableView {
                    Label("无法访问相册", systemImage: "photo.on.rectangle.angled")
                } description: {
                    Text("请在「设置 → 隐私与安全性 → 照片」中允许 STJ Media Viewer 访问相册。")
                } actions: {
                    Button("重新请求") { Task { await photos.requestAccessAndLoad() } }
                        .buttonStyle(.borderedProminent)
                }
            default:
                if photos.items.isEmpty {
                    ContentUnavailableView {
                        Label("相册是空的", systemImage: "photo.stack")
                    } description: {
                        Text("先在「照片」App 里添加一些照片，或点右上角刷新。")
                    }
                } else {
                    grid
                }
            }
        }
        .task {
            if photos.access == .unknown {
                await photos.requestAccessAndLoad()
            }
            openPreselectedIfNeeded()
            applyDebugLaunchHooks()
        }
        .onChange(of: photos.selectedPhotoID) { _, _ in openPreselectedIfNeeded() }
        .onChange(of: photos.items) { _, _ in openPreselectedIfNeeded() }
        .fullScreenCover(item: $previewItem) { item in
            MobilePhotoViewer(photos: photos, startID: item.id)
        }
        .toolbar { photoToolbar }
        .overlay(alignment: .top) {
            if let exportNote {
                Text(exportNote)
                    .font(.caption)
                    .padding(.horizontal, 12).padding(.vertical, 7)
                    .background(.regularMaterial, in: Capsule())
                    .padding(.top, 6)
                    .transition(.move(edge: .top).combined(with: .opacity))
            }
        }
        .overlay {
            if isExporting {
                ZStack {
                    Color.black.opacity(0.25).ignoresSafeArea()
                    VStack(spacing: 10) {
                        ProgressView()
                        Text("正在导出到素材库…").font(.footnote)
                    }
                    .padding(18)
                    .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 14))
                }
            }
        }
    }

    @ToolbarContentBuilder
    private var photoToolbar: some ToolbarContent {
        ToolbarItem(placement: .topBarLeading) {
            Button(isSelecting ? "完成" : "多选") {
                isSelecting.toggle()
                selectedIDs.removeAll()
            }
        }
        if isSelecting {
            ToolbarItem(placement: .topBarTrailing) {
                Button {
                    importSelected()
                } label: {
                    Label("导入 \(selectedIDs.count)", systemImage: "square.and.arrow.down")
                        .labelStyle(.titleAndIcon)
                }
                .disabled(selectedIDs.isEmpty || isExporting)
            }
        }
    }

    private func toggleSelection(_ item: PhotoItem) {
        if selectedIDs.contains(item.id) { selectedIDs.remove(item.id) } else { selectedIDs.insert(item.id) }
    }

    private func importSelected() {
        guard !selectedIDs.isEmpty else { return }
        let chosen = photos.items.filter { selectedIDs.contains($0.id) }
        isExporting = true
        exportNote = nil
        Task {
            let formatter = DateFormatter()
            formatter.dateFormat = "yyyyMMdd-HHmmss"
            let folder = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
                .appendingPathComponent("相册导入-\(formatter.string(from: Date()))", isDirectory: true)
            do {
                let urls = try await photos.export(chosen, to: folder)
                exportNote = "已导出 \(urls.count) 张，正在加入素材库…"
                isExporting = false
                isSelecting = false
                selectedIDs.removeAll()
                if urls.isEmpty {
                    exportNote = "没有可导出的照片（可能是 iCloud 原件未下载）"
                } else {
                    onImport?(folder)
                }
            } catch {
                isExporting = false
                exportNote = "导入失败：\(error.localizedDescription)"
            }
        }
    }

    private func applyDebugLaunchHooks() {
        let defaults = UserDefaults.standard
        if defaults.bool(forKey: "photoSelectOnLaunch"), !photos.items.isEmpty {
            isSelecting = true
            selectedIDs = Set(photos.items.prefix(3).map(\.id))
        }
        if defaults.bool(forKey: "photoImportOnLaunch"), !photos.items.isEmpty {
            isSelecting = true
            selectedIDs = Set(photos.items.prefix(2).map(\.id))
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.8) { importSelected() }
        }
    }

    private func openPreselectedIfNeeded() {
        guard previewItem == nil, let id = photos.selectedPhotoID,
              let item = photos.items.first(where: { $0.id == id }) else { return }
        previewItem = item
    }

    private var grid: some View {
        ScrollView {
            LazyVGrid(columns: columns, spacing: 8) {
                ForEach(photos.items) { item in
                    Button {
                        if isSelecting {
                            toggleSelection(item)
                        } else {
                            previewItem = item
                        }
                    } label: {
                        PhotoThumbnailCell(photos: photos, item: item,
                                           isSelected: isSelecting && selectedIDs.contains(item.id))
                    }
                    .buttonStyle(.plain)
                }
            }
            .padding(12)
        }
        .overlay(alignment: .bottom) {
            Text("系统相册 · \(photos.items.count) 张")
                .font(.caption)
                .padding(.horizontal, 10)
                .padding(.vertical, 5)
                .background(.regularMaterial, in: Capsule())
                .padding(.bottom, 10)
        }
    }
}

private struct PhotoThumbnailCell: View, Equatable {
    let photos: PhotoLibraryModel
    let item: PhotoItem
    var isSelected = false
    @State private var image: UIImage?

    static func == (lhs: PhotoThumbnailCell, rhs: PhotoThumbnailCell) -> Bool {
        lhs.item.id == rhs.item.id && lhs.isSelected == rhs.isSelected
    }

    var body: some View {
        Color.clear
            .aspectRatio(1, contentMode: .fit)
            .overlay {
                ZStack(alignment: .bottomLeading) {
                    Rectangle().fill(Color(uiColor: .tertiarySystemFill))
                    if let image {
                        Image(uiImage: image)
                            .resizable()
                            .scaledToFill()
                    } else {
                        Image(systemName: "photo")
                            .foregroundStyle(.secondary)
                    }
                }
                .clipped()
            }
            .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
        .overlay(alignment: .topTrailing) {
            if item.isFavorite {
                Image(systemName: "heart.fill")
                    .font(.caption2)
                    .padding(5)
                    .background(.ultraThinMaterial, in: Circle())
                    .padding(5)
            }
        }
        .overlay(alignment: .topLeading) {
            if isSelected {
                Image(systemName: "checkmark.circle.fill")
                    .font(.title3)
                    .symbolRenderingMode(.palette)
                    .foregroundStyle(.white, Color.accentColor)
                    .padding(6)
            }
        }
        .overlay(alignment: .bottomLeading) {
            Text(item.shortDateText)
                .font(.caption2)
                .lineLimit(1)
                .padding(.horizontal, 6)
                .padding(.vertical, 3)
                .background(.ultraThinMaterial, in: Capsule())
                .padding(6)
        }
        .task(id: item.id) {
            if image == nil {
                image = await photos.thumbnailAsync(for: item, size: CGSize(width: 360, height: 360))
            }
        }
    }
}

/// 相册大图浏览（左右滑动翻页 + 收藏切换）。
struct MobilePhotoViewer: View {
    @ObservedObject var photos: PhotoLibraryModel
    let startID: String
    @Environment(\.dismiss) private var dismiss
    @State private var index: Int = 0

    private var current: PhotoItem? {
        photos.items.indices.contains(index) ? photos.items[index] : nil
    }

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()
            if photos.items.isEmpty {
                Text("没有可显示的照片").foregroundStyle(.white)
            } else {
                TabView(selection: $index) {
                    ForEach(Array(photos.items.enumerated()), id: \.element.id) { offset, item in
                        PhotoFullImage(photos: photos, item: item)
                            .tag(offset)
                    }
                }
                .tabViewStyle(.page(indexDisplayMode: .never))
            }
            VStack {
                topBar
                Spacer()
                bottomBar
            }
            .padding(14)
        }
        .onAppear {
            index = photos.items.firstIndex { $0.id == startID } ?? 0
        }
    }

    private var topBar: some View {
        HStack(spacing: 12) {
            Button {
                dismiss()
            } label: {
                Image(systemName: "xmark")
                    .font(.callout.weight(.semibold))
                    .padding(10)
                    .background(.ultraThinMaterial, in: Circle())
            }
            Spacer()
            Text("\(index + 1) / \(photos.items.count)")
                .font(.callout.monospacedDigit())
                .padding(.horizontal, 10)
                .padding(.vertical, 5)
                .background(.ultraThinMaterial, in: Capsule())
            if let current {
                Button {
                    Task { await photos.toggleFavorite(current) }
                } label: {
                    Image(systemName: current.isFavorite ? "heart.fill" : "heart")
                        .font(.callout.weight(.semibold))
                        .foregroundStyle(current.isFavorite ? .pink : .primary)
                        .padding(10)
                        .background(.ultraThinMaterial, in: Circle())
                }
            }
        }
        .foregroundStyle(.primary)
    }

    private var bottomBar: some View {
        VStack(alignment: .leading, spacing: 4) {
            if let current {
                Text(current.dateText).font(.callout.weight(.semibold))
                HStack(spacing: 10) {
                    Label(current.dimensionText, systemImage: "aspectratio")
                    Label(current.isFavorite ? "已收藏" : "未收藏", systemImage: current.isFavorite ? "heart.fill" : "heart")
                }
                .font(.caption)
                .foregroundStyle(.secondary)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(12)
        .background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
    }
}

private struct PhotoFullImage: View {
    @ObservedObject var photos: PhotoLibraryModel
    let item: PhotoItem
    @State private var image: UIImage?

    var body: some View {
        ZStack {
            if let image {
                Image(uiImage: image)
                    .resizable()
                    .scaledToFit()
            } else {
                ProgressView().tint(.white)
            }
        }
        .task(id: item.id) {
            if image == nil {
                image = await photos.image(for: item)
            }
        }
    }
}
