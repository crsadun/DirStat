import Foundation

enum Util {
    static func formatBytes(_ bytes: Int64) -> String {
        let K = 1024.0
        if bytes < Int64(K) { return "\(bytes) B" }
        var v = Double(bytes) / K
        if v < K { return String(format: "%.1f KB", v) }
        v /= K
        if v < K { return String(format: "%.1f MB", v) }
        v /= K
        if v < K { return String(format: "%.2f GB", v) }
        v /= K
        return String(format: "%.2f TB", v)
    }

    static let countFormatter: NumberFormatter = {
        let f = NumberFormatter()
        f.numberStyle = .decimal
        return f
    }()

    static func formatCount(_ n: Int64) -> String {
        return countFormatter.string(from: NSNumber(value: n)) ?? "\(n)"
    }

    /// Returns the file extension *including the leading dot* (lowercased),
    /// or "<no extension>" if the name has none. Matches the labels used
    /// by `ExtensionStats` so map lookups stay consistent.
    static func extensionOf(_ name: String) -> String {
        let url = URL(fileURLWithPath: name)
        let ext = url.pathExtension
        if ext.isEmpty { return "<no extension>" }
        return "." + ext.lowercased()
    }
}

final class AtomicInt64: @unchecked Sendable {
    private let lock = NSLock()
    private var value: Int64

    init(_ initial: Int64 = 0) { value = initial }

    @discardableResult
    func add(_ delta: Int64) -> Int64 {
        lock.lock(); defer { lock.unlock() }
        value += delta
        return value
    }

    func load() -> Int64 {
        lock.lock(); defer { lock.unlock() }
        return value
    }

    func set(_ v: Int64) {
        lock.lock(); value = v; lock.unlock()
    }
}

final class AtomicBool: @unchecked Sendable {
    private let lock = NSLock()
    private var value: Bool

    init(_ initial: Bool = false) { value = initial }

    func load() -> Bool {
        lock.lock(); defer { lock.unlock() }
        return value
    }

    func set(_ v: Bool) {
        lock.lock(); value = v; lock.unlock()
    }
}
