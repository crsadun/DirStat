import Foundation

struct ScanProgress {
    var filesScanned: Int64
    var dirsScanned: Int64
    var bytesScanned: Int64
    var currentPath: String
}

struct ScanResult {
    let root: DirNode
    let extensions: ExtensionStats
    let elapsed: TimeInterval
    let errors: [String]
    let canceled: Bool
}

/// Fans out across the root's immediate children using
/// `DispatchQueue.concurrentPerform`, then recurses sequentially within each
/// subtree. Symbolic links are skipped to avoid loops & double-counting.
final class Scanner {
    private let rootPath: String
    private let onProgress: (ScanProgress) -> Void
    private let cancel = AtomicBool(false)
    private let scannedFiles = AtomicInt64()
    private let scannedDirs = AtomicInt64()
    private let scannedBytes = AtomicInt64()
    private let extensions = ExtensionStats()
    private let errorsLock = NSLock()
    private var errors: [String] = []
    private let reportLock = NSLock()
    private var lastReportTime = Date(timeIntervalSince1970: 0)

    // FileManager is documented as thread-safe for most read operations on macOS;
    // a single shared instance is fine across worker threads.
    private let fm = FileManager.default

    private static let resourceKeys: [URLResourceKey] = [
        .isDirectoryKey,
        .isSymbolicLinkKey,
        .fileSizeKey
    ]

    init(rootPath: String, onProgress: @escaping (ScanProgress) -> Void) {
        self.rootPath = rootPath
        self.onProgress = onProgress
    }

    func requestStop() {
        cancel.set(true)
    }

    func runAsync(completion: @escaping (ScanResult) -> Void) {
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            guard let self = self else { return }
            let result = self.run()
            DispatchQueue.main.async {
                completion(result)
            }
        }
    }

    private func run() -> ScanResult {
        let start = Date()
        var normalized = rootPath
        if normalized.count > 1 && normalized.hasSuffix("/") {
            normalized.removeLast()
        }
        let root = DirNode(name: normalized, parent: nil, isDirectory: true)

        let rootURL = URL(fileURLWithPath: rootPath, isDirectory: true)
        let topChildren: [URL]
        do {
            topChildren = try fm.contentsOfDirectory(
                at: rootURL,
                includingPropertiesForKeys: Self.resourceKeys,
                options: [])
        } catch {
            recordError("\(rootPath): \(error.localizedDescription)")
            return buildResult(root: root, start: start)
        }

        if cancel.load() || topChildren.isEmpty {
            return buildResult(root: root, start: start)
        }

        let appendLock = NSLock()
        // Cap concurrency: GCD's concurrentPerform already self-throttles to core count.
        DispatchQueue.concurrentPerform(iterations: topChildren.count) { [weak self] i in
            guard let self = self else { return }
            if self.cancel.load() { return }
            let url = topChildren[i]
            if let n = self.makeChild(parent: root, url: url) {
                appendLock.lock()
                root.children?.append(n)
                appendLock.unlock()
                if n.isDirectory {
                    self.scanRecursive(dir: url, node: n)
                }
            }
        }

        return buildResult(root: root, start: start)
    }

    private func makeChild(parent: DirNode, url: URL) -> DirNode? {
        do {
            let values = try url.resourceValues(forKeys: Set(Self.resourceKeys))
            if values.isSymbolicLink == true { return nil }
            let isDir = values.isDirectory ?? false
            let name = url.lastPathComponent
            if isDir {
                let node = DirNode(name: name, parent: parent, isDirectory: true)
                scannedDirs.add(1)
                return node
            } else {
                let len = Int64(values.fileSize ?? 0)
                let node = DirNode(name: name, parent: parent, isDirectory: false)
                node.size = len
                extensions.add(extension: Util.extensionOf(name), size: len)
                scannedFiles.add(1)
                scannedBytes.add(len)
                maybeReport(currentPath: url.path)
                return node
            }
        } catch {
            recordError("\(url.path): \(error.localizedDescription)")
            return nil
        }
    }

    private func scanRecursive(dir: URL, node: DirNode) {
        if cancel.load() { return }
        let entries: [URL]
        do {
            entries = try fm.contentsOfDirectory(
                at: dir,
                includingPropertiesForKeys: Self.resourceKeys,
                options: [])
        } catch {
            recordError("\(dir.path): \(error.localizedDescription)")
            return
        }
        for entry in entries {
            if cancel.load() { return }
            if let child = makeChild(parent: node, url: entry) {
                node.children?.append(child)
                if child.isDirectory {
                    scanRecursive(dir: entry, node: child)
                }
            }
        }
    }

    private func recordError(_ msg: String) {
        errorsLock.lock()
        errors.append(msg)
        errorsLock.unlock()
    }

    /// Throttled: at most one report per 100 ms, gated to roughly every 16k files
    /// so we don't take the lock on every single file.
    private func maybeReport(currentPath: String) {
        let files = scannedFiles.load()
        if (files & 0x3FFF) != 0 { return }
        let p: ScanProgress
        reportLock.lock()
        let now = Date()
        if now.timeIntervalSince(lastReportTime) < 0.1 {
            reportLock.unlock()
            return
        }
        lastReportTime = now
        p = ScanProgress(
            filesScanned: files,
            dirsScanned: scannedDirs.load(),
            bytesScanned: scannedBytes.load(),
            currentPath: currentPath)
        reportLock.unlock()
        onProgress(p)
    }

    private func buildResult(root: DirNode, start: Date) -> ScanResult {
        root.finalizePostScan()
        let final = ScanProgress(
            filesScanned: scannedFiles.load(),
            dirsScanned: scannedDirs.load(),
            bytesScanned: scannedBytes.load(),
            currentPath: "")
        onProgress(final)
        errorsLock.lock()
        let errs = errors
        errorsLock.unlock()
        return ScanResult(
            root: root,
            extensions: extensions,
            elapsed: Date().timeIntervalSince(start),
            errors: errs,
            canceled: cancel.load())
    }
}
