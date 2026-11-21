import CoreGraphics
import Foundation

/// 框选（橡皮筋）选择的几何计算，与视图层解耦，便于单元测试。
public enum MarqueeGeometry {
    /// 由起点与当前点生成规范化矩形（左上角 + 正宽高）。
    public static func rect(from start: CGPoint, to current: CGPoint) -> CGRect {
        CGRect(x: min(start.x, current.x),
               y: min(start.y, current.y),
               width: abs(current.x - start.x),
               height: abs(current.y - start.y))
    }

    /// 小于该边长的框视为“背景点击”，不参与框选。
    public static let clickTolerance: CGFloat = 3

    public static func isClick(_ rect: CGRect) -> Bool {
        rect.width < clickTolerance && rect.height < clickTolerance
    }

    /// 命中框选矩形的素材 id 集合。
    /// - Parameters:
    ///   - rect: 框选矩形（与 frames 同一坐标系）
    ///   - frames: 每张卡片的矩形
    ///   - current: 当前选择
    ///   - modifiers: `.shift` 叠加、`.command` 反向切换，其余情况替换
    public static func selection(in rect: CGRect,
                                 frames: [String: CGRect],
                                 current: Set<String>,
                                 shift: Bool,
                                 command: Bool) -> Set<String> {
        let hit = Set(frames.filter { $0.value.intersects(rect) }.keys)
        if command {
            return current.symmetricDifference(hit)
        }
        if shift {
            return current.union(hit)
        }
        return hit
    }
}