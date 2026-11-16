// Copyright (c) Stjarna Perspective. MIT.
//
// 素材库主界面：筛选栏 / 网格 / 检视面板。

import AppKit
import SwiftUI

struct ContentView: View {
    @StateObject private var model = LibraryModel()
    @State private var searchText: String = ""

    var body: some View {
        HSplitView {
            FilterSidebar(model: model)
                .frame(minWidth: 220, idealWidth: 240, maxWidth: 300)

            VStack(spacing: 0) {
                LibraryToolbar(model: model, searchText: $searchText)
                Divider()
                LibraryGrid(model: model)
            }
            .frame(minWidth: 520)

            InspectorView(model: model)
                .frame(minWidth: 260, idealWidth: 300, maxWidth: 360)
        }
        .frame(minWidth: 1080, minHeight: 680)
        .onChange(of: searchText) { value in
            model.filter.searchText = value
        }
        .onReceive(NotificationCenter.default.publisher(for: .reloadLibrary)) { _ in
            model.reload(immediate: true)
        }
        .onReceive(NotificationCenter.default.publisher(for: .clearFilters)) { _ in
            searchText = ""
            model.clearFilters()
        }
        .alert("出错了", isPresented: Binding(
            get: { model.errorMessage != nil },
            set: { if !$0 { model.errorMessage = nil } })) {
            Button("好", role: .cancel) { model.errorMessage = nil }
        } message: {
            Text(model.errorMessage ?? "")
        }
    }
}

// MARK: - 工具条

struct LibraryToolbar: View {
    @ObservedObject var model: LibraryModel
    @Binding var searchText: String

    var body: some View {
        VStack(spacing: 8) {
            HStack(spacing: 10) {
                Text("素材库")
                    .font(.system(size: 17, weight: .semibold))

                Text("筛选出 \(model.assets.count) / \(model.totalCount)")
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)

                if model.isLoading {
                    ProgressView().controlSize(.small)
                }

                Spacer()

                TextField("搜索文件名 / 城市 / 设备 / 关键词", text: $searchText)
                    .textFieldStyle(.roundedBorder)
                    .frame(width: 260)

                Picker("排序", selection: $model.sort) {
                    ForEach(SortKey.allCases) { key in
                        Text(key.title).tag(key)
                    }
                }
                .labelsHidden()
                .frame(width: 180)

                Button {
                    model.reload(immediate: true)
                } label: {
                    Image(systemName: "arrow.clockwise")
                }
                .help("重新载入")

                Button {
                    model.chooseDataDirectory()
                } label: {
                    Label("选择数据目录", systemImage: "folder")
                }
                .help("选择包含 catalog.db 的数据目录")
            }

            HStack(spacing: 8) {
                ForEach(model.facets.kinds, id: \.kind) { facet in
                    FilterChip(
                        title: "\(kindTitle(facet.kind)) \(facet.count)",
                        isOn: model.filter.kinds.contains(facet.kind)) {
                            model.toggleKind(facet.kind)
                        }
                }
                Divider().frame(height: 14)
                FilterChip(title: "只看选用", isOn: model.filter.onlyPick) {
                    model.filter.onlyPick.toggle()
                }
                FilterChip(title: "只看 Log", isOn: model.filter.onlyLog) {
                    model.filter.onlyLog.toggle()
                }
                FilterChip(title: "显示 RAW 副本", isOn: !model.filter.onlyCover) {
                    model.filter.onlyCover.toggle()
                }

                Spacer()

                if model.filter.isActive || !searchText.isEmpty {
                    Button("清空筛选") {
                        searchText = ""
                        model.clearFilters()
                    }
                    .buttonStyle(.link)
                }
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
        .background(.bar)
    }

    private func kindTitle(_ kind: Int) -> String {
        switch kind {
        case 1: return "图片"
        case 2: return "视频"
        case 3: return "音频"
        default: return "其他"
        }
    }
}

struct FilterChip: View {
    let title: String
    let isOn: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Text(title)
                .font(.system(size: 12))
                .padding(.horizontal, 9)
                .padding(.vertical, 3)
                .background(
                    RoundedRectangle(cornerRadius: 6, style: .continuous)
                        .fill(isOn ? Color.accentColor.opacity(0.18) : Color.primary.opacity(0.05))
                )
                .overlay(
                    RoundedRectangle(cornerRadius: 6, style: .continuous)
                        .strokeBorder(isOn ? Color.accentColor.opacity(0.55) : Color.primary.opacity(0.10))
                )
        }
        .buttonStyle(.plain)
        .foregroundStyle(isOn ? Color.accentColor : Color.primary)
    }
}

