import STJCore
import SwiftUI

/// 云端目录浏览：列出 WebDAV 上的文件与目录，勾选后下载到「数据目录/云同步下载」并增量导入。
struct RemoteBrowserView: View {
    @EnvironmentObject private var model: LibraryModel
    @Environment(\.dismiss) private var dismiss
    @State private var selection: Set<String> = []

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            entryList
            Divider()
            footer
        }
        .frame(minWidth: 720, idealWidth: 780, minHeight: 460, idealHeight: 540)
        .onChange(of: selection) { _, value in
            model.remoteSelection = value
        }
        .onChange(of: model.remoteEntries) { _, _ in
            selection = model.remoteSelection
        }
        .onChange(of: model.remoteSelection) { _, value in
            if selection != value { selection = value }
        }
    }

    private var header: some View {
        HStack(spacing: 8) {
            Button {
                model.remoteGoUp()
            } label: {
                Image(systemName: "chevron.up")
            }
            .buttonStyle(.borderless)
            .disabled(model.remotePath.isEmpty)
            .help("上一级目录")

            Image(systemName: "externaldrive.connected.to.line.below")
                .foregroundStyle(.secondary)
            Text(model.remotePath.isEmpty ? "云端根目录" : "/" + model.remotePath)
                .font(.system(size: 12, weight: .medium))
                .lineLimit(1)
                .truncationMode(.middle)

            Spacer()

            if model.isRemoteLoading {
                ProgressView().controlSize(.small)
            } else {
                Button {
                    Task { await model.loadRemoteEntries(model.remotePath) }
                } label: {
                    Image(systemName: "arrow.clockwise")
                }
                .buttonStyle(.borderless)
                .help("刷新")
            }

            Button("关闭") { dismiss() }
                .keyboardShortcut(.cancelAction)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .stjGlass(cornerRadius: 0, surface: .clear)
    }

    private var entryList: some View {
        List(selection: $selection) {
            ForEach(model.remoteEntries) { entry in
                HStack(spacing: 8) {
                    Image(systemName: entry.isDirectory ? "folder.fill" : icon(for: entry))
                        .foregroundStyle(entry.isDirectory ? Color.accentColor : Color.secondary)
                        .font(.system(size: 12))
                    Text(entry.name.isEmpty ? entry.path : entry.name)
                        .font(.system(size: 12))
                        .lineLimit(1)
                        .truncationMode(.middle)
                    Spacer()
                    Text(entry.sizeText)
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                        .monospacedDigit()
                    if let modified = entry.modified {
                        Text(modified, format: .dateTime.year().month().day().hour().minute())
                            .font(.system(size: 11))
                            .foregroundStyle(.secondary)
                            .monospacedDigit()
                    }
                }
                .tag(entry.path)
                .listRowBackground(selection.contains(entry.path) ? Color.accentColor.opacity(0.16) : Color.clear)
                .contentShape(Rectangle())
                .simultaneousGesture(TapGesture(count: 2).onEnded {
                    if entry.isDirectory { model.enterRemoteDirectory(entry) }
                })
            }
        }
        .listStyle(.inset)
        .overlay {
            if model.remoteEntries.isEmpty && !model.isRemoteLoading {
                ContentUnavailableView("云端目录为空", systemImage: "externaldrive.badge.questionmark",
                                       description: Text(model.remoteStatus ?? "先上传一些素材，或在设置 → 云同步里检查配置"))
            }
        }
    }

    private var footer: some View {
        HStack(spacing: 12) {
            if let progress = model.remoteDownloadProgress {
                ProgressView(value: Double(progress.done), total: Double(max(progress.total, 1)))
                    .frame(width: 180)
                Text("下载中 \(progress.done)/\(progress.total)")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
                Button("取消") { model.cancelRemoteDownload() }
                    .controlSize(.small)
            } else if let status = model.remoteStatus {
                Text(status)
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
            }
            Spacer()
            Button("下载所选（\(model.remoteSelection.count)）") {
                model.downloadRemoteSelection()
            }
            .keyboardShortcut(.defaultAction)
            .disabled(model.remoteSelection.isEmpty || model.isRemoteDownloading || model.dataDirectory == nil)
            .help("下载到「数据目录/云同步下载」，并增量导入当前素材库")
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .stjGlass(cornerRadius: 0, surface: .clear)
    }

    private func icon(for entry: RemoteEntry) -> String {
        if entry.name.lowercased().hasSuffix(".mov") || entry.name.lowercased().hasSuffix(".mp4") {
            return "film"
        }
        if entry.name.lowercased().hasSuffix(".xmp") { return "doc.text" }
        return "photo"
    }
}
