import STJCore
import SwiftUI

/// 顶部「目录标签页」条：每个标签对应一个直接打开的图片目录，`+` 打开新目录。
///
/// 设计意图：素材目录只被读取，应用自身的数据（索引/缩略图）都放在
/// `~/Library/Application Support/STJMediaViewer/`，不再把 catalog.db 写进素材目录。
struct FolderTabBar: View {
    @EnvironmentObject private var model: LibraryModel

    var body: some View {
        HStack(spacing: 8) {
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 6) {
                    ForEach(model.folderTabs) { tab in
                        tabButton(tab)
                    }
                }
                .padding(.vertical, 6)
            }

            Button {
                Task { await model.openFolderFromPanel() }
            } label: {
                Image(systemName: "plus")
                    .font(.system(size: 12, weight: .semibold))
                    .frame(width: 26, height: 24)
                    .background(Color.primary.opacity(0.06), in: RoundedRectangle(cornerRadius: 7, style: .continuous))
            }
            .buttonStyle(.plain)
            .help("打开图片文件夹（⌘O / ⌘T）")
            .accessibilityLabel("打开图片文件夹")
        }
        .padding(.horizontal, 10)
        .padding(.top, 2)
        .padding(.bottom, 2)
        .background(Color(nsColor: .windowBackgroundColor))
        .overlay(alignment: .bottom) {
            Divider()
        }
    }

    private var tabShape: RoundedRectangle {
        RoundedRectangle(cornerRadius: 8, style: .continuous)
    }

    private func tabFill(_ isActive: Bool) -> some View {
        tabShape.fill(isActive ? Color.accentColor.opacity(0.16) : Color.primary.opacity(0.05))
    }

    @ViewBuilder
    private func tabBorder(_ isActive: Bool) -> some View {
        tabShape.strokeBorder(isActive ? Color.accentColor.opacity(0.5) : Color.clear, lineWidth: 1)
    }

    @ViewBuilder
    private func tabButton(_ tab: LibraryModel.FolderTab) -> some View {
        let isActive = model.activeFolderTabID == tab.id
        HStack(spacing: 6) {
            Image(systemName: "folder")
                .font(.system(size: 11))
                .foregroundStyle(isActive ? Color.accentColor : Color.secondary)
            Text(tab.name)
                .font(.system(size: 12, weight: isActive ? .semibold : .regular))
                .lineLimit(1)
                .truncationMode(.middle)
                .frame(maxWidth: 190)
            Button {
                model.closeFolderTab(tab.id)
            } label: {
                Image(systemName: "xmark")
                    .font(.system(size: 8, weight: .bold))
                    .frame(width: 14, height: 14)
                    .background(Color.primary.opacity(0.08), in: Circle())
            }
            .buttonStyle(.plain)
            .help("关闭标签页（不移除磁盘上的目录）")
            .accessibilityLabel("关闭标签页 \(tab.name)")
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 5)
        .background(tabFill(isActive))
        .overlay(tabBorder(isActive))
        .contentShape(tabShape)
        .onTapGesture {
            Task { await model.switchFolderTab(tab.id) }
        }
        .contextMenu {
            Button("在 Finder 中显示") {
                NSWorkspace.shared.activateFileViewerSelecting([tab.directory])
            }
            Button("关闭标签页") { model.closeFolderTab(tab.id) }
            Button("关闭其他标签页") {
                model.folderTabs.filter { $0.id != tab.id }.forEach { model.closeFolderTab($0.id) }
            }
        }
        .help(tab.path)
    }
}