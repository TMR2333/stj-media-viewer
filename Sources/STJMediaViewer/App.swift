import AppKit
import STJCore
import SwiftUI

@main
struct STJMediaViewerApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @StateObject private var settings = SettingsStore.shared
    @StateObject private var model = LibraryModel()

    var body: some Scene {
        WindowGroup("STJ Media Viewer") {
            RootView()
                .environmentObject(model)
                .environmentObject(settings)
                .preferredColorScheme(settings.appearance.colorScheme)
                .frame(minWidth: 1100, minHeight: 680)
                .task {
                    KeyMonitor.shared.install(model: model)
                    await model.startIfNeeded()
                }
        }
        .defaultSize(width: 1320, height: 840)
        .commands {
            AppCommands(model: model, settings: settings)
        }

        Settings {
            SettingsView()
                .environmentObject(settings)
                .environmentObject(model)
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.regular)
        // 记住主窗口位置与大小（P0「窗口恢复」）。
        DispatchQueue.main.async {
            NSApp.windows
                .first { $0.title == "STJ Media Viewer" }?
                .setFrameAutosaveName("STJMainWindow")
        }
        NSApp.activate(ignoringOtherApps: true)
        AppLog.info("应用启动：STJ Media Viewer \(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "dev")，macOS \(ProcessInfo.processInfo.operatingSystemVersionString)")
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        true
    }

    func applicationWillTerminate(_ notification: Notification) {
        AppLog.info("应用退出")
    }
}

extension AppearanceMode {
    var colorScheme: ColorScheme? {
        switch self {
        case .system: return nil
        case .light: return .light
        case .dark: return .dark
        }
    }
}