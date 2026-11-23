import AppKit
import STJCore
import SwiftUI

/// 完整菜单栏：文件 / 编辑 / 查看 / 素材 / 窗口 / 帮助，含 Windows 版对应快捷键。
struct AppCommands: Commands {
    @ObservedObject var model: LibraryModel
    @ObservedObject var settings: SettingsStore

    var body: some Commands {
        CommandGroup(replacing: .newItem) {
            Button("打开图片文件夹…") {
                Task { await model.openFolderFromPanel() }
            }
            .keyboardShortcut("o", modifiers: .command)
            Button("新建目录标签页…") {
                Task { await model.openFolderFromPanel(multiple: false) }
            }
            .keyboardShortcut("t", modifiers: .command)
            Button("关闭标签页") {
                if let id = model.activeFolderTabID { model.closeFolderTab(id) }
            }
            .keyboardShortcut("w", modifiers: [.command, .shift])
            .disabled(model.activeFolderTabID == nil)
            Divider()
            Button("打开素材库文件（catalog.db）…") {
                Task { await model.openFromPanel() }
            }
            .keyboardShortcut("o", modifiers: [.command, .option])
            Button("导入 / 扫描素材目录…") {
                model.importLibraryFolder()
            }
            .keyboardShortcut("i", modifiers: .command)
            Divider()
            Button("刷新素材库") {
                Task { await model.reloadNow(reason: "菜单刷新") }
            }
            .keyboardShortcut("r", modifiers: .command)
            Divider()
            Button("导出设置与图片数据…") { model.exportSettingsExchange() }
                .keyboardShortcut("x", modifiers: [.command, .shift])
                .disabled(model.catalog == nil)
            Button("导入设置与图片数据…") { model.importSettingsExchange() }
                .keyboardShortcut("i", modifiers: [.command, .shift])
                .disabled(model.catalog == nil || model.isExchangeRunning)
        }

        CommandGroup(replacing: .pasteboard) {
            Button("全选素材") { model.selectAll() }
                .keyboardShortcut("a", modifiers: .command)
            Button("取消选择") { model.clearSelection() }
                .keyboardShortcut("a", modifiers: [.command, .shift])
        }

        CommandGroup(after: .textEditing) {
            Button("搜索素材") {
                model.searchFocused = true
            }
            .keyboardShortcut("f", modifiers: .command)
            Button("批量填写…") {
                model.isBatchEditorPresented = true
            }
            .keyboardShortcut("e", modifiers: [.command, .shift])
            .disabled(model.selection.isEmpty)
        }

        CommandGroup(replacing: .sidebar) {
            Button(model.columnVisibility == .detailOnly ? "显示侧边栏" : "隐藏侧边栏") {
                model.columnVisibility = model.columnVisibility == .detailOnly ? .all : .detailOnly
            }
            .keyboardShortcut("s", modifiers: [.command, .option])
            Button(settings.showsInspector ? "隐藏检视面板" : "显示检视面板") {
                settings.showsInspector.toggle()
            }
            .keyboardShortcut("i", modifiers: [.command, .option])
            Button("画廊查看") {
                model.openGallery()
            }
            .keyboardShortcut("g", modifiers: [.command, .shift])
            .disabled(model.selection.isEmpty && model.galleryAssetID == nil)
            Button(settings.foldRawPairs ? "显示 RAW+JPG 配对" : "折叠 RAW+JPG 配对") {
                settings.foldRawPairs.toggle()
                model.reloadForPreferenceChange()
            }
            Divider()
            Button("网格视图") { setDensity(.medium) }
                .keyboardShortcut("1", modifiers: .command)
            Button("小网格") { setDensity(.small) }
                .keyboardShortcut("2", modifiers: .command)
            Button("大网格") { setDensity(.large) }
                .keyboardShortcut("3", modifiers: .command)
            Button("列表视图") { setDensity(.list) }
                .keyboardShortcut("4", modifiers: .command)
            Divider()
            Toggle("时间线视图", isOn: $model.showsTimeline)
                .keyboardShortcut("t", modifiers: [.command, .option])
            Toggle("地图视图", isOn: $model.showsMap)
                .keyboardShortcut("m", modifiers: [.command, .option])
            Divider()
        }

        CommandMenu("素材") {
            Button("AI 打标所选…") { model.aiTagSelection() }
                .keyboardShortcut("a", modifiers: [.command, .option])
                .disabled(model.selection.isEmpty || model.isAITagging)
            Menu("评分") {
                ForEach((0...5).reversed(), id: \.self) { value in
                    Button(value == 0 ? "清除评分" : String(repeating: "★", count: value)) {
                        model.setRating(value)
                    }
                }
            }
            .disabled(model.selection.isEmpty)
            Menu("选用状态") {
                Button("精选") { model.setPick(1) }
                Button("废片") { model.setPick(0) }
                Button("未标记") { model.setPick(nil) }
            }
            .disabled(model.selection.isEmpty)
            Menu("加入合集") {
                if model.collections.isEmpty {
                    Text("暂无合集")
                } else {
                    ForEach(model.collections) { collection in
                        Button(collection.name) {
                            model.addSelectionToCollection(collection.id)
                        }
                    }
                }
                Divider()
                Button("新建合集…") { model.createCollection() }
            }
            .disabled(model.selection.isEmpty)
            Divider()
            Button("展开合并组") {
                if let group = model.mergeGroups.first {
                    model.toggleGroupExpansion(group)
                }
            }
            .disabled(model.mergeGroups.isEmpty || !model.selection.isEmpty)
            Button("收起合并组") { model.expandedGroup = nil }
                .disabled(model.expandedGroup == nil)
            Divider()
            Button("写入 XMP 侧车") { model.writeXMP() }
                .keyboardShortcut("e", modifiers: [.command, .option])
                .disabled(model.selection.isEmpty)
            Button("导出配对（JPG+RAW）…") { model.exportPairs() }
                .keyboardShortcut("p", modifiers: [.command, .option])
                .disabled(model.selection.isEmpty)
            Divider()
            Button("查找重复项…") { model.findDuplicates() }
                .keyboardShortcut("d", modifiers: [.command, .option])
                .disabled(model.catalog == nil || model.isDuplicateScanning)
            Menu("整理所选") {
                Button("复制到…") { model.organizeSelection(mode: .copy) }
                Button("移动到…") { model.organizeSelection(mode: .move) }
            }
            .disabled(model.selection.isEmpty || model.isOrganizing)
            Divider()
            if model.isCloudSyncing {
                Button("取消云同步") { model.cancelCloudSync() }
            } else {
                Button("云相册…") { model.presentCloudAlbum() }
                    .keyboardShortcut("l", modifiers: [.command, .option])
                Button("上传所选到云相册") { model.uploadSelectionToWebDAV() }
                    .keyboardShortcut("u", modifiers: [.command, .option])
                    .disabled(model.selection.isEmpty)
            }
            Divider()
            Button("用默认应用打开") { model.openSelectionInDefaultApp() }
                .keyboardShortcut(.return, modifiers: .command)
                .disabled(model.selection.isEmpty)
            Button("在 Finder 中显示") { model.revealSelection() }
                .keyboardShortcut("r", modifiers: [.command, .option])
                .disabled(model.selection.isEmpty)
            Divider()
            Button("移到废纸篓") { model.deleteSelection() }
                .keyboardShortcut(.delete, modifiers: .command)
                .disabled(model.selection.isEmpty)
        }

        CommandGroup(replacing: .help) {
            Button("打开日志目录") {
                NSWorkspace.shared.open(AppLog.directoryURL)
            }
            Link("项目文档（gitcode）",
                 destination: URL(string: "https://gitcode.com/TMR2333/stj-media-viewer-macos")!)
        }
    }

    private func setDensity(_ density: LibraryDensity) {
        model.listDensityOverride = density
    }
}