// MARK: - 筛选栏

struct FilterSidebar: View {
    @ObservedObject var model: LibraryModel

    var body: some View {
        List {
            Section("素材库") {
                if let dataDirectory = model.dataDirectory {
                    LabeledContent("数据目录") {
                        Text(dataDirectory.lastPathComponent)
                    }
                    LabeledContent("缓存") {
                        Text("\(model.thumbnails.cachedCount) 张")
                    }
                } else {
                    Text("尚未连接数据目录")
                        .foregroundStyle(.secondary)
                    Button("选择数据目录…") { model.chooseDataDirectory() }
                        .buttonStyle(.link)
                }
            }

            if !model.facets.years.isEmpty {
                Section("拍摄年份") {
                    facetRows(model.facets.years, selection: $model.filter.year)
                }
            }

            if !model.facets.cities.isEmpty {
                Section("城市") {
                    facetRows(model.facets.cities, selection: $model.filter.city)
                }
            }

            if !model.facets.devices.isEmpty {
                Section("拍摄设备") {
                    facetRows(model.facets.devices, selection: $model.filter.device)
                }
            }

            if !model.facets.themes.isEmpty {
                Section("主题") {
                    facetRows(model.facets.themes, selection: $model.filter.theme)
                }
            }

            Section("评分") {
                ForEach([5, 4, 3, 2, 1], id: \.self) { stars in
                    Button {
                        model.filter.minRating = model.filter.minRating == stars ? 0 : stars
                    } label: {
                        HStack {
                            Text(String(repeating: "★", count: stars))
                                .font(.system(size: 11))
                                .foregroundStyle(.secondary)
                            Text("≥ \(stars) 星")
                                .font(.system(size: 12))
                            Spacer()
                            if model.filter.minRating == stars {
                                Image(systemName: "checkmark")
                                    .font(.system(size: 11, weight: .semibold))
                                    .foregroundStyle(Color.accentColor)
                            }
                        }
                    }
                    .buttonStyle(.plain)
                }
            }
        }
        .listStyle(.sidebar)
    }

