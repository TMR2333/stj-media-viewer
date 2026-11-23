import STJMobile
import STJCore
import SwiftUI

/// iPad / iPhone 应用入口。真正的功能都在 `STJMobile` 包里，这里只负责挂根视图。
@main
struct STJMobileApp: App {
    @StateObject private var model = MobileLibraryModel()
    @StateObject private var settings = SettingsStore.shared

    var body: some Scene {
        WindowGroup {
            MobileRootView(model: model, settings: settings)
                .environmentObject(settings)
                .preferredColorScheme(colorScheme)
                .onAppear {
                    AppLog.breadcrumb("移动端启动：\(Self.versionDescription)")
                }
        }
    }

    private var colorScheme: ColorScheme? {
        switch settings.appearance {
        case .system: return nil
        case .light: return .light
        case .dark: return .dark
        }
    }

    static var versionDescription: String {
        let version = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "0.1"
        let build = Bundle.main.infoDictionary?["CFBundleVersion"] as? String ?? "0"
        return "STJ Media Viewer Mobile \(version) (\(build)) / iOS \(UIDevice.current.systemVersion)"
    }
}
