import STJCore
import SwiftUI

/// 移动端设置：只放移动端有意义的项（媒体读取、网格密度、关于）。
struct MobileSettingsView: View {
    @ObservedObject var settings: SettingsStore
    @ObservedObject var model: MobileLibraryModel
    @Environment(\.dismiss) private var dismiss
    @State private var webdavPassword = ""
    @State private var passwordNote: String?

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

            Section {
                TextField("服务器地址，如 https://dav.example.com/dav", text: $settings.webdavEndpoint)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
                    .keyboardType(.URL)
                TextField("用户名", text: $settings.webdavUsername)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
                SecureField(settings.hasWebDAVPassword ? "口令（已保存，留空则不修改）" : "口令", text: $webdavPassword)
                TextField("远端根目录，如 photos", text: $settings.webdavRemoteRoot)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
                Picker("上传布局", selection: $settings.webdavLayout) {
                    ForEach(CloudLayout.allCases, id: \.rawValue) { layout in
                        Text(layout.title).tag(layout.rawValue)
                    }
                }
                Button("保存口令") {
                    if webdavPassword.isEmpty {
                        passwordNote = "请先输入口令"
                    } else {
                        settings.setWebDAVPassword(webdavPassword)
                        webdavPassword = ""
                        passwordNote = "口令已保存到钥匙串"
                    }
                }
                if settings.hasWebDAVPassword {
                    Button("清除口令", role: .destructive) {
                        settings.setWebDAVPassword("")
                        passwordNote = "口令已清除"
                    }
                }
                if let passwordNote {
                    Text(passwordNote).font(.caption).foregroundStyle(.secondary)
                }
            } header: {
                Text("云相册（WebDAV）")
            } footer: {
                Text("配置后可在侧栏「工具 → 云相册」浏览远端相册、预览、导入到素材库，或把本地所选素材上传到云端。")
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
