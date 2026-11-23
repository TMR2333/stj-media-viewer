import AppKit
import STJCore
import SwiftUI

/// 网格与框选共用的坐标系名称。
enum LibraryGeometry {
    static let gridSpace = "libraryGrid"
}

struct LibraryContentView: View {
    @EnvironmentObject private var model: LibraryModel
    @EnvironmentObject private var settings: SettingsStore

    @State private var showsFilters = false
    @State private var searchFocusRequested = false
    @State private var cardFrames: [String: CGRect] = [:]
    @State private var marqueeStart: CGPoint?
    @State private var marqueeRect: CGRect?

    private var columns: [GridItem] {
        let minimum = model.density.thumbnailMinWidth
        return [GridItem(.adaptive(minimum: minimum, maximum: minimum * 1.7), spacing: 12, alignment: .top)]
    }

    var body: some View {
        VStack(spacing: 0) {
            FolderTabBar()
            if let group = model.expandedGroup {
                groupBanner(group)
            }
            headerBar
            Divider()
            content
            Divider()
            statusBar
        }
        .background(Color(nsColor: .windowBackgroundColor))
        .toolbar { toolbarContent }
        .task {
            #if DEBUG
            // 仅 Debug：无辅助功能权限时用启动参数验证搜索框聚焦与过滤联动。
            if UserDefaults.standard.bool(forKey: "focusSearchOnLaunch") {
                try? await Task.sleep(nanoseconds: 900_000_000)
                searchFocusRequested.toggle()
            }
            #endif
        }
        .sheet(isPresented: $model.isRemoteBrowserPresented) {
            RemoteBrowserView().environmentObject(model)
        }
        .onChange(of: model.searchFocused) { _, focused in
            if focused {
                searchFocusRequested.toggle()
                model.searchFocused = false
            }
        }
    }

    // MARK: 顶部工具条

