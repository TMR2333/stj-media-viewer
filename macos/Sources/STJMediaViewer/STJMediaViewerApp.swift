// Copyright (c) Stjarna Perspective. MIT.

import SwiftUI

@main
struct STJMediaViewerApp: App {
    var body: some Scene {
        WindowGroup("STJ Media Viewer") {
            ContentView()
                .preferredColorScheme(nil)
        }
        .windowStyle(.titleBar)
        .commands {
            CommandGroup(replacing: .newItem) { }

            CommandMenu("素材库") {
                Button("重新载入") {
                    NotificationCenter.default.post(name: .reloadLibrary, object: nil)
                }
                .keyboardShortcut("r", modifiers: .command)

                Button("清空筛选") {
                    NotificationCenter.default.post(name: .clearFilters, object: nil)
                }
                .keyboardShortcut(.delete, modifiers: .command)
            }
        }
    }
}

extension Notification.Name {
    static let reloadLibrary = Notification.Name("stj.reloadLibrary")
    static let clearFilters = Notification.Name("stj.clearFilters")
}