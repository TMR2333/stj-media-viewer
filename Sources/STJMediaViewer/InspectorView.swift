import AppKit
import STJCore
import SwiftUI

struct InspectorView: View {
    @EnvironmentObject private var model: LibraryModel

    var body: some View {
        Group {
            if model.selection.isEmpty {
                placeholder
            } else if model.selection.count == 1, let asset = model.primarySelection {
                AssetInspector(asset: asset)
                    .id(asset.id)
            } else {
                MultiSelectionInspector()
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(nsColor: .windowBackgroundColor))
    }

    private var placeholder: some View {
        VStack(spacing: 10) {
            Image(systemName: "sidebar.right")
                .font(.system(size: 32, weight: .light))
                .foregroundStyle(.tertiary)
            Text("未选择素材").font(.headline)
            Text("在网格或列表里点选一条素材，这里会显示它的元数据，并可以直接修改评分、色标、城市、主题等字段。")
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .padding(.horizontal, 24)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

// MARK: - 单选检视与编辑

private struct AssetInspector: View {
    @EnvironmentObject private var model: LibraryModel

    let asset: Asset

    @State private var city: String
    @State private var themes: String
    @State private var tone: String
    @State private var country: String
    @State private var province: String

    init(asset: Asset) {
        self.asset = asset
        _city = State(initialValue: asset.city ?? "")
        _themes = State(initialValue: asset.themes.joined(separator: "，"))
        _tone = State(initialValue: asset.tone ?? "")
        _country = State(initialValue: asset.country ?? "")
        _province = State(initialValue: asset.province ?? "")
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                header
                Divider()
                metadataSection
                Divider()
                editSection
                Divider()
                actionSection
                if let message = asset.lastError, !message.isEmpty {
                    Text(message)
                        .font(.system(size: 10))
                        .foregroundStyle(.red)
                }
            }
            .padding(14)
        }
    }

    // MARK: 顶部

    private var header: some View {
        HStack(alignment: .top, spacing: 10) {
            ThumbnailView(asset: asset, targetSize: 176)
                .frame(width: 96, height: 76)
                .clipShape(RoundedRectangle(cornerRadius: 7, style: .continuous))
                .overlay(
                    RoundedRectangle(cornerRadius: 7, style: .continuous)
                        .strokeBorder(Color.primary.opacity(0.12), lineWidth: 0.5)
                )
            VStack(alignment: .leading, spacing: 3) {
                Text(asset.fileName)
                    .font(.system(size: 13, weight: .semibold))
                    .lineLimit(2)
                    .truncationMode(.middle)
                Text(asset.kindText + " · " + asset.formatCode.uppercased())
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
                RatingStars(rating: asset.rating)
                if !asset.displayThemes.isEmpty {
                    Text(asset.displayThemes)
                        .font(.system(size: 10))
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
            }
        }
    }

    // MARK: 只读元数据

    private var metadataSection: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("元数据").font(.system(size: 11, weight: .semibold)).foregroundStyle(.secondary)
            infoRow("尺寸", asset.dimensionText)
            if asset.isVideo {
                infoRow("时长", asset.durationText ?? "—")
            }
            infoRow("文件大小", asset.sizeText)
            infoRow("拍摄时间", asset.capturedText)
            infoRow("地点", asset.locationText)
            infoRow("相机", [asset.cameraMake, asset.cameraModel].compactMap { $0 }.joined(separator: " "))
            infoRow("镜头", asset.lensModel ?? "—")
            infoRow("焦距 / 光圈", exposureText)
            infoRow("ISO", asset.iso.map(String.init) ?? "—")
            infoRow("色标", asset.colorLabel ?? "—")
            infoRow("选用", asset.pickText)
            infoRow("调色", asset.postProcessText)
            infoRow("Log", asset.isLog ? (asset.logProfile ?? "是") : "否")
            if let lut = asset.lut, !lut.isEmpty { infoRow("LUT", lut) }
            if let subject = asset.aiSubject, !subject.isEmpty { infoRow("AI 主体", subject) }
            if let mood = asset.aiMood, !mood.isEmpty { infoRow("AI 情绪", mood) }
            if let quality = asset.qualityScore { infoRow("AI 质量", String(format: "%.2f", quality)) }
            if let group = asset.groupID, !group.isEmpty {
                infoRow("合并组", group + (asset.isGroupCover ? "（封面）" : ""))
            }
            if !asset.keywords.isEmpty {
                Text("关键词").font(.system(size: 10)).foregroundStyle(.secondary)
                FlowLayout(spacing: 5) {
                    ForEach(asset.keywords, id: \.self) { keyword in
                        Text(keyword)
                            .font(.system(size: 10))
                            .padding(.horizontal, 7)
                            .padding(.vertical, 2)
                            .background(Color.primary.opacity(0.06), in: Capsule())
                    }
                }
            }
            Text(asset.path)
                .font(.system(size: 9.5))
                .foregroundStyle(.tertiary)
                .lineLimit(2)
                .truncationMode(.middle)
                .textSelection(.enabled)
        }
    }

    private var exposureText: String {
        var pieces: [String] = []
        if let focal = asset.focalLength { pieces.append("\(Int(focal.rounded()))mm") }
        if let aperture = asset.aperture { pieces.append(String(format: "f/%.1f", aperture)) }
        return pieces.isEmpty ? "—" : pieces.joined(separator: "  ")
    }

    private func infoRow(_ title: String, _ value: String) -> some View {
        HStack(alignment: .top, spacing: 6) {
            Text(title)
                .font(.system(size: 10))
                .foregroundStyle(.secondary)
                .frame(width: 66, alignment: .leading)
            Text(value.isEmpty ? "—" : value)
                .font(.system(size: 10.5))
                .lineLimit(2)
                .truncationMode(.middle)
            Spacer(minLength: 0)
        }
    }

    // MARK: 编辑

    private var editSection: some View {
        VStack(alignment: .leading, spacing: 9) {
            Text("编辑").font(.system(size: 11, weight: .semibold)).foregroundStyle(.secondary)

            HStack(spacing: 8) {
                Text("评分").font(.system(size: 10)).foregroundStyle(.secondary).frame(width: 46, alignment: .leading)
                StarRatingControl(rating: asset.rating) { rating in
                    var patch = AssetPatch()
                    patch.rating = rating
                    model.update(patch, ids: [asset.id])
                }
            }

            HStack(spacing: 8) {
                Text("选用").font(.system(size: 10)).foregroundStyle(.secondary).frame(width: 46, alignment: .leading)
                Picker("选用", selection: Binding(
                    get: { asset.isPick },
                    set: { newValue in
                        var patch = AssetPatch()
                        patch.isPick = newValue.map { PatchValue.set($0) } ?? .clear
                        model.update(patch, ids: [asset.id])
                    }
                )) {
                    Text("未标记").tag(Int?.none)
                    Text("精选").tag(Int?.some(1))
                    Text("废片").tag(Int?.some(0))
                }
                .labelsHidden()
                .pickerStyle(.segmented)
                .controlSize(.small)
            }

            HStack(spacing: 8) {
                Text("色标").font(.system(size: 10)).foregroundStyle(.secondary).frame(width: 46, alignment: .leading)
                Picker("色标", selection: Binding(
                    get: { asset.colorLabel ?? "" },
                    set: { newValue in
                        var patch = AssetPatch()
                        patch.colorLabel = newValue.isEmpty ? .clear : .set(newValue)
                        model.update(patch, ids: [asset.id])
                    }
                )) {
                    Text("无").tag("")
                    ForEach(ColorLabelOption.allCases) { option in
                        Text(option.title).tag(option.rawValue)
                    }
                }
                .labelsHidden()
                .controlSize(.small)
                .frame(width: 130)
            }

            HStack(spacing: 8) {
                Text("调色").font(.system(size: 10)).foregroundStyle(.secondary).frame(width: 46, alignment: .leading)
                Picker("调色", selection: Binding(
                    get: { asset.postProcess },
                    set: { value in
                        var patch = AssetPatch()
                        patch.postProcess = value
                        model.update(patch, ids: [asset.id])
                    }
                )) {
                    Text("未调色").tag(1)
                    Text("已调色").tag(2)
                }
                .labelsHidden()
                .pickerStyle(.segmented)
                .controlSize(.small)
            }

            HStack(spacing: 8) {
                Text("Log").font(.system(size: 10)).foregroundStyle(.secondary).frame(width: 46, alignment: .leading)
                Toggle("标记为 Log 素材", isOn: Binding(
                    get: { asset.isLog },
                    set: { value in
                        var patch = AssetPatch()
                        patch.isLog = .set(value)
                        model.update(patch, ids: [asset.id])
                    }
                ))
                .toggleStyle(.switch)
                .controlSize(.mini)
                .labelsHidden()
                Text(asset.logProfile ?? "")
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
            }

            editField("城市", text: $city) {
                var patch = AssetPatch()
                patch.city = city.isEmpty ? .clear : .set(city)
                model.update(patch, ids: [asset.id])
            }
            editField("省份", text: $province) {
                var patch = AssetPatch()
                patch.province = province.isEmpty ? .clear : .set(province)
                model.update(patch, ids: [asset.id])
            }
            editField("国家", text: $country) {
                var patch = AssetPatch()
                patch.country = country.isEmpty ? .clear : .set(country)
                model.update(patch, ids: [asset.id])
            }
            editField("主题", text: $themes) {
                let values = themes
                    .split(whereSeparator: { ",，;；".contains($0) })
                    .map { $0.trimmingCharacters(in: .whitespaces) }
                    .filter { !$0.isEmpty }
                var patch = AssetPatch()
                patch.themes = values.isEmpty ? .clear : .set(values)
                model.update(patch, ids: [asset.id])
            }
            editField("影调", text: $tone) {
                var patch = AssetPatch()
                patch.tone = tone.isEmpty ? .clear : .set(tone)
                model.update(patch, ids: [asset.id])
            }
            Text("多个主题用逗号分隔，第一个为主主题；改完按回车保存到 catalog.db。")
                .font(.system(size: 9))
                .foregroundStyle(.tertiary)
        }
    }

    private func editField(_ title: String, text: Binding<String>, commit: @escaping () -> Void) -> some View {
        HStack(spacing: 8) {
            Text(title).font(.system(size: 10)).foregroundStyle(.secondary).frame(width: 46, alignment: .leading)
            TextField(title, text: text)
                .textFieldStyle(.roundedBorder)
                .font(.system(size: 11))
                .onSubmit(commit)
        }
    }

    // MARK: 操作

    private var actionSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 6) {
                Button {
                    model.openSelectionInDefaultApp()
                } label: {
                    Label("打开", systemImage: "arrow.up.forward.app")
                }
                Button {
                    model.revealSelection()
                } label: {
                    Label("Finder", systemImage: "folder")
                }
                Button {
                    model.writeXMP()
                } label: {
                    Label("写 XMP", systemImage: "square.and.arrow.down")
                }
                Button(role: .destructive) {
                    model.deleteSelection()
                } label: {
                    Image(systemName: "trash")
                }
                .help("移到废纸篓（⌘⌫）")
            }
            .controlSize(.small)
            Button {
                model.isBatchEditorPresented = true
            } label: {
                Label("批量填写同类字段…", systemImage: "square.and.pencil")
            }
            .controlSize(.small)
        }
    }
}

