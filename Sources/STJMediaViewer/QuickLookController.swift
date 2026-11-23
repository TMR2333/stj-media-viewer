import AppKit
import Foundation
import Quartz

// MARK: - Quick Look

/// 空格键快速查看（原生 QLPreviewPanel）。
@MainActor
public final class QuickLookController: NSObject, QLPreviewPanelDataSource, QLPreviewPanelDelegate {
    public static let shared = QuickLookController()

    private var items: [NSURL] = []

    public func toggle(items urls: [URL]) {
        guard !urls.isEmpty else { return }
        guard let panel = QLPreviewPanel.shared() else { return }
        if panel.isVisible {
            panel.orderOut(nil)
            return
        }
        items = urls as [NSURL]
        panel.dataSource = self
        panel.delegate = self
        panel.makeKeyAndOrderFront(nil)
    }

    public func close() {
        QLPreviewPanel.shared()?.orderOut(nil)
    }

    public func numberOfPreviewItems(in panel: QLPreviewPanel!) -> Int {
        items.count
    }

    public func previewPanel(_ panel: QLPreviewPanel!, previewItemAt index: Int) -> (any QLPreviewItem)! {
        items[index]
    }
}

