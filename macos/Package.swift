// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "STJMediaViewer",
    platforms: [.macOS(.v13)],
    products: [
        .executable(name: "STJMediaViewer", targets: ["STJMediaViewer"])
    ],
    targets: [
        .executableTarget(
            name: "STJMediaViewer",
            path: "Sources/STJMediaViewer",
            linkerSettings: [
                .linkedLibrary("sqlite3"),
                .linkedFramework("QuickLookThumbnailing"),
                .linkedFramework("AppKit")
            ]
        )
    ]
)