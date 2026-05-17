import AppKit
import CoreGraphics

protocol TreemapViewDelegate: AnyObject {
    func treemapView(_ view: TreemapView, didSelect node: DirNode)
    func treemapView(_ view: TreemapView, didActivate node: DirNode)
    func treemapView(_ view: TreemapView, didRequestContextFor node: DirNode, at viewPoint: NSPoint)
}

/// Custom view that holds a rendered CGImage of the treemap and overlays
/// extension/selection highlights on top of it. The renderer's layout rects
/// are kept in *device-pixel* coordinates; we convert when hit-testing and
/// when drawing overlays.
final class TreemapView: NSView {
    weak var delegate: TreemapViewDelegate?
    var colorMap: ExtensionColorMap?

    static let backgroundColor = NSColor(white: 28.0/255.0, alpha: 1)

    private var root: DirNode?
    private var highlightNode: DirNode?
    private var highlightExt: String?
    private var highlightRect: CGRect?       // device pixels
    private var image: CGImage?
    private var imagePixelWidth: Int = 0
    private var imagePixelHeight: Int = 0
    private let renderer = TreemapRenderer()
    private var pendingRender = false

    /// Top-left origin, like the original WinForms version.
    override var isFlipped: Bool { return true }

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        layer?.backgroundColor = Self.backgroundColor.cgColor
    }
    required init?(coder: NSCoder) { fatalError() }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { return true }
    override var acceptsFirstResponder: Bool { return true }

    func setRoot(_ r: DirNode?) {
        root = r
        highlightNode = r
        highlightExt = nil
        scheduleRender()
    }

    func setHighlight(_ node: DirNode?) {
        highlightNode = node
        updateHighlightRect()
        needsDisplay = true
    }

    func setExtensionHighlight(_ ext: String?) {
        if highlightExt == ext { return }
        highlightExt = ext
        needsDisplay = true
    }

    private static func extensionMatches(_ node: DirNode, _ ext: String) -> Bool {
        if node.isDirectory { return false }
        return Util.extensionOf(node.name).caseInsensitiveCompare(ext) == .orderedSame
    }

    private static func isDescendant(_ candidate: DirNode, of target: DirNode) -> Bool {
        var c: DirNode? = candidate
        while let cur = c {
            if cur === target { return true }
            c = cur.parent
        }
        return false
    }

    private func updateHighlightRect() {
        highlightRect = nil
        guard let target = highlightNode else { return }
        var minX = CGFloat.greatestFiniteMagnitude
        var minY = CGFloat.greatestFiniteMagnitude
        var maxX = -CGFloat.greatestFiniteMagnitude
        var maxY = -CGFloat.greatestFiniteMagnitude
        var found = false
        for it in renderer.items {
            if Self.isDescendant(it.node, of: target) {
                found = true
                let r = it.rect
                if r.minX < minX { minX = r.minX }
                if r.minY < minY { minY = r.minY }
                if r.maxX > maxX { maxX = r.maxX }
                if r.maxY > maxY { maxY = r.maxY }
            }
        }
        if found {
            highlightRect = CGRect(x: minX, y: minY, width: maxX - minX, height: maxY - minY)
        }
    }

    override func setFrameSize(_ newSize: NSSize) {
        super.setFrameSize(newSize)
        scheduleRender()
    }

    override func viewDidChangeBackingProperties() {
        super.viewDidChangeBackingProperties()
        scheduleRender()
    }

    private func scheduleRender() {
        if pendingRender { return }
        pendingRender = true
        DispatchQueue.main.async { [weak self] in
            guard let self = self else { return }
            self.pendingRender = false
            self.renderNow()
            self.needsDisplay = true
        }
    }

    private func renderNow() {
        let scale = window?.backingScaleFactor ?? 1
        let w = Int((bounds.width * scale).rounded())
        let h = Int((bounds.height * scale).rounded())
        if w < 4 || h < 4 || root == nil {
            image = nil
            imagePixelWidth = 0
            imagePixelHeight = 0
            return
        }
        let bytesPerRow = w * 4
        let bitmapInfo = CGImageAlphaInfo.premultipliedFirst.rawValue
            | CGBitmapInfo.byteOrder32Little.rawValue
        let cs = CGColorSpaceCreateDeviceRGB()
        guard let ctx = CGContext(data: nil,
                                  width: w,
                                  height: h,
                                  bitsPerComponent: 8,
                                  bytesPerRow: bytesPerRow,
                                  space: cs,
                                  bitmapInfo: bitmapInfo),
              let buf = ctx.data?.assumingMemoryBound(to: UInt8.self) else {
            image = nil
            return
        }
        let picker: (DirNode) -> NSColor = { [weak self] node in
            self?.colorMap?.color(for: node) ?? ExtensionColorMap.fallback
        }
        renderer.render(into: buf,
                        width: w, height: h,
                        stride: ctx.bytesPerRow,
                        root: root,
                        colorOf: picker,
                        background: Self.backgroundColor)
        image = ctx.makeImage()
        imagePixelWidth = w
        imagePixelHeight = h
        updateHighlightRect()
    }

    private func toPoints(_ r: CGRect) -> CGRect {
        let s = window?.backingScaleFactor ?? 1
        guard s > 0 else { return r }
        return CGRect(x: r.minX / s, y: r.minY / s, width: r.width / s, height: r.height / s)
    }

    override func draw(_ dirtyRect: NSRect) {
        guard let ctx = NSGraphicsContext.current?.cgContext else { return }
        if let img = image {
            ctx.saveGState()
            // Image was rendered with top-left origin matching our flipped coords.
            ctx.draw(img, in: bounds)
            ctx.restoreGState()
        } else {
            ctx.saveGState()
            ctx.setFillColor(Self.backgroundColor.cgColor)
            ctx.fill(bounds)
            let msg = "No data — open a folder or volume to begin." as NSString
            let attrs: [NSAttributedString.Key: Any] = [
                .foregroundColor: NSColor.gray,
                .font: NSFont.systemFont(ofSize: 13)
            ]
            let size = msg.size(withAttributes: attrs)
            // Note: the view is flipped; drawing text needs a non-flipped context
            // or NSStringDrawing's flipped variant.
            let rect = NSRect(x: (bounds.width - size.width) / 2,
                              y: (bounds.height - size.height) / 2,
                              width: size.width, height: size.height)
            NSGraphicsContext.current?.saveGraphicsState()
            let nsContext = NSGraphicsContext(cgContext: ctx, flipped: true)
            NSGraphicsContext.current = nsContext
            msg.draw(in: rect, withAttributes: attrs)
            NSGraphicsContext.current?.restoreGraphicsState()
            ctx.restoreGState()
            return
        }

        // Extension highlight: translucent white fill + thin border over matching cells.
        if let ext = highlightExt, !ext.isEmpty {
            ctx.saveGState()
            ctx.setFillColor(NSColor.white.withAlphaComponent(0.35).cgColor)
            ctx.setStrokeColor(NSColor.white.cgColor)
            ctx.setLineWidth(1.5)
            for it in renderer.items {
                if !Self.extensionMatches(it.node, ext) { continue }
                let r = toPoints(it.rect)
                if r.width < 1 || r.height < 1 { continue }
                ctx.fill(r)
                if r.width >= 3 && r.height >= 3 {
                    ctx.stroke(r.insetBy(dx: 0.5, dy: 0.5))
                }
            }
            ctx.restoreGState()
        }

        // Selection outline (dotted).
        if let r = highlightRect {
            let pointsRect = toPoints(r)
            ctx.saveGState()
            ctx.setStrokeColor(NSColor.white.cgColor)
            ctx.setLineWidth(2)
            ctx.setLineDash(phase: 0, lengths: [2, 3])
            ctx.stroke(pointsRect.insetBy(dx: 1, dy: 1))
            ctx.restoreGState()
        }
    }

    // ---- Mouse handling ----

    private func itemAt(_ viewPoint: NSPoint) -> TreemapItem? {
        let scale = window?.backingScaleFactor ?? 1
        let pixelPoint = CGPoint(x: viewPoint.x * scale, y: viewPoint.y * scale)
        return renderer.itemAt(pixelPoint)
    }

    override func mouseDown(with event: NSEvent) {
        let p = convert(event.locationInWindow, from: nil)
        guard let item = itemAt(p) else { return }
        highlightNode = item.node
        updateHighlightRect()
        needsDisplay = true
        delegate?.treemapView(self, didSelect: item.node)
        if event.clickCount >= 2 {
            delegate?.treemapView(self, didActivate: item.node)
        }
    }

    override func rightMouseDown(with event: NSEvent) {
        let p = convert(event.locationInWindow, from: nil)
        guard let item = itemAt(p) else { return }
        highlightNode = item.node
        updateHighlightRect()
        needsDisplay = true
        delegate?.treemapView(self, didSelect: item.node)
        delegate?.treemapView(self, didRequestContextFor: item.node, at: p)
    }
}
