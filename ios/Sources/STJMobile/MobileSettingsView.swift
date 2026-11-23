import STJCore
import SwiftUI

/// 移动端设置：只放移动端有意义的项（媒体读取、网格密度、关于）。
struct MobileSettingsView: View {
    @ObservedObject var settings: SettingsStore
    @ObservedObject var model: MobileLibraryModel
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        Form {
            Section {
                Toggle("RAW+JPG 合并显示", isOn: $settings.foldRawPairs)
                    .onChange(of: settings.foldRawPairs) { _, _ in model.reloadFilterPreferences() }
                Toggle("图片与视频分库显示", isOn: $settings.splitMediaKinds)
            } header: {
                Text("素材读取")
            } footer: {
                Text("合并显示时优先显示 RAW 文件，JPG 伴生文件仅在导出时配对。")
            }

            Section("网格") {
                Picker("密度", selection: $settings.density) {
                    Text("小").tag(LibraryDensity.small)
                    Text("中").tag(LibraryDensity.medium)
                    Text("大").tag(LibraryDensity.large)
                }
                .pickerStyle(.segmented)
                .onChange(of: settings.density) { _, newValue in model.density = newValue }
            }

            Section("关于") {
                LabeledContent("版本", value: Self.appVersion)
                LabeledContent("平台", value: "iPadOS / iOS 17+")
                LabeledContent("数据格式", value: "catalog.db（与 Windows / macOS 版互通）")
                if let directory = model.directory {
                    LabeledContent("素材库") { Text(directory.lastPathComponent) }
                }
            }
        }
        .navigationTitle("设置")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .confirmationAction) { Button("完成") { dismiss() } }
        }
    }

    static var appVersion: String {
        let version = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "0.1"
        return version
    }
}
