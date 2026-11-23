import STJCore
import SwiftUI

/// 全屏画廊：左右滑动切换，双击放大，顶部显示位置与文件名。
struct MobileGalleryView: View {
    @ObservedObject var model: MobileLibraryModel
    @State private var image: CGImage?
    @State private var scale: CGFloat = 1
    @State private var lastScale: CGFloat = 1

    private var index: Int? {
        guard let id = model.galleryAssetID else { return nil }
        return model.assets.firstIndex { $0.id == id }
    }

    private var asset: Asset? {
        guard let index, model.assets.indices.contains(index) else { return nil }
        return model.assets[index]
    }

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()
            if let asset {
                Group {
                    if let image {
                        Image(decorative: image, scale: 1)
                            .resizable()
                            .scaledToFit()
                            .scaleEffect(scale)
                            .gesture(
                                MagnificationGesture()
                                    .onChanged { value in scale = max(1, min(6, lastScale * value)) }
                                    .onEnded { _ in lastScale = scale }
                            )
                    } else {
                        ProgressView().tint(.white)
                    }
                }
                .task(id: asset.id) {
                    scale = 1
                    lastScale = 1
                    image = ImageCodecs.decode(at: asset.fileURL)
                }

                VStack {
                    topBar(asset)
                    Spacer()
                    bottomBar(asset)
                }
                .padding()
            }
        }
        .statusBarHidden()
    }

    private func topBar(_ asset: Asset) -> some View {
        HStack {
            Button {
                model.galleryAssetID = nil
            } label: {
                Image(systemName: "xmark.circle.fill").font(.title2).foregroundStyle(.white.opacity(0.85))
            }
            Spacer()
            VStack(spacing: 2) {
                Text(asset.fileName).font(.callout).bold().foregroundStyle(.white).lineLimit(1)
                if let index {
                    Text("\(index + 1) / \(model.assets.count)").font(.caption2).foregroundStyle(.white.opacity(0.7))
                }
            }
            Spacer()
            Color.clear.frame(width: 28, height: 28)
        }
    }

    private func bottomBar(_ asset: Asset) -> some View {
        HStack(spacing: 22) {
            Button { step(-1) } label: { Image(systemName: "chevron.left.circle.fill").font(.title) }
            Button {
                model.selection = [asset.id]
            } label: {
                Image(systemName: model.selection.contains(asset.id) ? "checkmark.circle.fill" : "circle")
                    .font(.title2)
            }
            Button {
                model.selection = [asset.id]
                model.galleryAssetID = nil
            } label: {
                Image(systemName: "info.circle").font(.title2)
            }
            Button { step(1) } label: { Image(systemName: "chevron.right.circle.fill").font(.title) }
        }
        .foregroundStyle(.white)
        .padding(.bottom, 6)
    }

    private func step(_ delta: Int) {
        guard let index else { return }
        let next = index + delta
        guard model.assets.indices.contains(next) else { return }
        model.galleryAssetID = model.assets[next].id
        model.selection = [model.assets[next].id]
    }
}
