import AppKit
import STJCore
import SwiftUI

struct SettingsView: View {
    @EnvironmentObject private var model: LibraryModel
    @ObservedObject private var settings = SettingsStore.shared

    @State private var apiKeyInput = ""
    @State private var keyStatus = ""
    @State private var davPasswordInput = ""
    @State private var davStatus = ""
    @State private var davSteps: [DiagnosticStep] = []
    @State private var davTesting = false
    @State private var selectedTab: String = {
        // 验收截图辅助：启动参数直接定位到某个设置页（默认不启用）。
        if let tab = UserDefaults.standard.string(forKey: "settingsTab"), !tab.isEmpty {
            return tab
        }
        return "general"
    }()

    var body: some View {
        TabView(selection: $selectedTab) {
            generalTab
                .tabItem { Label("通用", systemImage: "gearshape") }
                .tag("general")
            libraryTab
                .tabItem { Label("媒体库", systemImage: "photo.on.rectangle") }
                .tag("library")
            aiTab
                .tabItem { Label("AI", systemImage: "sparkles") }
                .tag("ai")
            cloudTab
                .tabItem { Label("云同步", systemImage: "cloud") }
                .tag("cloud")
            aboutTab
                .tabItem { Label("关于", systemImage: "info.circle") }
                .tag("about")
        }
        .frame(width: 560, height: 700)
        .onAppear {
            keyStatus = settings.hasAPIKey ? "已保存到钥匙串" : "未设置"
            davStatus = settings.hasWebDAVPassword ? "口令已保存到钥匙串" : "未设置口令"
        }
        .task {
            #if DEBUG
            // 仅 Debug 构建：验收截图用，进入设置页即自动跑一次连接诊断。
            if UserDefaults.standard.bool(forKey: "davDiagnosticsOnLaunch"), davSteps.isEmpty {
                davTesting = true
                let steps = await model.runWebDAVDiagnostics()
                davSteps = steps
                davTesting = false
                davStatus = steps.allSatisfy(\.succeeded) ? "全部通过" : "存在问题"
            }
            #endif
        }
    }

    // MARK: 通用

