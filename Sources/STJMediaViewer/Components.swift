import AppKit
import STJCore
import SwiftUI

// MARK: - 自适应流式布局

struct FlowLayout: Layout {
    var spacing: CGFloat = 8
    var lineSpacing: CGFloat = 8

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let maxWidth = proposal.width ?? .infinity
        var x: CGFloat = 0
        var y: CGFloat = 0
        var lineHeight: CGFloat = 0
        var totalWidth: CGFloat = 0
        for subview in subviews {
            let size = subview.sizeThatFits(.unspecified)
            if x > 0, x + size.width > maxWidth {
                x = 0
                y += lineHeight + lineSpacing
                lineHeight = 0
            }
            x += size.width + spacing
            totalWidth = max(totalWidth, x - spacing)
            lineHeight = max(lineHeight, size.height)
        }
        return CGSize(width: min(totalWidth, maxWidth), height: y + lineHeight)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var x = bounds.minX
        var y = bounds.minY
        var lineHeight: CGFloat = 0
        for subview in subviews {
            let size = subview.sizeThatFits(.unspecified)
            if x > bounds.minX, x + size.width > bounds.maxX {
                x = bounds.minX
                y += lineHeight + lineSpacing
                lineHeight = 0
            }
            subview.place(at: CGPoint(x: x, y: y), proposal: ProposedViewSize(size))
            x += size.width + spacing
            lineHeight = max(lineHeight, size.height)
        }
    }
}

// MARK: - 小部件

struct RatingStars: View {
    var rating: Int
    var size: CGFloat = 10

    var body: some View {
        HStack(spacing: 1) {
            ForEach(0..<5, id: \.self) { index in
                Image(systemName: index < rating ? "star.fill" : "star")
                    .font(.system(size: size))
                    .foregroundStyle(index < rating ? Color.yellow : Color.secondary.opacity(0.5))
            }
        }
        .accessibilityLabel("评分 \(rating) 星")
    }
}

struct CapsuleBadge: View {
    var text: String
    var systemImage: String?
    var tint: Color = .secondary

    var body: some View {
        HStack(spacing: 3) {
            if let systemImage {
                Image(systemName: systemImage).font(.system(size: 8, weight: .bold))
            }
            Text(text).font(.system(size: 9, weight: .semibold))
        }
        .padding(.horizontal, 5)
        .padding(.vertical, 2)
        .background(.ultraThinMaterial, in: Capsule())
        .overlay(Capsule().stroke(tint.opacity(0.35), lineWidth: 0.5))
        .foregroundStyle(tint)
    }
}

struct FilterChip: View {
    var title: String
    var symbol: String?
    var isActive: Bool = false
    var action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: 4) {
                if let symbol { Image(systemName: symbol).font(.system(size: 10)) }
                Text(title).font(.system(size: 11))
                if isActive {
                    Image(systemName: "xmark.circle.fill")
                        .font(.system(size: 9))
                        .foregroundStyle(.secondary)
                }
            }
            .padding(.horizontal, 10)
            .padding(.vertical, 5)
            .stjGlassCapsule(tint: isActive ? Color.accentColor.opacity(0.38) : nil)
            .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .foregroundStyle(isActive ? Color.accentColor : Color.primary)
    }
}

struct EmptyLibraryView: View {
    var onChoose: () -> Void

    var body: some View {
        VStack(spacing: 14) {
            Image(systemName: "photo.on.rectangle.angled")
                .font(.system(size: 46, weight: .light))
                .foregroundStyle(.secondary)
            Text("还没有打开素材库").font(.title3.weight(.semibold))
            Text("选择一个包含 catalog.db 的素材库目录，即可浏览其中的素材。")
                .font(.callout)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 360)
            Button("打开素材库…", action: onChoose)
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(nsColor: .textBackgroundColor).opacity(0.4))
    }
}

// MARK: - 缩略图

struct ThumbnailView: View {
    var asset: Asset
    var targetSize: CGFloat

