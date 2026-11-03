// swift-tools-version: 6.0
// CuebotKit: networking and models for the Cuebot Companion app (no third-party packages). macOS is listed so the
// tests also run with `swift test` on a Mac.
import PackageDescription

let package = Package(
    name: "CuebotKit",
    platforms: [.iOS(.v18), .macOS(.v15)],
    products: [.library(name: "CuebotKit", targets: ["CuebotKit"])],
    targets: [
        .target(name: "CuebotKit"),
        .testTarget(name: "CuebotKitTests", dependencies: ["CuebotKit"]),
    ]
)