// MARK: - 多选

private struct MultiSelectionInspector: View {
    @EnvironmentObject private var model: LibraryModel

    var body: some View {
        VStack(spacing: 12) {
            Image(systemName: "square.stack.3d.up")
                .font(.system(size: 30, weight: .light))
                .foregroundStyle(.tertiary)
            Text("已选择 \(model.selection.count) 项").font(.headline)
            Text(summaryText)
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .padding(.horizontal, 20)
            Button("批量填写…") {
                model.isBatchEditorPresented = true
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.small)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var summaryText: String {
        let counts = Dictionary(grouping: model.selectedAssets, by: { $0.kind })
        let images = counts[1]?.count ?? 0
        let videos = counts[2]?.count ?? 0
        var pieces: [String] = []
        if images > 0 { pieces.append("\(images) 张图片") }
        if videos > 0 { pieces.append("\(videos) 个视频") }
        let picked = model.selectedAssets.filter { $0.isPick == 1 }.count
        if picked > 0 { pieces.append("\(picked) 项精选") }
        return pieces.isEmpty ? "可以一起改评分、色标、城市、主题等字段。" : pieces.joined(separator: " · ") + "。可以一起改评分、色标、城市、主题等字段。"
    }
}

// MARK: - 评分控件与色标

struct StarRatingControl: View {
    var rating: Int
    var onRate: (Int) -> Void

    var body: some View {
        HStack(spacing: 2) {
            ForEach(1...5, id: \.self) { index in
                Button {
                    onRate(rating == index ? 0 : index)
                } label: {
                    Image(systemName: index <= rating ? "star.fill" : "star")
                        .font(.system(size: 12))
                        .foregroundStyle(index <= rating ? Color.yellow : Color.secondary.opacity(0.6))
                }
                .buttonStyle(.plain)
                .help("\(index) 星")
            }
            Button {
                onRate(0)
            } label: {
                Image(systemName: "xmark.circle")
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
            }
            .buttonStyle(.plain)
            .help("清除评分")
        }
    }
}

enum ColorLabelOption: String, CaseIterable, Identifiable {
    case red = "红"
    case orange = "橙"
    case yellow = "黄"
    case green = "绿"
    case blue = "蓝"
    case purple = "紫"

    var id: String { rawValue }

    var title: String { rawValue }

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