    @ViewBuilder
    private func facetRows(_ items: [FacetCount], selection: Binding<String?>) -> some View {
        ForEach(items) { item in
            Button {
                selection.wrappedValue = selection.wrappedValue == item.value ? nil : item.value
            } label: {
                HStack(spacing: 8) {
                    Text(item.value)
                        .font(.system(size: 12))
                        .lineLimit(1)
                    Spacer()
                    Text("\(item.count)")
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                    if selection.wrappedValue == item.value {
                        Image(systemName: "checkmark")
                            .font(.system(size: 10, weight: .semibold))
                            .foregroundStyle(Color.accentColor)
                    }
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
        }
    }
}

// MARK: - 网格

struct LibraryGrid: View {
    @ObservedObject var model: LibraryModel

    private let columns = [GridItem(.adaptive(minimum: 168, maximum: 260), spacing: 16)]

    var body: some View {
        Group {
            if model.dataDirectory == nil {
                EmptyLibraryView(model: model)
            } else if model.assets.isEmpty && !model.isLoading {
                HintView(
                    title: "没有匹配的素材",
                    systemImage: "photo.on.rectangle.angled",
                    detail: model.filter.isActive ? "试试放宽筛选条件。" : "这个素材库里还没有素材。")
            } else {
                ScrollView {
                    LazyVGrid(columns: columns, spacing: 16) {
                        ForEach(model.assets) { asset in
                            AssetCard(asset: asset, model: model)
                                .onTapGesture(count: 2) { model.selectedID = asset.id; model.openInDefaultApp() }
                                .onTapGesture { model.selectedID = asset.id }
                        }
                    }
                    .padding(16)
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(nsColor: .textBackgroundColor))
    }
}

struct AssetCard: View {
    let asset: Asset
    @ObservedObject var model: LibraryModel
    @State private var image: NSImage?

    private var isSelected: Bool { model.selectedID == asset.id }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            ZStack(alignment: .topLeading) {
                RoundedRectangle(cornerRadius: 8, style: .continuous)
                    .fill(Color.primary.opacity(0.06))

                if let image {
                    Image(nsImage: image)
                        .resizable()
                        .aspectRatio(contentMode: .fill)
                        .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
                } else {
                    Image(systemName: placeholderGlyph)
                        .font(.system(size: 22))
                        .foregroundStyle(.secondary)
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                }

                HStack(spacing: 4) {
                    if asset.isLog {
                        badge("LOG", color: .orange)
                    }
                    if asset.kind == 1 && asset.path.lowercased().hasSuffix(".arw") {
                        badge("RAW", color: .brown)
                    }
                    if let durationText = asset.durationText {
                        badge(durationText, color: .blue)
                    }
                    Spacer(minLength: 0)
                    if asset.rating > 0 {
                        badge(String(repeating: "★", count: asset.rating), color: .yellow)
                    }
                }
                .padding(6)
            }
            .aspectRatio(4.0 / 3.0, contentMode: .fit)
            .overlay(
                RoundedRectangle(cornerRadius: 8, style: .continuous)
                    .strokeBorder(isSelected ? Color.accentColor : Color.primary.opacity(0.12),
                                  lineWidth: isSelected ? 2 : 1)
            )

            Text(asset.fileName)
                .font(.system(size: 12, weight: .medium))
                .lineLimit(1)
                .truncationMode(.middle)

            Text(metaLine)
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
                .lineLimit(1)
        }
        .contentShape(Rectangle())
        .onDrag {
            NSItemProvider(contentsOf: asset.fileURL) ?? NSItemProvider()
        }
        .contextMenu {
            Button("用默认应用打开") { model.selectedID = asset.id; model.openInDefaultApp() }
            Button("在 Finder 中显示") { model.selectedID = asset.id; model.revealInFinder() }
        }
        .task(id: asset.id) {
            await loadThumbnail()
        }
    }

    private var metaLine: String {
        var parts: [String] = []
        if let device = asset.deviceKey ?? asset.cameraModel { parts.append(device) }
        if let city = asset.city { parts.append(city) }
        parts.append(String(asset.capturedDay?.prefix(10) ?? "—"))
        return parts.joined(separator: " · ")
    }

    private var placeholderGlyph: String {
        switch asset.kind {
        case 1: return "photo"
        case 2: return "film"
        case 3: return "waveform"
        default: return "doc"
        }
    }

    private func badge(_ text: String, color: Color) -> some View {
        Text(text)
            .font(.system(size: 9, weight: .semibold))
            .padding(.horizontal, 5)
            .padding(.vertical, 1.5)
            .background(RoundedRectangle(cornerRadius: 4, style: .continuous).fill(color.opacity(0.85)))
            .foregroundStyle(.white)
    }

    private func loadThumbnail() async {
        if let url = model.thumbnails.cachedURL(forKey: asset.thumbKey),
           let cached = NSImage(contentsOf: url) {
            image = cached
            return
        }
        if let quickLook = await ThumbnailStore.quickLookImage(for: asset.fileURL, size: 360) {
            image = quickLook
        }
    }
}

struct EmptyLibraryView: View {
    @ObservedObject var model: LibraryModel

    var body: some View {
        VStack(spacing: 14) {
            Image(systemName: "externaldrive.badge.questionmark")
                .font(.system(size: 44))
                .foregroundStyle(.secondary)

            Text("先连接一个素材库数据目录")
                .font(.system(size: 17, weight: .semibold))

            Text("macOS 版目前是只读浏览器：读取 Windows 版建立的 catalog.db 和缩略图缓存。\n把 Windows 上的 %LOCALAPPDATA%\\StjMediaViewer 整个目录拷过来，然后选中它。")
                .font(.system(size: 12))
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 460)

            Button {
                model.chooseDataDirectory()
            } label: {
                Label("选择数据目录…", systemImage: "folder")
            }
            .controlSize(.large)
            .keyboardShortcut("o", modifiers: .command)

            Text("目录里应该能看到 catalog.db 和 Cache/thumbs")
                .font(.system(size: 11))
                .foregroundStyle(.tertiary)
        }
        .padding(40)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

// MARK: - 检视面板

struct InspectorView: View {
    @ObservedObject var model: LibraryModel

    var body: some View {
        Group {
            if let asset = model.selectedAsset {
                ScrollView {
                    VStack(alignment: .leading, spacing: 16) {
                        Text(asset.fileName)
                            .font(.system(size: 14, weight: .semibold))
                            .textSelection(.enabled)

                        VStack(alignment: .leading, spacing: 6) {
                            row("类型", asset.kindText)
                            row("尺寸", asset.dimensionText)
                            if let durationText = asset.durationText { row("时长", durationText) }
                            row("评分", asset.rating > 0 ? String(repeating: "★", count: asset.rating) : "—")
                            row("色标", asset.colorLabel ?? "—")
                            row("选用", asset.isPick ? "是" : "否")
                        }

                        Divider()

                        VStack(alignment: .leading, spacing: 6) {
                            row("拍摄时间", asset.capturedText)
                            row("设备", asset.deviceKey ?? asset.cameraModel ?? "—")
                            if let lens = asset.lensModel { row("镜头", lens) }
                            if let location = asset.locationText { row("地点", location) }
                        }

                        Divider()

                        VStack(alignment: .leading, spacing: 6) {
                            row("主题", asset.theme ?? "—")
                            row("影调", asset.tone ?? "—")
                            row("Log", asset.isLog ? (asset.logProfile ?? "是") : "—")
                            row("LUT", asset.lutId ?? "—")
                        }

                        if !asset.keywords.isEmpty {
                            Divider()
                            Text("关键词")
                                .font(.system(size: 11, weight: .medium))
                                .foregroundStyle(.secondary)
                            FlowLayout(spacing: 6) {
                                ForEach(asset.keywords, id: \.self) { keyword in
                                    Text(keyword)
                                        .font(.system(size: 11))
                                        .padding(.horizontal, 7)
                                        .padding(.vertical, 3)
                                        .background(RoundedRectangle(cornerRadius: 5).fill(Color.primary.opacity(0.07)))
                                }
                            }
                        }

                        Divider()

                        HStack(spacing: 8) {
                            Button("用默认应用打开") { model.openInDefaultApp() }
                            Button("在 Finder 中显示") { model.revealInFinder() }
                        }
                        .controlSize(.small)
                    }
                    .padding(16)
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
            } else {
                HintView(
                    title: "未选择素材",
                    systemImage: "sidebar.right",
                    detail: "在网格里点一张，这里显示完整元数据。")
            }
        }
        .background(.bar)
    }

    private func row(_ title: String, _ value: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Text(title)
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
                .frame(width: 62, alignment: .leading)
            Text(value)
                .font(.system(size: 12))
                .textSelection(.enabled)
                .lineLimit(3)
        }
    }
}

// MARK: - 简易流式布局（关键词胶囊）

struct FlowLayout: Layout {
    var spacing: CGFloat = 6

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let maxWidth = proposal.width ?? .infinity
        var x: CGFloat = 0, y: CGFloat = 0, lineHeight: CGFloat = 0
        for subview in subviews {
            let size = subview.sizeThatFits(.unspecified)
            if x + size.width > maxWidth, x > 0 {
                x = 0
                y += lineHeight + spacing
                lineHeight = 0
            }
            x += size.width + spacing
            lineHeight = max(lineHeight, size.height)
        }
        return CGSize(width: maxWidth, height: y + lineHeight)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var x = bounds.minX, y = bounds.minY, lineHeight: CGFloat = 0
        for subview in subviews {
            let size = subview.sizeThatFits(.unspecified)
            if x + size.width > bounds.maxX, x > bounds.minX {
                x = bounds.minX
                y += lineHeight + spacing
                lineHeight = 0
            }
            subview.place(at: CGPoint(x: x, y: y), proposal: ProposedViewSize(size))
            x += size.width + spacing
            lineHeight = max(lineHeight, size.height)
        }
    }
}

// MARK: - 通用提示视图

struct HintView: View {
    let title: String
    let systemImage: String
    let detail: String

    var body: some View {
        VStack(spacing: 10) {
            Image(systemName: systemImage)
                .font(.system(size: 34))
                .foregroundStyle(.tertiary)
            Text(title)
                .font(.system(size: 14, weight: .semibold))
            Text(detail)
                .font(.system(size: 12))
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 420)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding(30)
    }
}
