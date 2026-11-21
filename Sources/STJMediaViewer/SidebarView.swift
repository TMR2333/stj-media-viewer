import STJCore
import SwiftUI

struct SidebarView: View {
    @EnvironmentObject private var model: LibraryModel

    private var selectionBinding: Binding<SidebarSelection?> {
        Binding(
            get: { model.sidebarSelection },
            set: { value in
                if let value {
                    model.sidebarSelection = value
                }
            }
        )
    }

    var body: some View {
        List(selection: selectionBinding) {
            Section("素材库") {
                ForEach(SidebarSection.allCases) { section in
                    row(title: section.title,
                        symbol: section.symbol,
                        count: count(for: section))
                        .tag(SidebarSelection.section(section))
                }
            }

            if !model.mergeGroups.isEmpty {
                Section("合并组") {
                    ForEach(model.mergeGroups) { group in
                        row(title: group.name.isEmpty ? "合并组" : group.name,
                            symbol: "rectangle.stack",
                            count: group.count)
                            .tag(SidebarSelection.mergeGroup(group.id))
                            .contextMenu {
                                Button("展开/收起") { model.toggleGroupExpansion(group) }
                            }
                    }
                }
            }

            Section("合集") {
                ForEach(model.collections) { collection in
                    row(title: collection.name,
                        symbol: "folder",
                        count: collection.count)
                        .tag(SidebarSelection.collection(collection.id))
                        .contextMenu {
                            Button("重命名…") { model.renameCollection(collection) }
                            Button("删除合集", role: .destructive) { model.deleteCollection(collection) }
                        }
                }
                Button {
                    model.createCollection()
                } label: {
                    Label("新建合集…", systemImage: "plus.circle")
                        .font(.system(size: 12))
                }
                .buttonStyle(.plain)
                .foregroundStyle(.secondary)
            }
        }
        .listStyle(.sidebar)
        .navigationSplitViewColumnWidth(min: 208, ideal: 236, max: 300)
        .safeAreaInset(edge: .bottom) {
            VStack(alignment: .leading, spacing: 6) {
                Divider()
                HStack(spacing: 8) {
                    Image(systemName: "externaldrive")
                        .foregroundStyle(.secondary)
                    VStack(alignment: .leading, spacing: 1) {
                        Text(model.dataDirectory?.lastPathComponent ?? "未打开素材库")
                            .font(.system(size: 11, weight: .medium))
                            .lineLimit(1)
                        Text(model.dataDirectory?.path ?? "请选择包含 catalog.db 的目录")
                            .font(.system(size: 9))
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                            .truncationMode(.middle)
                    }
                    Spacer()
                    Button {
                        Task { await model.openFromPanel() }
                    } label: {
                        Image(systemName: "folder")
                    }
                    .buttonStyle(.plain)
                    .help("切换素材库目录")
                }
                .padding(.horizontal, 10)
                .padding(.bottom, 8)
            }
            .background(.bar)
        }
    }

    private func row(title: String, symbol: String, count: Int) -> some View {
        HStack(spacing: 7) {
            Image(systemName: symbol)
                .font(.system(size: 11))
                .frame(width: 16)
                .foregroundStyle(.secondary)
            Text(title)
                .font(.system(size: 12))
                .lineLimit(1)
            Spacer(minLength: 4)
            if count > 0 {
                Text("\(count)")
                    .font(.system(size: 10))
                    .monospacedDigit()
                    .foregroundStyle(.secondary)
            }
        }
    }

    private func count(for section: SidebarSection) -> Int {
        switch section {
        case .all: return model.facets.total
        case .pick: return model.facets.picked
        case .rejected: return model.facets.rejected
        case .uncategorized: return model.facets.unsetCounts[.collection] ?? 0
        }
    }
}