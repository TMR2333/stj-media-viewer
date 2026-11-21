import STJCore
import SwiftUI

/// 首次启动引导：选择现有素材库 / 导入新目录 / 快捷键提示。
struct OnboardingView: View {
    @EnvironmentObject private var model: LibraryModel
    @EnvironmentObject private var settings: SettingsStore
    @Environment(\.dismiss) private var dismiss

    @State private var step = 0

    private let steps: [(title: String, detail: String, symbol: String)] = [
        ("打开已有素材库", "选择包含 catalog.db 的文件夹，直接读取 Windows 版建的库（评分、主题、合集都会带过来）。", "externaldrive.connected.to.line.below"),
        ("或者导入新目录", "扫描一个文件夹，自动抽取 EXIF / 视频信息，生成 catalog.db 与缩略图缓存。", "square.and.arrow.down.on.square"),
        ("用键盘快速浏览", "⌘F 搜索 · ⌘A 全选 · 空格 QuickLook · ⌘⌥I 检视面板 · ⌘⌥T 时间线 · ⌘⌥M 地图 · ⇧⌘G 画廊。", "keyboard"),
    ]

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack(spacing: 12) {
                Image(systemName: "photo.stack")
                    .font(.system(size: 30, weight: .light))
                    .foregroundStyle(Color.accentColor)
                VStack(alignment: .leading, spacing: 2) {
                    Text("欢迎使用 STJ Media Viewer")
                        .font(.title2.weight(.semibold))
                    Text("原生 macOS 素材浏览器 · 与 Windows 版共用同一份素材库")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                }
            }

            Divider()

            VStack(alignment: .leading, spacing: 14) {
                ForEach(Array(steps.enumerated()), id: \.offset) { index, item in
                    HStack(alignment: .top, spacing: 12) {
                        Image(systemName: item.symbol)
                            .font(.system(size: 16))
                            .frame(width: 26, height: 26)
                            .background(Circle().fill(Color.accentColor.opacity(index == step ? 0.22 : 0.10)))
                            .foregroundStyle(Color.accentColor)
                        VStack(alignment: .leading, spacing: 3) {
                            Text(item.title)
                                .font(.system(size: 13, weight: .semibold))
                            Text(item.detail)
                                .font(.system(size: 11.5))
                                .foregroundStyle(.secondary)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                    }
                    .contentShape(Rectangle())
                    .onTapGesture { step = index }
                }
            }

            if let directory = model.dataDirectory {
                Label("当前素材库：\(directory.path)", systemImage: "checkmark.seal.fill")
                    .font(.system(size: 11))
                    .foregroundStyle(.green)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }

            Spacer(minLength: 0)

            HStack {
                Button("稍后再说") { finish() }
                    .keyboardShortcut(.cancelAction)
                Spacer()
                Button("选择数据目录…") { Task { await model.openFromPanel() } }
                Button("导入文件夹…") { model.importLibraryFolder() }
                Button("开始使用") { finish() }
                    .keyboardShortcut(.defaultAction)
                    .buttonStyle(.borderedProminent)
            }
        }
        .padding(24)
        .stjGlass(cornerRadius: 24)
        .padding(12)
        .frame(width: 584, height: 428)
    }

    private func finish() {
        settings.hasCompletedOnboarding = true
        AppLog.breadcrumb("完成首次引导")
        dismiss()
    }
}
