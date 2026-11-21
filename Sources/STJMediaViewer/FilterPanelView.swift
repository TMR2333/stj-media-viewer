import STJCore
import SwiftUI

struct FilterPanelView: View {
    @EnvironmentObject private var model: LibraryModel

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text("筛选").font(.headline)
                Spacer()
                if model.filter.activeCount > 0 {
                    Text("\(model.filter.activeCount) 项条件")
                        .font(.system(size: 10))
                        .foregroundStyle(.secondary)
                }
                Button("重置") { model.clearFilters() }
                    .buttonStyle(.link)
                    .font(.system(size: 11))
            }
            .padding(.horizontal, 14)
            .padding(.top, 12)
            .padding(.bottom, 8)
            Divider()

            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    kindSection
                    facetSection("年份", items: model.facets.years, selection: model.filter.years,
                                 set: { model.filter.years = $0 },
                                 unset: .year)
                    facetSection("城市", items: model.facets.cities, selection: model.filter.cities,
                                 set: { model.filter.cities = $0 },
                                 unset: .city)
                    facetSection("相机型号", items: model.facets.devices, selection: model.filter.devices,
                                 set: { model.filter.devices = $0 },
                                 unset: .device)
                    facetSection("主题", items: model.facets.themes, selection: model.filter.themes,
                                 set: { model.filter.themes = $0 },
                                 unset: .theme)
                    facetSection("影调", items: model.facets.tones, selection: model.filter.tones,
                                 set: { model.filter.tones = $0 },
                                 unset: .tone)
                    ratingSection
                    colorLabelSection
                    pickSection
                    postSection
                    unsetSection
                }
                .padding(14)
            }
            .frame(maxHeight: 460)
        }
        .frame(width: 380)
    }

    // MARK: 分区

    private var kindSection: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("类型").font(.system(size: 11, weight: .semibold)).foregroundStyle(.secondary)
            Picker("类型", selection: Binding(
                get: { model.filter.kinds },
                set: { model.filter.kinds = $0 }
            )) {
                Text("全部").tag(Set<Int>())
                Text("图片").tag(Set([1]))
                Text("视频").tag(Set([2]))
            }
            .pickerStyle(.segmented)
            .labelsHidden()

            FlowLayout(spacing: 6) {
                ForEach(model.facets.formats) { format in
                    FilterChip(title: format.value.uppercased(),
                               isActive: model.filter.formats.contains(format.value)) {
                        toggle(format.value, in: &model.filter.formats)
                    }
                }
            }
        }
    }

    private func facetSection(_ title: String, items: [FacetItem], selection: Set<String>,
                              set: @escaping (Set<String>) -> Void,
                              unset: FilterDimension) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text(title).font(.system(size: 11, weight: .semibold)).foregroundStyle(.secondary)
                Spacer()
                if !selection.isEmpty {
                    Button("清除") { set([]) }
                        .buttonStyle(.link)
                        .font(.system(size: 10))
                }
            }
            if items.isEmpty {
                Text("暂无数据").font(.system(size: 10)).foregroundStyle(.tertiary)
            } else {
                FlowLayout(spacing: 6) {
                    ForEach(items) { item in
                        FilterChip(title: "\(item.value)  \(item.count)",
                                   isActive: selection.contains(item.value)) {
                            var next = selection
                            if next.contains(item.value) {
                                next.remove(item.value)
                            } else {
                                next.insert(item.value)
                            }
                            set(next)
                        }
                    }
                }
            }
        }
    }

    private var ratingSection: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("评分").font(.system(size: 11, weight: .semibold)).foregroundStyle(.secondary)
            FlowLayout(spacing: 6) {
                ForEach(model.facets.ratings) { item in
                    FilterChip(title: "★ \(item.value)  \(item.count)",
                               isActive: model.filter.ratings.contains(Int(item.value) ?? 0)) {
                        var next = model.filter.ratings
                        let rating = Int(item.value) ?? 0
                        if next.contains(rating) {
                            next.remove(rating)
                        } else {
                            next.insert(rating)
                        }
                        model.filter.ratings = next
                    }
                }
            }
        }
    }

    private var colorLabelSection: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("色标").font(.system(size: 11, weight: .semibold)).foregroundStyle(.secondary)
            FlowLayout(spacing: 6) {
                ForEach(model.facets.colorLabels) { item in
                    FilterChip(title: "\(item.value)  \(item.count)",
                               isActive: model.filter.colorLabels.contains(item.value)) {
                        toggle(item.value, in: &model.filter.colorLabels)
                    }
                }
            }
        }
    }

    private var pickSection: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("选用状态").font(.system(size: 11, weight: .semibold)).foregroundStyle(.secondary)
            Picker("选用", selection: Binding(
                get: { model.filter.pick },
                set: { model.filter.pick = $0 }
            )) {
                Text("全部").tag(Int?.none)
                Text("精选").tag(Int?.some(1))
                Text("废片").tag(Int?.some(0))
            }
            .pickerStyle(.segmented)
            .labelsHidden()
        }
    }

    private var postSection: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("调色状态").font(.system(size: 11, weight: .semibold)).foregroundStyle(.secondary)
            HStack(spacing: 6) {
                FilterChip(title: "未调色", isActive: model.filter.postProcesses.contains(1)) {
                    toggleInt(1, in: &model.filter.postProcesses)
                }
                FilterChip(title: "已调色", isActive: model.filter.postProcesses.contains(2)) {
                    toggleInt(2, in: &model.filter.postProcesses)
                }
                FilterChip(title: "Log 素材", isActive: model.filter.logMode == true) {
                    model.filter.logMode = model.filter.logMode == true ? nil : true
                }
            }
        }
    }

    private var unsetSection: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("未填写（__unset__）").font(.system(size: 11, weight: .semibold)).foregroundStyle(.secondary)
            FlowLayout(spacing: 6) {
                ForEach([FilterDimension.year, .city, .theme, .tone, .colorLabel, .device], id: \.self) { dimension in
                    let count = model.facets.unsetCounts[dimension] ?? 0
                    if count > 0 {
                        FilterChip(title: "\(dimension.title)未填写  \(count)",
                                   isActive: model.filter.unsetDimensions.contains(dimension)) {
                            if model.filter.unsetDimensions.contains(dimension) {
                                model.filter.unsetDimensions.remove(dimension)
                            } else {
                                model.filter.unsetDimensions.insert(dimension)
                            }
                        }
                    }
                }
            }
            Text("“未填写”与具体值是并集关系，对齐 Windows 版的 __unset__ 伪值。")
                .font(.system(size: 9))
                .foregroundStyle(.tertiary)
        }
    }

    // MARK: 工具

    private func toggle(_ value: String, in set: inout Set<String>) {
        if set.contains(value) {
            set.remove(value)
        } else {
            set.insert(value)
        }
    }

    private func toggleInt(_ value: Int, in set: inout Set<Int>) {
        if set.contains(value) {
            set.remove(value)
        } else {
            set.insert(value)
        }
    }
}