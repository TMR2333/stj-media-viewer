// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "STJMediaViewer",
    platforms: [.macOS(.v14), .iOS(.v17)],
    products: [
        .executable(name: "STJMediaViewer", targets: ["STJMediaViewer"]),
        .executable(name: "stjcheck", targets: ["stjcheck"]),
        .library(name: "STJCore", targets: ["STJCore"]),
    ],
    targets: [
        .target(
            name: "STJCore",
            path: "Sources/STJCore",
            linkerSettings: [
                .linkedLibrary("sqlite3"),
                .linkedFramework("AppKit"),
                .linkedFramework("ImageIO"),
                .linkedFramework("QuickLookThumbnailing"),
                .linkedFramework("Security"),
                .linkedFramework("CoreServices"),
            ]
        ),
        .executableTarget(
            name: "STJMediaViewer",
            dependencies: ["STJCore"],
            path: "Sources/STJMediaViewer",
            linkerSettings: [
                .linkedFramework("AppKit"),
                .linkedFramework("QuickLookThumbnailing"),
                .linkedFramework("SwiftUI"),
            ]
        ),
        .executableTarget(
            name: "stjcheck",
            dependencies: ["STJCore"],
            path: "Sources/stjcheck"
        ),
        .testTarget(
            name: "STJCoreTests",
            dependencies: ["STJCore"],
            path: "Tests/STJCoreTests"
        ),
    ]
)