    @State private var image: NSImage?
    @State private var failed = false

    var body: some View {
        ZStack {
            if let image {
                Color.clear
                    .overlay(
                        Image(nsImage: image)
                            .resizable()
                            .aspectRatio(contentMode: .fill)
                    )
                    .clipped()
            } else if failed {
                Color(nsColor: .quaternarySystemFill)
                    .overlay(
                        Image(systemName: asset.isVideo ? "film" : "photo")
                            .font(.system(size: 22, weight: .light))
                            .foregroundStyle(.tertiary)
                    )
            } else {
                Color(nsColor: .quaternarySystemFill)
                    .overlay(ProgressView().controlSize(.small))
            }
        }
        .task(id: asset.id) {
            let result = await ThumbnailStore.shared.image(for: asset, targetSize: targetSize)
            await MainActor.run {
                image = result?.image
                failed = result == nil
            }
        }
    }
}

// MARK: - 卡片几何（框选用）

/// 收集网格里每张卡片的矩形，供框选（橡皮筋）命中计算使用。
struct CardFramePreferenceKey: PreferenceKey {
    static var defaultValue: [String: CGRect] = [:]

    static func reduce(value: inout [String: CGRect], nextValue: () -> [String: CGRect]) {
        value.merge(nextValue()) { _, new in new }
    }
}


// MARK: - 素材卡片

struct AssetCardView: View {
    @EnvironmentObject private var model: LibraryModel

    var asset: Asset
    var density: LibraryDensity
    var isSelected: Bool

    private var thumbHeight: CGFloat {
        switch density {
        case .small: return 100
        case .medium: return 152
        case .large: return 214
        case .list: return 0
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            thumbnail
            VStack(alignment: .leading, spacing: 2) {
                Text(asset.fileName)
                    .font(.system(size: 11, weight: .medium))
                    .lineLimit(1)
                    .truncationMode(.middle)
                HStack(spacing: 5) {
                    Text(asset.isVideo ? (asset.durationText ?? "视频") : asset.dimensionText)
                        .font(.system(size: 9.5))
                        .foregroundStyle(.secondary)
                    if !asset.themes.isEmpty {
                        Text(asset.themes.prefix(2).joined(separator: " · "))
                            .font(.system(size: 9.5))
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }
                }
                .frame(height: 12)
            }
        }
        .padding(6)
        .background(Color(nsColor: .controlBackgroundColor).opacity(0.55), in: RoundedRectangle(cornerRadius: 9, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 9, style: .continuous)
                .strokeBorder(isSelected ? Color.accentColor : Color.primary.opacity(0.09),
                              lineWidth: isSelected ? 2 : 1)
        )
        .contentShape(RoundedRectangle(cornerRadius: 9, style: .continuous))
        .background(
            GeometryReader { proxy in
                Color.clear
                    .preference(key: CardFramePreferenceKey.self,
                                value: [asset.id: proxy.frame(in: .named(LibraryGeometry.gridSpace))])
            }
        )
        .onTapGesture(count: 2) {
            model.openSelectionInDefaultApp()
        }
        .onTapGesture {
            model.handleTap(asset: asset, modifiers: NSEvent.modifierFlags)
        }
        .onDrag { model.dragProvider(for: asset) }
        .contextMenu { contextMenu }
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(asset.fileName)，\(asset.capturedText)，\(asset.city ?? "未填写城市")")
    }

