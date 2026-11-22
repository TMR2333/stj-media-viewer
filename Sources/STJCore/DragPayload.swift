import Foundation

/// 拖拽载荷计算：决定一次拖拽要带上哪些素材文件。
///
/// Windows 版拖动已选中的卡片时会带上整批选中项；macOS 的 SwiftUI `.onDrag`
/// 一次只能提供一个 `NSItemProvider`，所以改为在拖拽开始时自己算好文件列表，
/// 再一次性开一个 AppKit 拖拽会话（见 `MultiFileDrag`）。
public enum DragPayload {
    /// 拖拽目标卡片对应的素材 id 列表：
    /// - 拖的是选中项之一 → 整批选中项（按 id 排序，保证顺序稳定）
    /// - 拖的是未选中的卡片 → 只有它自己
    public static func assetIDs(dragging id: String, selection: Set<String>) -> [String] {
        selection.contains(id) ? selection.sorted() : [id]
    }
}
