import STJCore
import SwiftUI

struct SidebarView: View {
    @EnvironmentObject private var model: LibraryModel
    @ObservedObject private var settings = SettingsStore.shared

    private var selectionBinding: Binding<SidebarSelection?> {
        Binding(
            get: { model.sidebarSelection },
            set: { value in
                if let value {
                    model.sidebarSelection = value
                }
            }
        )
    }

    var body: some View {
        List(selection: selectionBinding) {
            Section("素材库") {
                row(title: SidebarSection.all.title,
                    symbol: SidebarSection.all.symbol,
                    count: count(for: .all))
                    .tag(SidebarSelection.section(.all))
                if settings.splitMediaKinds {
                    row(title: SidebarSection.images.title,
                        symbol: SidebarSection.images.symbol,
                        count: count(for: .images))
                        .tag(SidebarSelection.section(.images))
                    row(title: SidebarSection.videos.title,
                        symbol: SidebarSection.videos.symbol,
                        count: count(for: .videos))
                        .tag(SidebarSelection.section(.videos))
                }
            }

            Section("智能分类") {
                ForEach([SidebarSection.pick, .rejected, .uncategorized]) { section in
                    row(title: section.title,
                        symbol: section.symbol,
                        count: count(for: section))
                        .tag(SidebarSelection.section(section))
                }
            }

            if !model.mergeGroups.isEmpty {
                Section("合并组") {
                    ForEach(model.mergeGroups) { group in
                        row(title: group.name.isEmpty ? "合并组" : group.name,
                            symbol: "rectangle.stack",
                            count: group.count)
                            .tag(SidebarSelection.mergeGroup(group.id))
                            .contextMenu {
                                Button("展开/收起") { model.toggleGroupExpansion(group) }
                            }
                    }
                }
            }

            Section("合集") {
                ForEach(model.collections) { collection in
                    row(title: collection.name,
                        symbol: "folder",
                        count: collection.count)
                        .tag(SidebarSelection.collection(collection.id))
                        .contextMenu {
                            Button("重命名…") { model.renameCollection(collection) }
                            Button("删除合集", role: .destructive) { model.deleteCollection(collection) }
                        }
                }
                Button {
                    model.createCollection()
                } label: {
                    Label("新建合集…", systemImage: "plus.circle")
                        .font(.system(size: 12))
                }
                .buttonStyle(.plain)
                .foregroundStyle(.secondary)
            }

            Section("工具") {
                toolRow("扫描导入…", symbol: "folder.badge.plus", shortcut: "⌘I") {
                    model.importLibraryFolder()
                }
                toolRow("快速批量填写…", symbol: "rectangle.and.pencil.and.ellipsis", shortcut: "⇧⌘E",
                        enabled: !model.selection.isEmpty) {
                    model.isBatchEditorPresented = true
                }
                toolRow("AI 打标所选", symbol: "sparkles", shortcut: "⌥⌘A",
                        enabled: !model.selection.isEmpty && !model.isAITagging) {
                    model.aiTagSelection()
                }
                toolRow("导出带水印…", symbol: "seal", shortcut: "⌥⌘W",
                        enabled: !model.selection.isEmpty) {
                    model.exportWatermarkedSelection()
                }
                toolRow("查找重复项…", symbol: "square.on.square", shortcut: "⌥⌘D",
                        enabled: model.dataDirectory != nil && !model.isDuplicateScanning) {
                    model.findDuplicates()
                }
                toolRow("上传所选到云端", symbol: "icloud.and.arrow.up", shortcut: "⌥⌘U",
                        enabled: !model.selection.isEmpty && !model.isCloudSyncing) {
                    model.uploadSelectionToWebDAV()
                }
                toolRow("浏览云端…", symbol: "icloud", shortcut: "⌥⌘L",
                        enabled: model.dataDirectory != nil && !model.isRemoteLoading) {
                    model.openRemoteBrowser()
                }
                toolRow("备份素材库…", symbol: "externaldrive.badge.timemachine", shortcut: "⇧⌘B",
                        enabled: model.dataDirectory != nil) {
                    model.backupLibrary()
                }
                toolRow("从备份恢复…", symbol: "arrow.counterclockwise.circle", shortcut: "⌥⌘B",
                        enabled: model.dataDirectory != nil) {
                    model.restoreLibrary()
                }
            }
        }
        .listStyle(.sidebar)
        .navigationSplitViewColumnWidth(min: 208, ideal: 236, max: 300)
        .safeAreaInset(edge: .bottom) {
            VStack(alignment: .leading, spacing: 6) {
                Divider()
                HStack(spacing: 8) {
                    Image(systemName: "externaldrive")
                        .foregroundStyle(.secondary)
                    VStack(alignment: .leading, spacing: 1) {
                        Text(model.dataDirectory?.lastPathComponent ?? "未打开素材库")
                            .font(.system(size: 11, weight: .medium))
                            .lineLimit(1)
                        Text(model.dataDirectory?.path ?? "请选择包含 catalog.db 的目录")
                            .font(.system(size: 9))
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                            .truncationMode(.middle)
                    }
                    Spacer()
                    Button {
                        Task { await model.openFromPanel() }
                    } label: {
                        Image(systemName: "folder")
                    }
                    .buttonStyle(.plain)
                    .help("切换素材库目录")
                }
                .padding(.horizontal, 10)
                .padding(.bottom, 8)
            }
            .background(.bar)
        }
    }

    private func row(title: String, symbol: String, count: Int) -> some View {
        HStack(spacing: 7) {
            Image(systemName: symbol)
                .font(.system(size: 11))
                .frame(width: 16)
                .foregroundStyle(.secondary)
            Text(title)
                .font(.system(size: 12))
                .lineLimit(1)
            Spacer(minLength: 4)
            if count > 0 {
                Text("\(count)")
                    .font(.system(size: 10))
                    .monospacedDigit()
                    .foregroundStyle(.secondary)
            }
        }
    }

    private func count(for section: SidebarSection) -> Int {
        switch section {
        case .all: return model.facets.total
        case .images: return kindCount("1")
        case .videos: return kindCount("2")
        case .pick: return model.facets.picked
        case .rejected: return model.facets.rejected
        case .uncategorized: return model.facets.unsetCounts[.collection] ?? 0
        }
    }

    private func kindCount(_ value: String) -> Int {
        model.facets.kinds.first { $0.value == value }?.count ?? 0
    }

    /// 侧栏「工具」区的一行：图标 + 标题 + 快捷键提示，禁用时整体变淡。
    private func toolRow(_ title: String, symbol: String, shortcut: String?, enabled: Bool = true,
                         action: @escaping () -> Void = {}) -> some View {
        Button(action: action) {
            HStack(spacing: 7) {
                Image(systemName: symbol)
                    .font(.system(size: 11))
                    .frame(width: 16)
                    .foregroundStyle(.secondary)
                Text(title)
                    .font(.system(size: 12))
                    .lineLimit(1)
                Spacer(minLength: 4)

            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!enabled)
        .opacity(enabled ? 1 : 0.45)
        .help(shortcut.map { "\(title.replacingOccurrences(of: "…", with: ""))（\($0)）" } ?? title)
    }
}
