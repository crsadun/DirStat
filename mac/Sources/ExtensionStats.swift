import Foundation
import AppKit

final class ExtensionStats: @unchecked Sendable {
    struct Entry {
        var ext: String        // canonical key, e.g. ".txt" or "<no extension>"
        var totalSize: Int64
        var fileCount: Int64
    }

    private let lock = NSLock()
    private var map: [String: Entry] = [:]

    /// `ext` should already be the canonical key from `Util.extensionOf`.
    func add(extension ext: String, size: Int64) {
        let key = ext
        lock.lock(); defer { lock.unlock() }
        if var e = map[key] {
            e.totalSize += size
            e.fileCount += 1
            map[key] = e
        } else {
            map[key] = Entry(ext: key, totalSize: size, fileCount: 1)
        }
    }

    func sortedBySize() -> [Entry] {
        lock.lock(); defer { lock.unlock() }
        return Array(map.values).sorted { $0.totalSize > $1.totalSize }
    }
}

/// Maps file extensions to colors. The top-N most-prominent extensions by total
/// bytes each get a palette slot; everything else falls back to gray.
final class ExtensionColorMap {
    static let palette: [NSColor] = [
        NSColor(srgbRed:   0/255.0, green: 102/255.0, blue: 204/255.0, alpha: 1),
        NSColor(srgbRed:  51/255.0, green: 153/255.0, blue:  51/255.0, alpha: 1),
        NSColor(srgbRed: 204/255.0, green:   0/255.0, blue:   0/255.0, alpha: 1),
        NSColor(srgbRed: 255/255.0, green: 153/255.0, blue:   0/255.0, alpha: 1),
        NSColor(srgbRed: 153/255.0, green:  51/255.0, blue: 204/255.0, alpha: 1),
        NSColor(srgbRed:  51/255.0, green: 153/255.0, blue: 204/255.0, alpha: 1),
        NSColor(srgbRed: 204/255.0, green: 153/255.0, blue:  51/255.0, alpha: 1),
        NSColor(srgbRed: 204/255.0, green: 102/255.0, blue: 153/255.0, alpha: 1),
        NSColor(srgbRed: 102/255.0, green: 153/255.0, blue:  51/255.0, alpha: 1),
        NSColor(srgbRed:  51/255.0, green:  51/255.0, blue: 153/255.0, alpha: 1),
        NSColor(srgbRed: 153/255.0, green: 102/255.0, blue:  51/255.0, alpha: 1),
        NSColor(srgbRed: 102/255.0, green:  51/255.0, blue: 153/255.0, alpha: 1)
    ]

    static let fallback = NSColor(white: 0.5, alpha: 1)

    private(set) var map: [String: NSColor] = [:]

    func build(from stats: ExtensionStats) {
        let list = stats.sortedBySize()
        var m: [String: NSColor] = [:]
        for (i, e) in list.enumerated() {
            if i >= Self.palette.count { break }
            m[e.ext] = Self.palette[i]
        }
        map = m
    }

    func color(for node: DirNode) -> NSColor {
        if node.isDirectory { return Self.fallback }
        return map[Util.extensionOf(node.name)] ?? Self.fallback
    }
}