    private var thumbnail: some View {
        Color.clear
            .frame(height: thumbHeight)
            .frame(maxWidth: .infinity)
            .overlay(ThumbnailView(asset: asset, targetSize: max(thumbHeight * 2, 240)))
            .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
            .overlay(alignment: .topLeading) {
                HStack(spacing: 4) {
                    if asset.isRaw { CapsuleBadge(text: "RAW", tint: .orange) }
                    if asset.isVideo { CapsuleBadge(text: "视频", systemImage: "play.fill", tint: .pink) }
                    if asset.isLog { CapsuleBadge(text: asset.logProfile ?? "LOG", tint: .purple) }
                }
                .padding(5)
            }
            .overlay(alignment: .topTrailing) {
                if isSelected {
                    Image(systemName: "checkmark.circle.fill")
                        .font(.system(size: 15, weight: .semibold))
                        .foregroundStyle(.white, Color.accentColor)
                        .padding(5)
                }
            }
            .overlay(alignment: .bottomLeading) {
                HStack(spacing: 4) {
                    RatingStars(rating: asset.rating)
                    if asset.isPick == 1 {
                        Image(systemName: "star.circle.fill")
                            .font(.system(size: 9))
                            .foregroundStyle(.yellow)
                    } else if asset.isPick == 0 {
                        Image(systemName: "xmark.circle.fill")
                            .font(.system(size: 9))
                            .foregroundStyle(.secondary)
                    }
                }
                .padding(5)
            }
            .overlay(alignment: .bottomTrailing) {
                if let duration = asset.durationText {
                    Text(duration)
                        .font(.system(size: 9, weight: .semibold))
                        .padding(.horizontal, 4)
                        .padding(.vertical, 1)
                        .background(.thinMaterial, in: Capsule())
                        .padding(5)
                }
            }
    }

    @ViewBuilder
    private var contextMenu: some View {
        Button("用默认应用打开") { model.openSelectionInDefaultApp() }
        Button("在 Finder 中显示") { model.revealSelection() }
        Divider()
        Menu("设置评分") {
            ForEach(0...5, id: \.self) { rating in
                Button(String(repeating: "★", count: rating) + (rating == 0 ? " 清除评分" : "")) {
                    model.setRating(rating)
                }
            }
        }
        Button("标记为精选") { model.setPick(1) }
        Button("标记为废片") { model.setPick(0) }
        Button("取消选用标记") { model.setPick(nil) }
        Divider()
        if let groupID = asset.groupID, !groupID.isEmpty {
            Button(asset.isGroupCover ? "展开合并组" : "合并组信息") {
                if let group = model.mergeGroups.first(where: { $0.id == groupID }) {
                    model.toggleGroupExpansion(group)
                }
            }
        }
        Button("批量填写…") { model.isBatchEditorPresented = true }
        Button("写入 XMP 侧车") { model.writeXMP() }
        Button("导出配对（JPG+RAW）…") { model.exportPairs() }
        Menu("加入合集") {
            ForEach(model.collections) { collection in
                Button(collection.name) { model.addSelectionToCollection(collection.id) }
            }
            if model.collections.isEmpty {
                Text("暂无合集")
            }
        }
        Divider()
        Button("移到废纸篓", role: .destructive) { model.deleteSelection() }
    }
}

// MARK: - 列表行

struct AssetListRow: View {
    var asset: Asset
    var isSelected: Bool

    var body: some View {
        HStack(spacing: 10) {
            ThumbnailView(asset: asset, targetSize: 96)
                .frame(width: 44, height: 44)
                .clipShape(RoundedRectangle(cornerRadius: 4))
            VStack(alignment: .leading, spacing: 1) {
                Text(asset.fileName).font(.system(size: 12, weight: .medium)).lineLimit(1)
                HStack(spacing: 6) {
                    Text(asset.kindText)
                    Text(asset.formatCode.uppercased())
                    Text(asset.dimensionText)
                    if let duration = asset.durationText { Text(duration) }
                }
                .font(.system(size: 10))
                .foregroundStyle(.secondary)
            }
            Spacer()
            Text(asset.city ?? "—").font(.system(size: 10)).foregroundStyle(.secondary).frame(width: 90, alignment: .leading)
            Text(asset.capturedText).font(.system(size: 10)).foregroundStyle(.secondary).frame(width: 130, alignment: .leading)
            RatingStars(rating: asset.rating)
            Text(asset.sizeText).font(.system(size: 10)).foregroundStyle(.secondary).frame(width: 66, alignment: .trailing)
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 4)
        .contentShape(Rectangle())
    }
}

