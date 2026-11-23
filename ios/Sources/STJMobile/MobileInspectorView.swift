import STJCore
import SwiftUI

/// iPad 检视面板：只读元数据 + 常用编辑（评分 / 精选 / 废片 / 色标）。
struct MobileInspectorView: View {
    @ObservedObject var model: MobileLibraryModel

    var body: some View {
        Group {
            if let asset = model.primarySelection {
                content(for: asset)
            } else {
                ContentUnavailableView("未选择素材", systemImage: "sidebar.right",
                                       description: Text("点按网格里的卡片查看详情。"))
            }
        }
    }

    private func content(for asset: Asset) -> some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                if model.selection.count > 1 {
                    Label("已选 \(model.selection.count) 张，编辑会批量应用", systemImage: "square.stack.3d.up")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                }

                MobileThumbnailView(asset: asset, targetSize: 640)
                    .frame(height: 200)
                    .frame(maxWidth: .infinity)
                    .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))

                Text(asset.fileName)
                    .font(.headline)
                    .lineLimit(2)

                editing(for: asset)

                Divider()

                VStack(alignment: .leading, spacing: 6) {
                    metaRow("类型", asset.kindText)
                    metaRow("格式", asset.formatCode.isEmpty ? "—" : asset.formatCode.uppercased())
                    metaRow("尺寸", asset.dimensionText)
                    metaRow("大小", asset.sizeText)
                    if asset.isVideo { metaRow("时长", asset.durationText ?? "—") }
                    metaRow("拍摄时间", asset.capturedText)
                    metaRow("设备", asset.cameraModel ?? asset.deviceKey ?? "—")
                    metaRow("镜头", asset.lensModel ?? "—")
                    metaRow("地点", asset.locationText)
                    metaRow("主题", asset.displayThemes.isEmpty ? "—" : asset.displayThemes)
                    metaRow("影调", asset.tone ?? "—")
                    metaRow("关键词", asset.keywords.isEmpty ? "—" : asset.keywords.joined(separator: "、"))
                }
                .font(.callout)

                Divider()

                Text(asset.path)
                    .font(.caption2)
                    .foregroundStyle(.secondary)
                    .lineLimit(3)
                    .textSelection(.enabled)
            }
            .padding(14)
        }
    }

    private func editing(for asset: Asset) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 6) {
                ForEach(1...5, id: \.self) { value in
                    Button {
                        model.setRating(asset.rating == value ? 0 : value)
                    } label: {
                        Image(systemName: value <= asset.rating ? "star.fill" : "star")
                            .foregroundStyle(value <= asset.rating ? .yellow : .secondary)
                            .font(.title3)
                    }
                    .buttonStyle(.plain)
                }
                Spacer()
                if asset.rating > 0 {
                    Button("清除") { model.setRating(0) }
                        .font(.caption)
                }
            }

            HStack(spacing: 8) {
                pickButton("精选", symbol: "star.circle", active: asset.isPick == 1) {
                    model.setPick(asset.isPick == 1 ? 0 : 1)
                }
                pickButton("废片", symbol: "xmark.circle", active: asset.isPick == 0) {
                    model.setPick(asset.isPick == 0 ? 1 : 0)
                }
                Spacer()
            }

            HStack(spacing: 8) {
                Text("色标").font(.callout).foregroundStyle(.secondary)
                ForEach(ColorLabelOption.allCases) { option in
                    Circle()
                        .fill(option.color)
                        .frame(width: 18, height: 18)
                        .overlay {
                            if asset.colorLabel?.lowercased() == option.rawValue {
                                Circle().strokeBorder(Color.primary, lineWidth: 2)
                            }
                        }
                        .onTapGesture {
                            var patch = AssetPatch()
                            patch.colorLabel = option.rawValue == asset.colorLabel?.lowercased()
                                ? .clear
                                : .set(option.rawValue)
                            model.update(patch, ids: Array(model.selection))
                        }
                }
                Button("清除") {
                    var patch = AssetPatch()
                    patch.colorLabel = .clear
                    model.update(patch, ids: Array(model.selection))
                }
                .font(.caption)
            }
        }
    }

    private func pickButton(_ title: String, symbol: String, active: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Label(title, systemImage: symbol)
                .font(.callout)
                .padding(.horizontal, 10)
                .padding(.vertical, 6)
                .background(active ? Color.accentColor.opacity(0.2) : Color.secondary.opacity(0.12),
                            in: Capsule())
        }
        .buttonStyle(.plain)
    }

    private func metaRow(_ title: String, _ value: String) -> some View {
        HStack(alignment: .top, spacing: 8) {
            Text(title).foregroundStyle(.secondary).frame(width: 64, alignment: .leading)
            Text(value).textSelection(.enabled)
        }
    }
}

/// 色标选项（与 macOS 版一致的红橙黄绿蓝紫）。
enum ColorLabelOption: String, CaseIterable, Identifiable {
    case red, orange, yellow, green, blue, purple

    var id: String { rawValue }

    var title: String {
        switch self {
        case .red: return "红"
        case .orange: return "橙"
        case .yellow: return "黄"
        case .green: return "绿"
        case .blue: return "蓝"
        case .purple: return "紫"
        }
    }

    var color: Color {
        switch self {
        case .red: return .red
        case .orange: return .orange
        case .yellow: return .yellow
        case .green: return .green
        case .blue: return .blue
        case .purple: return .purple
        }
    }
}
