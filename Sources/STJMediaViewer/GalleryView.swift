import AppKit
import STJCore
import SwiftUI

/// 画廊模式：在当前筛选结果内做大图查看，可前后翻页。
/// ← → 翻页、Esc 关闭、空格打开 QuickLook 由 KeyMonitor 统一接管。
struct GalleryView: View {
    @EnvironmentObject private var model: LibraryModel
    @Environment(\.dismiss) private var dismiss

    @State private var image: NSImage?
    @State private var loadFailed = false

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()
            if let asset = model.galleryAsset {
                VStack(spacing: 0) {
                    header(asset)
                    imageArea(asset)
                    footer(asset)
                }
            } else {
                ProgressView()
                    .controlSize(.large)
                    .tint(.white)
            }
        }
        .frame(minWidth: 880, minHeight: 600)
        .task(id: "\(model.galleryAssetID ?? "none")|\(model.galleryLUTName ?? "none")") {
            await loadImage()
        }
        .onDisappear { image = nil }
    }

    // MARK: 顶栏

    private func header(_ asset: Asset) -> some View {
        HStack(spacing: 12) {
            Image(systemName: asset.isVideo ? "film" : "photo")
                .font(.title3)
                .foregroundStyle(.white.opacity(0.75))
            VStack(alignment: .leading, spacing: 2) {
                Text(asset.fileName)
                    .font(.headline)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .foregroundStyle(.white)
                Text("\(asset.kindText) · \(asset.dimensionText) · \(asset.sizeText)")
                    .font(.caption)
                    .foregroundStyle(.white.opacity(0.6))
            }
            Spacer()
            if !asset.isVideo {
                lutMenu
            }
            if let position = model.galleryPosition {
                Text("\(position.index) / \(position.total)")
                    .font(.callout.monospacedDigit())
                    .foregroundStyle(.white.opacity(0.75))
            }
            Button {
                model.closeGallery()
                dismiss()
            } label: {
                Image(systemName: "xmark.circle.fill")
                    .font(.title2)
                    .foregroundStyle(.white.opacity(0.7))
            }
            .stjGlassButtonStyle()
            .help("关闭画廊（Esc）")
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 12)
        .stjGlass(cornerRadius: 18, surface: .regular, tint: Color.black.opacity(0.42))
        .padding(.horizontal, 14)
        .padding(.top, 12)
    }

    // MARK: LUT

    private var lutMenu: some View {
        let luts = model.availableLUTs
        return Menu {
            Button("原图") { model.galleryLUTName = nil }
            if !luts.isEmpty {
                Divider()
                ForEach(luts) { lut in
                    Button(lut.name) { model.galleryLUTName = lut.name }
                }
            } else {
                Divider()
                Text("把 .cube 文件放到数据目录的 LUTs/ 下")
            }
        } label: {
            Label(model.galleryLUTName ?? "LUT", systemImage: "camera.filters")
                .font(.caption)
        }
        .menuStyle(.borderlessButton)
        .fixedSize()
        .disabled(luts.isEmpty)
        .help("画廊预览 LUT（.cube 文件放在数据目录 LUTs/）")
    }

    private func applyLUTIfNeeded(_ image: NSImage) -> NSImage {
        guard let name = model.galleryLUTName,
              let lut = model.availableLUTs.first(where: { $0.name == name }) else { return image }
        return LUTPreview.render(image, lut: lut)
    }

    // MARK: 图像区域

    @ViewBuilder
    private func imageArea(_ asset: Asset) -> some View {
        ZStack {
            if asset.isVideo {
                // 视频直接在画廊里播放（AVPlayerView 自带控件），不再显示静态封面。
                VideoPlayerView(url: asset.fileURL)
                    .padding(.horizontal, 20)
                    .padding(.vertical, 10)
            } else if let image {
                Image(nsImage: image)
                    .resizable()
                    .aspectRatio(contentMode: .fit)
                    .padding(.horizontal, 28)
                    .padding(.vertical, 12)
                    .shadow(color: .black.opacity(0.6), radius: 18, y: 6)
            } else if loadFailed {
                VStack(spacing: 10) {
                    Image(systemName: "exclamationmark.triangle")
                        .font(.largeTitle)
                    Text("无法加载这个素材")
                        .font(.headline)
                    Text(asset.fileName)
                        .font(.callout)
                        .foregroundStyle(.white.opacity(0.6))
                    Text(asset.path)
                        .font(.caption2)
                        .foregroundStyle(.white.opacity(0.4))
                        .lineLimit(2)
                        .truncationMode(.middle)
                }
                .foregroundStyle(.white.opacity(0.85))
            } else {
                ProgressView()
                    .controlSize(.large)
                    .tint(.white)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .contentShape(Rectangle())
        .onTapGesture(count: 2) {
            model.openSelectionInDefaultApp()
        }
    }

    // MARK: 底栏

    private func footer(_ asset: Asset) -> some View {
        HStack(spacing: 16) {
            Button {
                model.stepGallery(by: -1)
            } label: {
                Label("上一张", systemImage: "chevron.left")
            }
            .disabled((model.galleryPosition?.index ?? 1) <= 1)
            .help("上一张（←）")

            Button {
                model.stepGallery(by: 1)
            } label: {
                Label("下一张", systemImage: "chevron.right")
            }
            .disabled((model.galleryPosition?.index ?? 1) >= (model.galleryPosition?.total ?? 1))
            .help("下一张（→）")

            Divider()
                .frame(height: 18)
                .overlay(.white.opacity(0.2))

            RatingStars(rating: asset.rating, size: 12)
            if asset.isPick == 1 {
                CapsuleBadge(text: "精选", systemImage: "checkmark.seal.fill", tint: .green)
            } else if asset.isPick == 0 {
                CapsuleBadge(text: "废片", systemImage: "trash", tint: .red)
            }

            Spacer()

            Text(asset.breadcrumbDescription)
                .font(.caption)
                .foregroundStyle(.white.opacity(0.55))
                .lineLimit(1)
                .truncationMode(.middle)

            Button {
                model.openSelectionInDefaultApp()
            } label: {
                Label("打开", systemImage: "arrow.up.forward.app")
            }
            Button {
                model.revealSelection()
            } label: {
                Label("Finder", systemImage: "folder")
            }
        }
        .buttonStyle(.borderless)
        .tint(.white)
        .padding(.horizontal, 20)
        .padding(.vertical, 10)
        .stjGlass(cornerRadius: 18, surface: .regular, tint: Color.black.opacity(0.42))
        .padding(.horizontal, 14)
        .padding(.bottom, 12)
    }

    // MARK: 取图

    private func loadImage() async {
        guard let asset = model.galleryAsset else {
            image = nil
            loadFailed = false
            return
        }
        image = nil
        loadFailed = false
        guard !asset.isVideo else { return } // 视频交给 AVPlayerView，不预载静态图
        let url = asset.fileURL
        let loaded = await Task.detached(priority: .userInitiated) {
            ImageLoader.previewImage(at: url)
        }.value
        guard !Task.isCancelled else { return }
        if let loaded {
            image = applyLUTIfNeeded(loaded)
            AppLog.breadcrumb("画廊加载成功：\(asset.fileName)")
            return
        }
        // 图片解码失败（例如视频）回退 QuickLook 缩略图，失败同样按文件名写日志。
        let fallback = await ThumbnailStore.shared.image(for: asset, targetSize: 2_000)
        guard !Task.isCancelled else { return }
        if let fallback {
            image = applyLUTIfNeeded(fallback.image)
        } else {
            loadFailed = true
            AppLog.error("画廊加载失败：\(asset.fileName)（\(asset.path)）")
        }
    }
}

private extension Asset {
    /// 画廊底栏用的一行位置摘要。
    var breadcrumbDescription: String {
        var pieces: [String] = []
        if let city, !city.isEmpty { pieces.append(city) }
        if let capturedDay, !capturedDay.isEmpty { pieces.append(capturedDay) }
        if let deviceKey, !deviceKey.isEmpty { pieces.append(deviceKey) }
        return pieces.joined(separator: " · ")
    }
}