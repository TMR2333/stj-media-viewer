import AppKit
import STJCore
import SwiftUI

/// 多文件拖拽（拖到 Finder / 其他 App）。
///
/// SwiftUI 的 `.onDrag` 只能提供一个 `NSItemProvider`，一次拖多个文件必须用
/// AppKit 的 `NSDraggingSession`。做法：卡片上叠一个透明锚点视图，`DragGesture`
/// 一开始就把「本次拖拽要带的文件列表」交给锚点，由锚点用当前鼠标事件开拖拽会话。
enum MultiFileDrag {
    /// 单个文件图标预览的边长（点）。
    static let iconSize: CGFloat = 64

    /// 为每个文件生成一个拖拽项（文件 URL + 系统文件图标预览）。
    static func makeItems(for urls: [URL], near point: CGPoint) -> [NSDraggingItem] {
        var items: [NSDraggingItem] = []
        items.reserveCapacity(urls.count)
        for (index, url) in urls.enumerated() {
            // NSURL 实现 NSPasteboardWriting，落到 Finder 是「复制文件」而不是文本
            let item = NSDraggingItem(pasteboardWriter: url as NSURL)
            let icon = NSWorkspace.shared.icon(forFile: url.path)
            icon.size = NSSize(width: iconSize, height: iconSize)
            // 多个图标稍微错开，形成一叠的观感
            let offset = CGFloat(index % 3) * 4
            item.setDraggingFrame(
                NSRect(x: point.x - iconSize / 2 + offset,
                       y: point.y - iconSize / 2 - offset,
                       width: iconSize,
                       height: iconSize),
                contents: icon
            )
            items.append(item)
        }
        return items
    }

    /// 从锚点视图发起拖拽会话；`event` 需是当前鼠标拖拽事件。
    static func begin(urls: [URL], from view: NSView, event: NSEvent) {
        guard !urls.isEmpty, view.window != nil else { return }
        let point = view.convert(event.locationInWindow, from: nil)
        let items = makeItems(for: urls, near: point)
        guard !items.isEmpty else { return }
        let session = view.beginDraggingSession(with: items, event: event, source: DragSessionSource.shared)
        session.animatesToStartingPositionsOnCancelOrFail = true
        AppLog.breadcrumb("开始拖拽：\(urls.count) 个文件")
    }
}

/// 拖拽会话的 source：只允许复制（拖出是复制文件，不是剪切）。
final class DragSessionSource: NSObject, NSDraggingSource {
    static let shared = DragSessionSource()

    func draggingSession(_ session: NSDraggingSession,
                         sourceOperationMaskFor context: NSDraggingContext) -> NSDragOperation {
        context == .outsideApplication ? .copy : .copy
    }

    func draggingSession(_ session: NSDraggingSession, endedAt screenPoint: NSPoint, operation: NSDragOperation) {
        if operation.isEmpty {
            AppLog.debug("拖拽取消")
        } else {
            AppLog.breadcrumb("拖出文件：\(session.draggingPasteboard.pasteboardItems?.count ?? 0) 个")
        }
    }
}

/// 卡片上的透明拖拽锚点（不参与命中测试，点击/选择/右键菜单仍由 SwiftUI 处理）。
@MainActor
final class CardDragAnchor: ObservableObject {
    weak var view: NSView?
    private var started = false

    func begin(urls: [URL]) {
        guard !started, let view, let event = NSApp.currentEvent else { return }
        started = true
        MultiFileDrag.begin(urls: urls, from: view, event: event)
    }

    func reset() {
        started = false
    }
}

struct CardDragAnchorView: NSViewRepresentable {
    let anchor: CardDragAnchor

    func makeNSView(context: Context) -> NSView {
        let view = AnchorView()
        anchor.view = view
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        anchor.view = nsView
    }

    final class AnchorView: NSView {
        override func hitTest(_ point: NSPoint) -> NSView? { nil }
    }
}
