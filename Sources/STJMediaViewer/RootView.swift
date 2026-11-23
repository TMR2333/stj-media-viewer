import AppKit
import STJCore
import SwiftUI

struct RootView: View {
    @EnvironmentObject private var model: LibraryModel
    @EnvironmentObject private var settings: SettingsStore
    @Environment(\.openSettings) private var openSettings

    private var inspectorBinding: Binding<Bool> {
        Binding(get: { settings.showsInspector },
                set: { settings.showsInspector = $0 })
    }

    var body: some View {
        splitView
            .frame(minWidth: 1100, minHeight: 680)
        .task {
            // 验收截图辅助：启动参数直接打开设置窗口（默认不启用，需要在命令行显式传入）。
            if UserDefaults.standard.bool(forKey: "openSettingsOnLaunch") {
                try? await Task.sleep(nanoseconds: 700_000_000)
                openSettings()
                AppLog.breadcrumb("启动参数打开设置窗口")
            }
        }
        .alert("出错了", isPresented: Binding(
            get: { model.presentedError != nil },
            set: { if !$0 { model.presentedError = nil } }
        )) {
            Button("好", role: .cancel) { model.presentedError = nil }
        } message: {
            Text(model.presentedError ?? "")
        }
    }

    private var splitView: some View {
        NavigationSplitView(columnVisibility: $model.columnVisibility) {
            SidebarView()
                .navigationSplitViewColumnWidth(min: 208, ideal: 236, max: 300)
        } detail: {
            LibraryContentView()
                .inspector(isPresented: inspectorBinding) {
                    InspectorView()
                        .inspectorColumnWidth(min: 280, ideal: 320, max: 420)
                }
                .navigationTitle("STJ Media Viewer")
                .navigationSubtitle(model.breadcrumb)
        }
        .frame(minWidth: 1100, minHeight: 680)
        .task {
            // 验收截图辅助：启动参数直接打开设置窗口（默认不启用，需要在命令行显式传入）。
            if UserDefaults.standard.bool(forKey: "openSettingsOnLaunch") {
                try? await Task.sleep(nanoseconds: 700_000_000)
                openSettings()
                AppLog.breadcrumb("启动参数打开设置窗口")
            }
        }
        .alert("出错了", isPresented: Binding(
            get: { model.presentedError != nil },
            set: { if !$0 { model.presentedError = nil } }
        )) {
            Button("好") { model.presentedError = nil }
                .keyboardShortcut(.defaultAction)
        } message: {
            Text(model.presentedError ?? "")
        }
        .sheet(isPresented: $model.isBatchEditorPresented) {
            BatchEditView()
                .environmentObject(model)
        }
        .sheet(isPresented: Binding(
            get: { model.galleryAssetID != nil },
            set: { if !$0 { model.closeGallery() } }
        )) {
            GalleryView()
                .environmentObject(model)
        }
        .sheet(isPresented: Binding(
            get: { !settings.hasCompletedOnboarding },
            set: { presented in if !presented { settings.hasCompletedOnboarding = true } }
        )) {
            OnboardingView().environmentObject(model).environmentObject(settings)
                .presentationBackground(.clear)
        }
        .sheet(isPresented: $model.isDuplicateSheetPresented) {
            DuplicatesView()
                .environmentObject(model)
        }
    }
}