    private var generalTab: some View {
        Form {
            Picker("外观", selection: $settings.appearance) {
                ForEach(AppearanceMode.allCases) { mode in
                    Text(mode.title).tag(mode)
                }
            }
            Picker("默认视图密度", selection: $settings.density) {
                ForEach(LibraryDensity.allCases) { density in
                    Text(density.title).tag(density)
                }
            }
            Toggle("显示侧边栏", isOn: $settings.showsSidebar)
            Toggle("显示检视面板", isOn: $settings.showsInspector)
            Toggle("相册视图隐藏截图", isOn: $settings.excludesScreenshotsInGallery)
            Text("外观跟随系统时会随 macOS 深浅色自动切换。")
                .font(.system(size: 10))
                .foregroundStyle(.secondary)

            Section("设置与图片数据迁移") {
                HStack(spacing: 8) {
                    Button("导出设置与图片数据…") { model.exportSettingsExchange() }
                    Button("导入设置与图片数据…") { model.importSettingsExchange() }
                    Spacer()
                }
                Text("导出的 JSON 与 Windows 版通用：包含应用设置（外观/密度/排序/面板/RAW 折叠）与图片数据（评分、色标、选用、地点、主题、影调、关键词、标题、调色状态）以及合集归属；不含 API Key、云端口令等密钥，也不会移动图片文件。")
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .formStyle(.grouped)
        .padding()
    }

    // MARK: 媒体库

    private var libraryTab: some View {
        Form {
            Section("数据目录") {
                HStack(spacing: 8) {
                    Image(systemName: "folder")
                        .foregroundStyle(.secondary)
                    Text(model.dataDirectory?.path ?? "未打开素材库")
                        .font(.system(size: 11))
                        .lineLimit(1)
                        .truncationMode(.middle)
                    Spacer()
                }
                HStack(spacing: 8) {
                    Button("切换素材库…") {
                        Task { await model.openFromPanel() }
                    }
                    if let directory = model.dataDirectory {
                        Button("在 Finder 显示") {
                            NSWorkspace.shared.activateFileViewerSelecting([directory])
                        }
                    }
                }
            }
            Section("缓存与日志") {
                LabeledContent("缓存缩略图", value: "\(model.cachedThumbnailCount) 张")
                LabeledContent("QuickLook 兜底", value: "\(model.quickLookFallbackCount) 项")
                LabeledContent("日志文件") {
                    Button("打开日志目录") {
                        NSWorkspace.shared.open(AppLog.directoryURL)
                    }
                    .controlSize(.small)
                }
                Text(AppLog.fileURL.path)
                    .font(.system(size: 9))
                    .foregroundStyle(.tertiary)
                    .textSelection(.enabled)
            }
            Section("水印导出") {
                TextField("水印文字", text: $settings.watermarkText, prompt: Text("STJ Media Viewer"))
                Picker("位置", selection: $settings.watermarkPosition) {
                    ForEach(WatermarkStyle.Position.allCases) { position in
                        Text(position.title).tag(position.rawValue)
                    }
                }
                HStack {
                    Text("不透明度")
                    Slider(value: $settings.watermarkOpacity, in: 0.1...1.0)
                    Text(String(format: "%.0f%%", settings.watermarkOpacity * 100))
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                        .frame(width: 42, alignment: .trailing)
                }
                Toggle("额外显示相机型号", isOn: $settings.watermarkIncludesCameraModel)
                Text("导出时叠加在 JPEG 上（⌥⌘W 导出所选）。文字留空表示不加水印。")
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
            }
            Section("素材读取") {
                Toggle("折叠 RAW+JPG 配对（只显示 RAW）", isOn: $settings.foldRawPairs)
                    .onChange(of: settings.foldRawPairs) { _, _ in
                        model.reloadForPreferenceChange()
                    }
                Text("支持 Windows 版生成的 catalog.db（assets / asset_custom / collections / merge_groups / libraries 表）。修改会直接写回数据库，缩略图缓存沿用素材库下的 Cache/thumbs。")
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .padding()
    }

    // MARK: AI

    private var aiTab: some View {
        Form {
            TextField("接口地址", text: $settings.aiEndpoint, prompt: Text("https://api.openai.com/v1"))
            TextField("模型名称", text: $settings.aiModel, prompt: Text("gpt-4o-mini"))
            SecureField("API Key", text: $apiKeyInput, prompt: Text(settings.hasAPIKey ? "已保存（重新输入可覆盖）" : "粘贴 API Key"))
            HStack(spacing: 8) {
                Button("保存到钥匙串") {
                    let value = apiKeyInput.trimmingCharacters(in: .whitespacesAndNewlines)
                    guard !value.isEmpty else {
                        keyStatus = "请输入 API Key"
                        return
                    }
                    settings.setAPIKey(value)
                    apiKeyInput = ""
                    keyStatus = "已保存到钥匙串"
                }
                Button("清除") {
                    settings.setAPIKey("")
                    keyStatus = "未设置"
                }
                Spacer()
                Text(keyStatus)
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
            }
            Text("API Key 只保存在本机钥匙串（服务名 com.stjarnaperspective.stjmediaviewer），不会写入 catalog.db。AI 打标签属于 P3 阶段，当前版本只做配置。")
                .font(.system(size: 10))
                .foregroundStyle(.secondary)
        }
        .formStyle(.grouped)
        .padding()
    }

    // MARK: 云同步

    private var cloudTab: some View {
        Form {
            Section("服务器") {
                TextField("地址", text: $settings.webdavEndpoint,
                          prompt: Text("https://example.com/dav/"))
                TextField("用户名", text: $settings.webdavUsername)
                SecureField("口令", text: $davPasswordInput,
                            prompt: Text(settings.hasWebDAVPassword ? "已保存（重新输入可覆盖）" : "粘贴 WebDAV 口令"))
                HStack(spacing: 8) {
                    Button("保存口令") {
                        let value = davPasswordInput.trimmingCharacters(in: .whitespacesAndNewlines)
                        guard !value.isEmpty else {
                            davStatus = "请先输入口令"
                            return
                        }
                        settings.setWebDAVPassword(value)
                        davPasswordInput = ""
                        davStatus = "口令已保存到钥匙串"
                    }
                    Button("清除") {
                        settings.setWebDAVPassword("")
                        davStatus = "未设置口令"
                        davSteps = []
                    }
                    Spacer()
                    Text(davStatus)
                        .font(.system(size: 10))
                        .foregroundStyle(.secondary)
                }
            }

            Section("上传布局") {
                Picker("远端目录结构", selection: $settings.webdavLayout) {
                    ForEach(CloudLayout.allCases) { layout in
                        Text(layout.title).tag(layout.rawValue)
                    }
                }
                TextField("远端根目录", text: $settings.webdavRemoteRoot,
                          prompt: Text("留空表示上传到服务器根目录"))
                Text(settings.cloudLayout.remotePathExample)
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
            }

            Section("连接诊断") {
                HStack(spacing: 8) {
                    Button(davTesting ? "诊断中…" : "测试连接") {
                        davTesting = true
                        Task {
                            let steps = await model.runWebDAVDiagnostics()
                            davSteps = steps
                            davTesting = false
                            davStatus = steps.allSatisfy(\.succeeded) ? "全部通过" : "存在问题"
                        }
                    }
                    .disabled(davTesting)
                    Button("清空结果") { davSteps = [] }
                        .disabled(davSteps.isEmpty)
                    Spacer()
                }
                if davSteps.isEmpty {
                    Text("依次检查：连接 / 鉴权 → 列表 → 上传探针 → 核验大小 → 清理。上传所选素材请用「素材 → 上传所选到 WebDAV」（⌥⌘U）。")
                        .font(.system(size: 10))
                        .foregroundStyle(.secondary)
                } else {
                    ScrollView {
                        VStack(alignment: .leading, spacing: 6) {
                            ForEach(davSteps) { step in
                                HStack(alignment: .firstTextBaseline, spacing: 6) {
                                    Image(systemName: step.succeeded ? "checkmark.circle.fill" : "xmark.circle.fill")
                                        .foregroundStyle(step.succeeded ? Color.green : Color.red)
                                    VStack(alignment: .leading, spacing: 1) {
                                        Text(step.title)
                                            .font(.system(size: 11, weight: .medium))
                                        Text(step.detail)
                                            .font(.system(size: 10))
                                            .foregroundStyle(.secondary)
                                            .lineLimit(3)
                                    }
                                }
                            }
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .frame(height: 180)
                }
            }
        }
        .formStyle(.grouped)
        .padding()
    }

    // MARK: 关于

    private var aboutTab: some View {
        VStack(spacing: 10) {
            Image(nsImage: NSImage(named: "AppIcon") ?? NSImage())
                .resizable()
                .frame(width: 64, height: 64)
                .clipShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
            Text("STJ Media Viewer").font(.title3.weight(.semibold))
            Text("版本 \(version)（build \(buildNumber)）· macOS 原生版")
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
            Text("Swift 6 · SwiftUI · 最低 macOS 14\n与 Windows 版共用 catalog.db 素材库格式")
                .font(.system(size: 10))
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
            Text("当前系统：macOS \(ProcessInfo.processInfo.operatingSystemVersionString)")
                .font(.system(size: 10))
                .foregroundStyle(.secondary)
            HStack(spacing: 10) {
                Link("项目主页", destination: URL(string: "https://gitcode.com/TMR2333/stj-media-viewer-macos")!)
                Link("全部发行版", destination: URL(string: "https://gitcode.com/TMR2333/stj-media-viewer-macos/releases")!)
                Link("Windows 版", destination: URL(string: "https://gitcode.com/TMR2333/stj-media-viewer")!)
            }
            .font(.system(size: 10))
            updateSection
            HStack(spacing: 8) {
                Button("打开日志目录") { NSWorkspace.shared.open(AppLog.directoryURL) }
                Button("打开素材库目录") {
                    if let directory = model.dataDirectory { NSWorkspace.shared.open(directory) }
                }
                Button("备份素材库…") { model.backupLibrary() }
                    .disabled(model.dataDirectory == nil)
            }
            .font(.system(size: 10))
            Divider().frame(width: 260)
            Text("日志：~/Library/Logs/STJMediaViewer/app.log\n数据目录下的 Cache/thumbs 与 catalog.db 可用「文件 → 备份素材库」整体备份。")
                .font(.system(size: 9))
                .foregroundStyle(.tertiary)
                .multilineTextAlignment(.center)
            Spacer()
        }
        .padding(24)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    /// 更新检查：手动检查 + 有新版时直接给出下载入口。
    private var updateSection: some View {
        VStack(spacing: 6) {
            Divider().frame(width: 260)
            HStack(spacing: 8) {
                Button {
                    model.checkForUpdates()
                } label: {
                    Label("检查更新", systemImage: "arrow.triangle.2.circlepath")
                }
                .disabled(model.isCheckingUpdate)
                if model.isCheckingUpdate {
                    ProgressView().controlSize(.small)
                }
            }
            .font(.system(size: 11))
            if let update = model.availableUpdate {
                Text("发现新版本 \(update.version)（当前 \(version)）")
                    .font(.system(size: 10, weight: .medium))
                    .foregroundStyle(Color.accentColor)
                Button("下载 \(update.tag)") {
                    model.openUpdatePage()
                }
                .controlSize(.small)
            } else if let message = model.updateCheckMessage {
                Text(message)
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
            } else {
                Text("启动时每天自动检查一次；也可以点上面的按钮手动检查。")
                    .font(.system(size: 10))
                    .foregroundStyle(.tertiary)
            }
        }
    }

    private var version: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "1.25.0"
    }

    private var buildNumber: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "0"
    }
}

extension LibraryDensity {
    var title: String {
        switch self {
        case .small: return "小网格"
        case .medium: return "中网格"
        case .large: return "大网格"
        case .list: return "列表"
        }
    }
}