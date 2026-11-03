import CuebotKit
import Foundation

/// Iconify icons for deck buttons: fetched once from api.iconify.design, kept in memory and in the Caches folder, so
/// a deck keeps its icons offline after the first look. Unknown or undrawable icons are remembered as missing for the
/// session (the button then shows a stand-in symbol or just its title).
actor IconCache {
    static let shared = IconCache()

    private var memory: [String: SVGIcon] = [:]
    private var missing: Set<String> = []
    private var inFlight: [String: Task<SVGIcon?, Never>] = [:]
    private let folder: URL
    private let session: URLSession

    init(session: URLSession = .shared) {
        self.session = session
        folder = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0].appending(path: "IconifyIcons", directoryHint: .isDirectory)
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
    }

    func icon(_ name: String) async -> SVGIcon? {
        let key = name.lowercased()
        if let icon = memory[key] { return icon }
        if missing.contains(key) { return nil }
        if let task = inFlight[key] { return await task.value }
        guard let url = Iconify.url(for: key) else {
            missing.insert(key)
            return nil
        }
        let file = folder.appending(path: key.replacingOccurrences(of: ":", with: "__") + ".svg")
        let session = session
        let task = Task<SVGIcon?, Never> {
            if let text = try? String(contentsOf: file, encoding: .utf8), let icon = SVGIcon(svg: text) { return icon }
            guard let (data, response) = try? await session.data(from: url), (response as? HTTPURLResponse)?.statusCode == 200,
                  let text = String(data: data, encoding: .utf8), let icon = SVGIcon(svg: text) else { return nil }
            try? data.write(to: file, options: .atomic)
            return icon
        }
        inFlight[key] = task
        let icon = await task.value
        inFlight[key] = nil
        if let icon { memory[key] = icon } else { missing.insert(key) }
        return icon
    }
}
