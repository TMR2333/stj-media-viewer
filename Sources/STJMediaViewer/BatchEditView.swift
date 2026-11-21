import STJCore
import SwiftUI

/// 批量填写：把勾选的字段一次性写入所选素材（对齐 Windows 版的“批量填写”）。
struct BatchEditView: View {
    @EnvironmentObject private var model: LibraryModel

    @State private var ratingEnabled = false
    @State private var rating = 0
    @State private var colorEnabled = false
    @State private var colorLabel: String = ""
    @State private var pickEnabled = false
    @State private var pick: Int? = 1
    @State private var cityEnabled = false
    @State private var city = ""
    @State private var themesEnabled = false
    @State private var themes = ""
    @State private var toneEnabled = false
    @State private var tone = ""
    @State private var logEnabled = false
    @State private var isLog = true
    @State private var postEnabled = false
    @State private var postProcess = 2

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text("批量填写").font(.headline)
                Spacer()
                Text("已选择 \(model.selection.count) 项")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
            }
            .padding(.horizontal, 16)
            .padding(.top, 16)
            .padding(.bottom, 10)
            Divider()

            ScrollView {
                VStack(alignment: .leading, spacing: 10) {
                    row(enabled: $ratingEnabled, title: "评分") {
                        StarRatingControl(rating: rating) { rating = $0 }
                    }
                    row(enabled: $colorEnabled, title: "色标") {
                        Picker("色标", selection: $colorLabel) {
                            Text("无").tag("")
                            ForEach(ColorLabelOption.allCases) { option in
                                Text(option.title).tag(option.rawValue)
                            }
                        }
                        .labelsHidden()
                        .frame(width: 120)
                        .controlSize(.small)
                    }
                    row(enabled: $pickEnabled, title: "选用") {
                        Picker("选用", selection: $pick) {
                            Text("未标记").tag(Int?.none)
                            Text("精选").tag(Int?.some(1))
                            Text("废片").tag(Int?.some(0))
                        }
                        .pickerStyle(.segmented)
                        .labelsHidden()
                        .frame(width: 180)
                        .controlSize(.small)
                    }
                    row(enabled: $cityEnabled, title: "城市") {
                        TextField("例如：上海", text: $city)
                            .textFieldStyle(.roundedBorder)
                            .frame(width: 180)
                    }
                    row(enabled: $themesEnabled, title: "主题") {
                        TextField("用逗号分隔，第一个为主主题", text: $themes)
                            .textFieldStyle(.roundedBorder)
                            .frame(width: 220)
                    }
                    row(enabled: $toneEnabled, title: "影调") {
                        TextField("例如：冷调", text: $tone)
                            .textFieldStyle(.roundedBorder)
                            .frame(width: 180)
                    }
                    row(enabled: $logEnabled, title: "Log") {
                        Toggle("标记为 Log 素材", isOn: $isLog)
                            .toggleStyle(.switch)
                            .controlSize(.mini)
                    }
                    row(enabled: $postEnabled, title: "调色状态") {
                        Picker("调色状态", selection: $postProcess) {
                            Text("未调色").tag(1)
                            Text("已调色").tag(2)
                        }
                        .pickerStyle(.segmented)
                        .labelsHidden()
                        .frame(width: 160)
                        .controlSize(.small)
                    }
                }
                .padding(16)
            }
            .frame(maxHeight: 340)

            Divider()
            HStack(spacing: 8) {
                Text("只写入勾选的字段，未勾选的字段保持原值。")
                    .font(.system(size: 10))
                    .foregroundStyle(.tertiary)
                Spacer()
                Button("取消") { model.isBatchEditorPresented = false }
                    .keyboardShortcut(.cancelAction)
                Button("应用到 \(model.selection.count) 项") { apply() }
                    .buttonStyle(.borderedProminent)
                    .keyboardShortcut(.defaultAction)
                    .disabled(model.selection.isEmpty)
            }
            .padding(14)
        }
        .frame(width: 520)
    }

    private func row<Content: View>(enabled: Binding<Bool>, title: String,
                                    @ViewBuilder content: () -> Content) -> some View {
        HStack(spacing: 10) {
            Toggle("", isOn: enabled)
                .toggleStyle(.checkbox)
                .labelsHidden()
                .frame(width: 18)
            Text(title)
                .font(.system(size: 11))
                .foregroundStyle(enabled.wrappedValue ? .primary : .secondary)
                .frame(width: 70, alignment: .leading)
            content()
                .disabled(!enabled.wrappedValue)
                .opacity(enabled.wrappedValue ? 1 : 0.45)
            Spacer(minLength: 0)
        }
    }

    private func apply() {
        var patch = AssetPatch()
        if ratingEnabled { patch.rating = rating }
        if colorEnabled { patch.colorLabel = colorLabel.isEmpty ? .clear : .set(colorLabel) }
        if pickEnabled { patch.isPick = pick.map { .set($0) } ?? .clear }
        if cityEnabled { patch.city = city.isEmpty ? .clear : .set(city) }
        if themesEnabled {
            let values = themes
                .split(whereSeparator: { ",，;；".contains($0) })
                .map { $0.trimmingCharacters(in: .whitespaces) }
                .filter { !$0.isEmpty }
            patch.themes = values.isEmpty ? .clear : .set(values)
        }
        if toneEnabled { patch.tone = tone.isEmpty ? .clear : .set(tone) }
        if logEnabled { patch.isLog = .set(isLog) }
        if postEnabled { patch.postProcess = postProcess }

        guard !patch.isEmpty else {
            model.showStatus("没有勾选要修改的字段")
            return
        }
        model.submitBatchEdit(patch)
    }
}