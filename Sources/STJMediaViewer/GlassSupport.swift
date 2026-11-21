import SwiftUI

/// Liquid Glass（macOS 26 Tahoe）适配层。
///
/// macOS 26 起使用原生玻璃材质（`glassEffect` / `GlassEffectContainer`）；
/// macOS 14–15 自动回退到系统材质 `.regularMaterial`，布局保持不变。
enum GlassSurface {
    /// 常规玻璃：面板、状态条等需要清晰边界的区域。
    case regular
    /// 清透玻璃：叠在照片/视频上的浮层，尽量不遮挡内容。
    case clear
}

@available(macOS 26.0, *)
private enum GlassFactory {
    static func make(_ surface: GlassSurface, tint: Color?, interactive: Bool) -> Glass {
        var glass: Glass = surface == .clear ? .clear : .regular
        if let tint { glass = glass.tint(tint) }
        if interactive { glass = glass.interactive() }
        return glass
    }
}

extension View {
    /// 圆角玻璃面板。
    @ViewBuilder
    func stjGlass(cornerRadius: CGFloat = 18,
                  surface: GlassSurface = .regular,
                  tint: Color? = nil,
                  interactive: Bool = false) -> some View {
        if #available(macOS 26.0, *) {
            self.glassEffect(GlassFactory.make(surface, tint: tint, interactive: interactive),
                             in: .rect(cornerRadius: cornerRadius))
        } else {
            self.background(.regularMaterial,
                            in: RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
                .overlay(RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                    .strokeBorder(Color.primary.opacity(0.06)))
        }
    }

    /// 胶囊玻璃：快捷筛选 chip、悬浮状态条。
    @ViewBuilder
    func stjGlassCapsule(surface: GlassSurface = .regular,
                         tint: Color? = nil,
                         interactive: Bool = true) -> some View {
        if #available(macOS 26.0, *) {
            self.glassEffect(GlassFactory.make(surface, tint: tint, interactive: interactive), in: .capsule)
        } else {
            self.background(.regularMaterial, in: Capsule())
                .overlay(Capsule().strokeBorder(Color.primary.opacity(0.08)))
        }
    }

    /// 玻璃按钮样式：浮层上的圆形/图标按钮。
    @ViewBuilder
    func stjGlassButtonStyle() -> some View {
        if #available(macOS 26.0, *) {
            self.buttonStyle(.glass)
        } else {
            self.buttonStyle(.borderless)
        }
    }
}

/// 把若干玻璃元素合并成一个可形变的玻璃群组（macOS 26+）。
struct GlassCluster<Content: View>: View {
    var spacing: CGFloat = 6
    @ViewBuilder var content: Content

    var body: some View {
        if #available(macOS 26.0, *) {
            GlassEffectContainer(spacing: spacing) { content }
        } else {
            content
        }
    }
}