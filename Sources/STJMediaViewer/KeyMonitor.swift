import AppKit
import STJCore

/// 键盘交互：空格 QuickLook、回车打开、方向键移动选中、Esc 取消。
/// 只在主窗口内生效，输入框获得焦点时让系统处理按键。
@MainActor
final class KeyMonitor {
    static let shared = KeyMonitor()

    private weak var model: LibraryModel?
    private var monitor: Any?

    private init() {}

    func install(model: LibraryModel) {
        self.model = model
        guard monitor == nil else { return }
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            guard let self else { return event }
            return self.handle(event)
        }
    }

    func uninstall() {
        if let monitor {
            NSEvent.removeMonitor(monitor)
        }
        monitor = nil
    }

    private func handle(_ event: NSEvent) -> NSEvent? {
        guard let model, event.window === NSApp.keyWindow else { return event }
        // 正在编辑文本时把按键交还给输入框。
        if let responder = event.window?.firstResponder, responder is NSTextView {
            return event
        }
        if !event.modifierFlags.intersection([.command, .control, .option]).isEmpty {
            return event
        }

        // 画廊打开时：← → 翻页，Esc 关闭，其余按键不拦截。
        if model.galleryAssetID != nil {
            switch event.keyCode {
            case 123:
                model.stepGallery(by: -1)
                return nil
            case 124:
                model.stepGallery(by: 1)
                return nil
            case 53:
                model.closeGallery()
                return nil
            default:
                return event
            }
        }

        switch event.keyCode {
        case 49: // 空格
            model.toggleQuickLook()
            return nil
        case 36, 76: // 回车 / 小键盘回车
            model.openSelectionInDefaultApp()
            return nil
        case 53: // Esc
            QuickLookController.shared.close()
            model.expandedGroup = nil
            model.clearSelection()
            return nil
        case 123: // ←
            model.moveSelection(offset: -1)
            return nil
        case 124: // →
            model.moveSelection(offset: 1)
            return nil
        case 126: // ↑
            model.moveSelection(offset: model.density.isList ? -1 : -4)
            return nil
        case 125: // ↓
            model.moveSelection(offset: model.density.isList ? 1 : 4)
            return nil
        default:
            return event
        }
    }
}