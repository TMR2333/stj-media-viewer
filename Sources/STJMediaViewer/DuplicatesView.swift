import STJCore
import SwiftUI

/// 重复项检测结果面板。
struct DuplicatesView: View {
    @EnvironmentObject private var model: LibraryModel
    @Environment(\.dismiss) private var dismiss

    private var wastedTotal: Int64 {
        model.duplicateGroups.reduce(Int64(0)) { $0 + $1.wastedBytes }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .firstTextBaseline) {
                VStack(alignment: .leading, spacing: 4) {
                    Text("重复项")
                        .font(.title3.weight(.semibold))
                    Text(summaryText)
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                }
                Spacer()
                Button("关闭") { dismiss() }
                    .keyboardShortcut(.cancelAction)
            }
            .padding(20)

            Divider()

            if model.duplicateGroups.isEmpty {
                ContentUnavailableView(
                    "没有发现重复素材",
                    systemImage: "square.on.square",
                    description: Text("按文件大小、像素尺寸与文件头哈希比对后，未发现疑似重复的素材。")
                )
            } else {
                List(model.duplicateGroups) { group in
                    groupRow(group)
                }
                .listStyle(.inset)
            }

            Divider()
            HStack {
                Text("建议保留：精选 > 未标记 > 废片，其次评分高、体积大、修改时间新的那张。")
                    .font(.footnote)
                    .foregroundStyle(.secondary)
                Spacer()
            }
            .padding(12)
        }
        .frame(minWidth: 720, minHeight: 460)
    }

    private var summaryText: String {
        if model.duplicateGroups.isEmpty { return "未发现重复" }
        let wasted = ByteCountFormatter.string(fromByteCount: wastedTotal, countStyle: .file)
        return "\(model.duplicateGroups.count) 组 · 可回收 \(wasted)"
    }

    @ViewBuilder
    private func groupRow(_ group: DuplicateGroup) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("\(group.count) 个文件")
                    .font(.headline)
                Text(group.reason)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Spacer()
                Text("可回收 \(group.wastedText)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Button("选中多余的") {
                    model.selection = Set(group.assets.dropFirst().map(\.id))
                    dismiss()
                }
                .buttonStyle(.bordered)
                .controlSize(.small)
                Button("选中整组") {
                    model.selectDuplicateGroup(group)
                }
                .buttonStyle(.bordered)
                .controlSize(.small)
            }
            ForEach(Array(group.assets.enumerated()), id: \.element.id) { index, asset in
                HStack(spacing: 8) {
                    Image(systemName: index == 0 ? "star.fill" : "square.on.square")
                        .foregroundStyle(index == 0 ? Color.accentColor : .secondary)
                        .font(.caption)
                    Text(asset.fileName)
                        .font(.callout)
                        .lineLimit(1)
                        .truncationMode(.middle)
                    Text(asset.dimensionText)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    Text(ByteCountFormatter.string(fromByteCount: asset.sizeBytes, countStyle: .file))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    Spacer()
                    if index == 0 {
                        Text("建议保留")
                            .font(.caption2)
                            .padding(.horizontal, 6)
                            .padding(.vertical, 2)
                            .background(Color.accentColor.opacity(0.16), in: Capsule())
                    }
                }
            }
        }
        .padding(.vertical, 6)
    }
}