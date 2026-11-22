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

        // 评分 / 色标 / 选用 / 废片：与 Windows 画廊、预览一致的节奏键。
        if let rating = Self.ratingFor(keyCode: event.keyCode) {
            model.setRating(rating)
            return nil
        }
        if let label = Self.colorLabelFor(keyCode: event.keyCode) {
            model.setColorLabel(label)
            return nil
        }
        switch event.keyCode {
        case 35: // P
            model.setPick(1)
            return nil
        case 7: // X
            model.setPick(0)
            return nil
        default:
            break
        }

        // 画廊打开时：← → 翻页、Home/End 首末、Esc 关闭，其余按键不拦截。
        if model.galleryAssetID != nil {
            switch event.keyCode {
            case 123:
                model.stepGallery(by: -1)
                return nil
            case 124:
                model.stepGallery(by: 1)
                return nil
            case 115: // Home
                model.stepGallery(by: -model.assets.count)
                return nil
            case 119: // End
                model.stepGallery(by: model.assets.count)
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
        case 36, 76: // 回车 / 小键盘回车：Windows 是内置预览，外部打开走菜单 ⌘↩
            model.openSelectionInGallery()
            return nil
        case 115: // Home
            if let first = model.assets.first { model.selection = [first.id] }
            return nil
        case 119: // End
            if let last = model.assets.last { model.selection = [last.id] }
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

    /// 0–5 → 评分（键位码取自 US 键盘数字行）。
    static func ratingFor(keyCode: UInt16) -> Int? {
        switch keyCode {
        case 29: return 0
        case 18: return 1
        case 19: return 2
        case 20: return 3
        case 21: return 4
        case 23: return 5
        default: return nil
        }
    }

    /// 6/7/8/9 → 红/黄/绿/蓝 色标（与 Windows 画廊一致）。
    static func colorLabelFor(keyCode: UInt16) -> String? {
        switch keyCode {
        case 22: return "red"
        case 26: return "yellow"
        case 28: return "green"
        case 25: return "blue"
        default: return nil
        }
    }
}
