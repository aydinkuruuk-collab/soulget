// swift-tools-version:5.9
// soulget for Mac: a small SwiftUI app around the soulget engine (../standalone).
import PackageDescription

let package = Package(
    name: "SoulgetMac",
    platforms: [.macOS(.v13)],
    targets: [
        .executableTarget(name: "SoulgetApp", path: "Sources/SoulgetApp"),
    ]
)
