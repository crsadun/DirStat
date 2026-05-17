import Foundation

final class DirNode {
    let name: String
    weak var parent: DirNode?
    var children: [DirNode]?     // nil for files; empty array means "scanned, empty dir"
    let isDirectory: Bool
    var size: Int64 = 0
    var fileCount: Int64 = 0
    var dirCount: Int64 = 0

    init(name: String, parent: DirNode?, isDirectory: Bool) {
        self.name = name
        self.parent = parent
        self.isDirectory = isDirectory
        if isDirectory { self.children = [] }
    }

    /// Builds the full POSIX path by walking parent links.
    var fullPath: String {
        if let p = parent {
            let pp = p.fullPath
            if pp.hasSuffix("/") { return pp + name }
            return pp + "/" + name
        }
        return name
    }

    /// Bottom-up roll-up: recomputes each directory's size and counts from its
    /// children, then sorts children descending by size (treemap layout needs this).
    /// Files are left as-is; they are populated during scan.
    func finalizePostScan() {
        guard isDirectory, var ch = children else { return }
        for c in ch { c.finalizePostScan() }
        var s: Int64 = 0
        var fc: Int64 = 0
        var dc: Int64 = 0
        for c in ch {
            s += c.size
            if c.isDirectory {
                dc += 1 + c.dirCount
                fc += c.fileCount
            } else {
                fc += 1
            }
        }
        size = s
        fileCount = fc
        dirCount = dc
        ch.sort { $0.size > $1.size }
        children = ch
    }
}
