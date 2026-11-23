import STJCore
import SwiftUI

/// 移动端筛选面板：类型 / 评分 / 选用状态 / 色标 / 年份。
struct MobileFilterView: View {
    @ObservedObject var model: MobileLibraryModel
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            Form {
                Section("类型") {
                    ForEach(model.facets.kinds, id: \.value) { item in
                        filterRow(title: kindTitle(item.value), count: item.count,
                                  isOn: model.filter.kinds.contains(Int(item.value) ?? -1)) {
                            toggleKind(Int(item.value) ?? -1)
                        }
                    }
                }

                Section("评分") {
                    ForEach([5, 4, 3, 2, 1], id: \.self) { value in
                        filterRow(title: String(repeating: "★", count: value) + " 及以上",
                                  count: nil,
                                  isOn: model.filter.ratings.contains(value + 1) && (1...5).allSatisfy(model.filter.ratings.contains)) {
                            toggleRating(value + 1)
                        }
                    }
                }

                Section("选用状态") {
                    filterRow(title: "精选", count: model.facets.picked, isOn: model.filter.pick == 1) {
                        model.filter.pick = model.filter.pick == 1 ? nil : 1
                    }
                    filterRow(title: "废片", count: model.facets.rejected, isOn: model.filter.pick == 0) {
                        model.filter.pick = model.filter.pick == 0 ? nil : 0
                    }
                }

                if !model.facets.years.isEmpty {
                    Section("年份") {
                        ForEach(model.facets.years, id: \.value) { item in
                            filterRow(title: item.value, count: item.count,
                                      isOn: model.filter.years.contains(item.value)) {
                                toggleString(\.years, item.value)
                            }
                        }
                    }
                }

                if !model.facets.cities.isEmpty {
                    Section("城市") {
                        ForEach(model.facets.cities.prefix(12), id: \.value) { item in
                            filterRow(title: item.value, count: item.count,
                                      isOn: model.filter.cities.contains(item.value)) {
                                toggleString(\.cities, item.value)
                            }
                        }
                    }
                }

                Section {
                    Button("清除全部筛选") { model.filter = AssetFilter() }
                        .disabled(model.filter.isEmpty)
                }
            }
            .navigationTitle("筛选")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) { Button("完成") { dismiss() } }
            }
        }
    }

    private func filterRow(title: String, count: Int?, isOn: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack {
                Text(title)
                Spacer()
                if let count { Text("\(count)").foregroundStyle(.secondary) }
                Image(systemName: isOn ? "checkmark.circle.fill" : "circle")
                    .foregroundStyle(isOn ? Color.accentColor : Color.secondary)
            }
        }
        .buttonStyle(.plain)
    }

    private func toggleKind(_ value: Int) {
        var next = model.filter
        if next.kinds.contains(value) { next.kinds.remove(value) } else { next.kinds.insert(value) }
        model.filter = next
    }

    private func toggleRating(_ value: Int) {
        var next = model.filter
        if next.ratings.contains(value) { next.ratings.remove(value) } else { next.ratings.insert(value) }
        model.filter = next
    }

    private func toggleString(_ keyPath: WritableKeyPath<AssetFilter, Set<String>>, _ value: String) {
        var next = model.filter
        var set = next[keyPath: keyPath]
        if set.contains(value) { set.remove(value) } else { set.insert(value) }
        next[keyPath: keyPath] = set
        model.filter = next
    }

    private func kindTitle(_ raw: String) -> String {
        switch raw {
        case "1": return "图片"
        case "2": return "视频"
        default: return "类型 \(raw)"
        }
    }
}
