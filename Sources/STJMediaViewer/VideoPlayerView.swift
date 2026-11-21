import AVKit
import SwiftUI

/// 画廊里的视频播放器：用 AppKit 的 AVPlayerView，自带播放/进度/音量控件。
struct VideoPlayerView: NSViewRepresentable {
    var url: URL

    func makeNSView(context: Context) -> AVPlayerView {
        let view = AVPlayerView()
        view.controlsStyle = .inline
        view.videoGravity = .resizeAspect
        view.showsFullScreenToggleButton = true
        view.showsFrameSteppingButtons = true
        view.player = context.coordinator.makePlayer(for: url)
        return view
    }

    func updateNSView(_ view: AVPlayerView, context: Context) {
        guard context.coordinator.currentURL != url else { return }
        context.coordinator.replacePlayer(in: view, url: url)
    }

    static func dismantleNSView(_ view: AVPlayerView, coordinator: Coordinator) {
        view.player?.pause()
        view.player = nil
    }

    func makeCoordinator() -> Coordinator { Coordinator(url: url) }

    final class Coordinator {
        private(set) var currentURL: URL
        private var player: AVPlayer?

        init(url: URL) {
            currentURL = url
            player = AVPlayer(url: url)
        }

        func makePlayer(for url: URL) -> AVPlayer? {
            if player == nil { player = AVPlayer(url: url) }
            return player
        }

        func replacePlayer(in view: AVPlayerView, url: URL) {
            player?.pause()
            player = AVPlayer(url: url)
            currentURL = url
            view.player = player
        }
    }
}
