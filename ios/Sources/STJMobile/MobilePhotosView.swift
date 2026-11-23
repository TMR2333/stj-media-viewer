import SwiftUI
import UIKit

/// iPad/iPhone：系统相册网格。
struct MobilePhotosView: View {
    @ObservedObject var photos: PhotoLibraryModel
    @State private var previewItem: PhotoItem?

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
        }
        .onChange(of: photos.selectedPhotoID) { _, _ in openPreselectedIfNeeded() }
        .onChange(of: photos.items) { _, _ in openPreselectedIfNeeded() }
        .fullScreenCover(item: $previewItem) { item in
            MobilePhotoViewer(photos: photos, startID: item.id)
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
                        previewItem = item
                    } label: {
                        PhotoThumbnailCell(photos: photos, item: item)
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

private struct PhotoThumbnailCell: View {
    @ObservedObject var photos: PhotoLibraryModel
    let item: PhotoItem
    @State private var image: UIImage?

    var body: some View {
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
        .frame(height: 116)
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
        .overlay(alignment: .bottomLeading) {
            Text(item.dateText)
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