    @ToolbarContentBuilder
    private var toolbarContent: some ToolbarContent {
        ToolbarItemGroup(placement: .primaryAction) {
            Button {
                showsFilters.toggle()
            } label: {
                Label("筛选", systemImage: model.filter.isEmpty ? "line.3.horizontal.decrease.circle" : "line.3.horizontal.decrease.circle.fill")
            }
            .help("筛选面板")
            .popover(isPresented: $showsFilters, arrowEdge: .bottom) {
                FilterPanelView()
                    .environmentObject(model)
                    .environmentObject(settings)
            }

            sortMenu

            Picker("视图密度", selection: $model.density) {
                ForEach(LibraryDensity.allCases) { density in
                    Image(systemName: density.systemImage).tag(density)
                }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .help("视图密度：小 / 中 / 大 / 列表")

            Button {
                Task { await model.reloadNow(reason: "手动刷新") }
            } label: {
                Label("刷新", systemImage: "arrow.clockwise")
            }
            .help("刷新素材库（⌘R）")
            .disabled(model.catalog == nil)

            Button {
                model.isBatchEditorPresented = true
            } label: {
                Label("批量填写", systemImage: "square.and.pencil")
            }
            .help("批量填写所选素材（⇧⌘E）")
            .disabled(model.selection.isEmpty)

            Button {
                model.openGallery()
            } label: {
                Label("画廊", systemImage: "rectangle.expand.vertical")
            }
            .help("画廊查看（⇧⌘G）")
            .disabled(model.selection.isEmpty)

            Button {
                model.toggleTimeline()
            } label: {
                Label("时间线", systemImage: "calendar.day.timeline.left")
            }
            .help("时间线视图（⌥⌘T）")
            .buttonStyle(.borderless)
            .foregroundStyle(model.showsTimeline ? Color.accentColor : Color.primary)

            Button {
                model.toggleMap()
            } label: {
                Label("地图", systemImage: "map")
            }
            .help("地图视图（⌥⌘M）")
            .buttonStyle(.borderless)
            .foregroundStyle(model.showsMap ? Color.accentColor : Color.primary)

            Button {
                settings.showsInspector.toggle()
            } label: {
                Label("检视面板", systemImage: "sidebar.trailing")
            }
            .help("显示/隐藏检视面板（⌘⌥I）")
        }
    }

    private var sortMenu: some View {
        Menu {
            Picker("排序字段", selection: Binding(
                get: { model.sort.key },
                set: { key in
                    model.sort = SortDescriptor(key: key, ascending: model.sort.ascending)
                }
            )) {
                ForEach(SortKey.allCases) { key in
                    Text(key.title).tag(key)
                }
            }
            Divider()
            Toggle("升序", isOn: Binding(
                get: { model.sort.ascending },
                set: { model.sort.ascending = $0 }
            ))
        } label: {
            Label(model.sort.key.title, systemImage: "arrow.up.arrow.down")
        }
        .help("排序方式")
    }

    // MARK: 面包屑 + 快捷筛选

    private var headerBar: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 8) {
                NativeSearchField(text: $model.filter.searchText,
                                  placeholder: "搜索文件名、关键词、城市",
                                  focusTrigger: searchFocusRequested)
                    .frame(width: 240)
                Image(systemName: "folder")
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
                Text(model.breadcrumb)
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Spacer()
                if model.filter.activeCount > 0 {
                    Button("清除筛选") { model.clearFilters() }
                        .buttonStyle(.link)
                        .font(.system(size: 11))
                }
            }
            ScrollView(.horizontal, showsIndicators: false) {
                GlassCluster(spacing: 6) {
                HStack(spacing: 6) {
                    FilterChip(title: "精选", symbol: "star.fill", isActive: model.filter.pick == 1) {
                        var next = model.filter
                        next.pick = next.pick == 1 ? nil : 1
                        model.filter = next
                    }
                    FilterChip(title: "废片", symbol: "trash", isActive: model.filter.pick == 0) {
                        var next = model.filter
                        next.pick = next.pick == 0 ? nil : 0
                        model.filter = next
                    }
                    FilterChip(title: "评分 ≥ 4", symbol: "star.leadinghalf.filled",
                               isActive: model.filter.ratings == Set([4, 5])) {
                        var next = model.filter
                        next.ratings = next.ratings == Set([4, 5]) ? [] : Set([4, 5])
                        model.filter = next
                    }
                    FilterChip(title: "已调色", symbol: "paintpalette", isActive: model.filter.postProcesses == [2]) {
                        var next = model.filter
                        next.postProcesses = next.postProcesses == [2] ? [] : [2]
                        model.filter = next
                    }
                    FilterChip(title: "Log 素材", symbol: "camera.filters", isActive: model.filter.logMode == true) {
                        var next = model.filter
                        next.logMode = next.logMode == true ? nil : true
                        model.filter = next
                    }
                    FilterChip(title: "城市未填写", symbol: "mappin.slash",
                               isActive: model.filter.unsetDimensions.contains(.city)) {
                        var next = model.filter
                        if next.unsetDimensions.contains(.city) {
                            next.unsetDimensions.remove(.city)
                        } else {
                            next.unsetDimensions.insert(.city)
                        }
                        model.filter = next
                    }
                    FilterChip(title: "主题未填写", symbol: "tag.slash",
                               isActive: model.filter.unsetDimensions.contains(.theme)) {
                        var next = model.filter
                        if next.unsetDimensions.contains(.theme) {
                            next.unsetDimensions.remove(.theme)
                        } else {
                            next.unsetDimensions.insert(.theme)
                        }
                        model.filter = next
                    }
                    Divider().frame(height: 16)
                    ForEach(model.filter.years.sorted(), id: \.self) { year in
                        FilterChip(title: year, symbol: "calendar") {
                            var next = model.filter
                            next.years.remove(year)
                            model.filter = next
                        }
                    }
                    ForEach(model.filter.cities.sorted(), id: \.self) { city in
                        FilterChip(title: city, symbol: "mappin") {
                            var next = model.filter
                            next.cities.remove(city)
                            model.filter = next
                        }
                    }
                    ForEach(model.filter.themes.sorted(), id: \.self) { theme in
                        FilterChip(title: theme, symbol: "tag") {
                            var next = model.filter
                            next.themes.remove(theme)
                            model.filter = next
                        }
                    }
                }
                .padding(.vertical, 1)
                }
            }
        }
        .padding(.horizontal, 14)
        .padding(.top, 8)
        .padding(.bottom, 6)
    }

    private func groupBanner(_ group: MergeGroup) -> some View {
        HStack(spacing: 8) {
            Image(systemName: "rectangle.stack.fill").foregroundStyle(Color.accentColor)
            Text("正在查看合并组“\(group.name)”的 \(group.count) 个成员")
                .font(.system(size: 11))
            Spacer()
            Button("收起合并组") { model.expandedGroup = nil }
                .buttonStyle(.bordered)
                .controlSize(.small)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 7)
        .background(Color.accentColor.opacity(0.12))
    }

    // MARK: 内容

    @ViewBuilder
    private var content: some View {
        if model.dataDirectory == nil {
            EmptyLibraryView {
                Task { await model.openFromPanel() }
            }
        } else if model.assets.isEmpty {
            ContentUnavailableView {
                Label(model.filter.isEmpty ? "素材库是空的" : "没有符合条件的素材",
                      systemImage: model.filter.isEmpty ? "photo.on.rectangle.angled" : "line.3.horizontal.decrease.circle")
            } description: {
                Text(model.filter.isEmpty ? "这个目录里还没有被索引的图片或视频。" : "试着调整筛选条件或清除搜索关键词。")
            } actions: {
                if !model.filter.isEmpty {
                    Button("清除筛选") { model.clearFilters() }
                }
            }
        } else if model.showsMap {
            PhotoMapView()
        } else if model.showsTimeline {
            timelineContent
        } else if model.density.isList {
            listContent
        } else {
            gridContent
        }
    }

    private var gridContent: some View {
        ScrollView {
            LazyVGrid(columns: columns, alignment: .leading, spacing: 12) {
                ForEach(model.assets) { asset in
                    AssetCardView(asset: asset, density: model.density, isSelected: model.selection.contains(asset.id))
                }
            }
            .padding(14)
            .background(marqueeSurface)
            .overlay(marqueeOverlay)
            .coordinateSpace(name: LibraryGeometry.gridSpace)
            .onPreferenceChange(CardFramePreferenceKey.self) { cardFrames = $0 }

        }
        .scrollContentBackground(.hidden)
    }

    // MARK: 时间线

    /// 按拍摄日分组的时间线：每天一条横向缩略图带，日期倒序，未标注日期排最后。
    private var timelineContent: some View {
        ScrollView {
            LazyVStack(alignment: .leading, spacing: 16, pinnedViews: [.sectionHeaders]) {
                ForEach(Timeline.sections(from: model.assets)) { section in
                    Section {
                        ScrollView(.horizontal, showsIndicators: false) {
                            LazyHStack(alignment: .top, spacing: 12) {
                                ForEach(section.assets) { asset in
                                    AssetCardView(asset: asset,
                                                  density: model.density,
                                                  isSelected: model.selection.contains(asset.id))
                                        .frame(width: timelineCardWidth)
                                }
                            }
                            .padding(.horizontal, 14)
                            .padding(.bottom, 6)
                        }
                    } header: {
                        HStack(spacing: 8) {
                            Image(systemName: "calendar.day.timeline.left")
                                .font(.system(size: 11))
                                .foregroundStyle(.secondary)
                            Text(section.title)
                                .font(.system(size: 12, weight: .semibold))
                            Text("\(section.count) 张")
                                .font(.system(size: 10))
                                .foregroundStyle(.secondary)
                            Spacer()
                        }
                        .padding(.horizontal, 14)
                        .padding(.vertical, 6)
                        .background(.bar)
                    }
                }
            }
            .padding(.vertical, 8)
        }
        .scrollContentBackground(.hidden)
    }

    private var timelineCardWidth: CGFloat {
        CGFloat(model.density.thumbnailMinWidth) + 44
    }

    // MARK: 框选（橡皮筋）

    /// 空白处按下拖动即开始框选；卡片上的点击/拖拽不受影响。
    private var marqueeSurface: some View {
        Color.clear
            .contentShape(Rectangle())
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { value in
                        if marqueeStart == nil {
                            marqueeStart = value.startLocation
                        }
                        guard let start = marqueeStart else { return }
                        marqueeRect = MarqueeGeometry.rect(from: start, to: value.location)
                    }
                    .onEnded { value in
                        defer {
                            marqueeStart = nil
                            marqueeRect = nil
                        }
                        guard let start = marqueeStart else {
                            model.clearSelection()
                            return
                        }
                        let rect = MarqueeGeometry.rect(from: start, to: value.location)
                        guard !MarqueeGeometry.isClick(rect) else {
                            model.clearSelection()
                            return
                        }
                        let modifiers = NSEvent.modifierFlags
                        model.selection = MarqueeGeometry.selection(in: rect,
                                                                   frames: cardFrames,
                                                                   current: model.selection,
                                                                   shift: modifiers.contains(.shift),
                                                                   command: modifiers.contains(.command))
                        if !model.selection.isEmpty {
                            AppLog.debug("框选：命中 \(model.selection.count) 张")
                        }
                    }
            )
    }

    @ViewBuilder
    private var marqueeOverlay: some View {
        if let rect = marqueeRect, !MarqueeGeometry.isClick(rect) {
            RoundedRectangle(cornerRadius: 3, style: .continuous)
                .fill(Color.accentColor.opacity(0.12))
                .overlay(
                    RoundedRectangle(cornerRadius: 3, style: .continuous)
                        .strokeBorder(Color.accentColor, lineWidth: 1)
                )
                .frame(width: rect.width, height: rect.height)
                .position(x: rect.midX, y: rect.midY)
                .allowsHitTesting(false)
        }
    }

    private var listContent: some View {
        List(selection: $model.selection) {
            ForEach(model.assets) { asset in
                AssetListRow(asset: asset, isSelected: model.selection.contains(asset.id))
                    .tag(asset.id)
                    .onTapGesture(count: 2) {
                        model.selection = [asset.id]
                        model.openSelectionInDefaultApp()
                    }
                    .contextMenu {
                        Button("用默认应用打开") { model.selection = [asset.id]; model.openSelectionInDefaultApp() }
                        Button("在 Finder 中显示") { model.selection = [asset.id]; model.revealSelection() }
                        Divider()
                        Button("移到废纸篓", role: .destructive) { model.selection = [asset.id]; model.deleteSelection() }
                    }
            }
        }
        .listStyle(.inset)
    }

    // MARK: 状态栏

    private var statusBar: some View {
        HStack(spacing: 10) {
            if model.isLoading {
                ProgressView().controlSize(.small).scaleEffect(0.7)
            }
            if let progress = model.aiTagProgress {
            HStack(spacing: 6) {
                ProgressView().controlSize(.small)
                Text("AI 打标 \(progress.done)/\(progress.total)")
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
            }
        }
        if model.isImporting {
                ProgressView().controlSize(.small).scaleEffect(0.7)
                if let progress = model.importProgress, progress.total > 0 {
                    Text("导入中 \(progress.processed)/\(progress.total)")
                        .font(.system(size: 10))
                        .foregroundStyle(.secondary)
                        .monospacedDigit()
                } else {
                    Text("正在扫描…")
                        .font(.system(size: 10))
                        .foregroundStyle(.secondary)
                }
            }
            if model.isCloudSyncing, let progress = model.cloudSyncProgress {
                ProgressView(value: Double(progress.completed), total: Double(max(progress.total, 1)))
                    .progressViewStyle(.linear)
                    .frame(width: 90)
                Text("云同步 \(progress.completed)/\(progress.total) · \(ByteCountFormatter.string(fromByteCount: progress.bytesUploaded, countStyle: .file))")
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
            }
            Text(model.statusMessage ?? model.statusLine)
                .font(.system(size: 10))
                .foregroundStyle(model.statusMessage == nil ? AnyShapeStyle(.secondary) : AnyShapeStyle(Color.accentColor))
                .lineLimit(1)
            Spacer()
            if model.selection.count == 1, let asset = model.primarySelection {
                Text("\(asset.fileName) · \(asset.sizeText)")
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            if let update = model.availableUpdate {
                Button {
                    model.openUpdatePage()
                } label: {
                    Label("新版本 \(update.version)", systemImage: "arrow.down.circle.fill")
                        .font(.system(size: 10, weight: .medium))
                }
                .buttonStyle(.plain)
                .foregroundStyle(Color.accentColor)
                .help("打开发行版页面查看 \(update.name)")
            }
            // 与 Windows 状态栏一致：素材 N / 筛选 N / 已选 N（数值用等宽数字防抖动）。
            HStack(spacing: 10) {
                Text("素材 \(model.totalCount)")
                if model.filter.activeCount > 0 || !model.filter.searchText.isEmpty {
                    Text("筛选 \(model.assets.count)").foregroundStyle(Color.accentColor)
                }
                if !model.selection.isEmpty {
                    Text("已选 \(model.selection.count)")
                }
            }
            .font(.system(size: 10))
            .monospacedDigit()
            .foregroundStyle(.secondary)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 5)
        .stjGlassCapsule(surface: .regular)
        .padding(.horizontal, 8)
        .padding(.bottom, 6)
    }
}

extension LibraryDensity {
    var systemImage: String {
        switch self {
        case .small: return "square.grid.3x3"
        case .medium: return "square.grid.2x2"
        case .large: return "rectangle.grid.1x2"
        case .list: return "list.bullet"
        }
    }
}