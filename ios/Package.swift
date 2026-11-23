// swift-tools-version:5.9
import PackageDescription

/// iPadOS / iOS 端（第一阶段：可编译的 iPad 素材库 P0）。
///
/// 复用根仓库的 `STJCore`（数据层与缩略图已平台无关），本包只提供 SwiftUI 视图与
/// 移动端专用的库模型。用 Xcode 打开本目录即可把它加进 iOS App 工程：
/// 新建 App target → Build Phases 里链接 `STJMobile`（或直接把 Sources 拖进 target）。
let package = Package(
    name: "STJMediaViewerMobile",
    platforms: [.iOS(.v17)],
    products: [
        .library(name: "STJMobile", targets: ["STJMobile"]),
    ],
    dependencies: [
        .package(path: ".."),
    ],
    targets: [
        .target(
            name: "STJMobile",
            dependencies: [.product(name: "STJCore", package: "STJMediaViewer")],
            path: "Sources/STJMobile"
        ),
    ]
)
