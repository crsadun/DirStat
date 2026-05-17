import Foundation
import AppKit
import CoreGraphics

struct TreemapItem {
    let node: DirNode
    let rect: CGRect    // in *destination buffer* (device-pixel) coordinates
}

/// Bruls/Huijsen/van Wijk squarified treemap layout, with per-pixel cushion
/// shading on file rectangles. Direct pixel writes into a raw BGRA buffer.
final class TreemapRenderer {
    // Light direction (toward the surface, upper-left). Will be normalized.
    private static let LX: Double = -1.0
    private static let LY: Double = -1.0
    private static let LZ: Double =  4.0
    private static let LMag = (LX*LX + LY*LY + LZ*LZ).squareRoot()

    private static let cushionHeight: Double = 0.6
    private static let ambient: Double = 0.18

    private(set) var items: [TreemapItem] = []

    /// Buffer is BGRA (matches kCGImageAlphaPremultipliedFirst | byteOrder32Little).
    func render(into pixels: UnsafeMutablePointer<UInt8>,
                width: Int,
                height: Int,
                stride: Int,
                root: DirNode?,
                colorOf: (DirNode) -> NSColor,
                background: NSColor) {
        let (bgR, bgG, bgB) = rgbComponents(background)
        for y in 0..<height {
            var row = pixels + y * stride
            for _ in 0..<width {
                row[0] = bgB
                row[1] = bgG
                row[2] = bgR
                row[3] = 255
                row += 4
            }
        }
        items.removeAll(keepingCapacity: true)
        guard let root = root else { return }
        let bounds = CGRect(x: 0, y: 0, width: CGFloat(width), height: CGFloat(height))
        layout(node: root, rect: bounds)
        if items.isEmpty { return }
        for it in items {
            if it.rect.width < 1 || it.rect.height < 1 { continue }
            let c = colorOf(it.node)
            drawCushion(pixels: pixels, stride: stride, imgW: width, imgH: height,
                        r: it.rect, color: c)
        }
    }

    func itemAt(_ p: CGPoint) -> TreemapItem? {
        for it in items {
            if it.rect.contains(p) { return it }
        }
        return nil
    }

    // ---- Squarified layout ----

    private func layout(node: DirNode, rect: CGRect) {
        if rect.width < 1 || rect.height < 1 { return }
        if !node.isDirectory || node.children == nil || node.children!.isEmpty {
            if !node.isDirectory && node.size > 0 {
                items.append(TreemapItem(node: node, rect: rect))
            }
            return
        }
        let children = node.children!
        var total: Int64 = 0
        var contribCount = 0
        for c in children {
            if c.size > 0 { total += c.size; contribCount += 1 }
        }
        if total <= 0 || contribCount == 0 { return }
        let areaScale = Double(rect.width) * Double(rect.height) / Double(total)
        var contribNodes: [DirNode] = []
        contribNodes.reserveCapacity(contribCount)
        var areas: [Double] = []
        areas.reserveCapacity(contribCount)
        for c in children {
            if c.size <= 0 { continue }
            contribNodes.append(c)
            areas.append(Double(c.size) * areaScale)
        }
        squarify(nodes: contribNodes, areas: areas, rect: rect)
    }

    private func squarify(nodes: [DirNode], areas: [Double], rect inputRect: CGRect) {
        var rect = inputRect
        var idx = 0
        let n = nodes.count
        while idx < n {
            if rect.width < 1 || rect.height < 1 { return }
            let w = min(Double(rect.width), Double(rect.height))
            if w <= 0 { return }
            let rowStart = idx
            var rowSum = areas[idx]
            var rowMin = areas[idx]
            var rowMax = areas[idx]
            idx += 1
            while idx < n {
                let a = areas[idx]
                let newSum = rowSum + a
                let newMin = Swift.min(rowMin, a)
                let newMax = Swift.max(rowMax, a)
                let oldWorst = worst(sum: rowSum, mn: rowMin, mx: rowMax, w: w)
                let newWorst = worst(sum: newSum, mn: newMin, mx: newMax, w: w)
                if newWorst <= oldWorst {
                    rowSum = newSum; rowMin = newMin; rowMax = newMax
                    idx += 1
                } else { break }
            }
            if rect.width >= rect.height {
                var stripW = CGFloat(rowSum / Double(rect.height))
                if stripW > rect.width { stripW = rect.width }
                var y = rect.minY
                let yEnd = rect.minY + rect.height
                for i in rowStart..<idx {
                    let h = (i == idx - 1)
                        ? (yEnd - y)
                        : CGFloat(areas[i] / Double(stripW))
                    let r = CGRect(x: rect.minX, y: y, width: stripW, height: h)
                    if nodes[i].isDirectory {
                        layout(node: nodes[i], rect: r)
                    } else {
                        items.append(TreemapItem(node: nodes[i], rect: r))
                    }
                    y += h
                }
                rect = CGRect(x: rect.minX + stripW, y: rect.minY,
                              width: rect.width - stripW, height: rect.height)
            } else {
                var stripH = CGFloat(rowSum / Double(rect.width))
                if stripH > rect.height { stripH = rect.height }
                var x = rect.minX
                let xEnd = rect.minX + rect.width
                for i in rowStart..<idx {
                    let ww = (i == idx - 1)
                        ? (xEnd - x)
                        : CGFloat(areas[i] / Double(stripH))
                    let r = CGRect(x: x, y: rect.minY, width: ww, height: stripH)
                    if nodes[i].isDirectory {
                        layout(node: nodes[i], rect: r)
                    } else {
                        items.append(TreemapItem(node: nodes[i], rect: r))
                    }
                    x += ww
                }
                rect = CGRect(x: rect.minX, y: rect.minY + stripH,
                              width: rect.width, height: rect.height - stripH)
            }
        }
    }

    private func worst(sum: Double, mn: Double, mx: Double, w: Double) -> Double {
        let s2 = sum * sum
        let w2 = w * w
        return Swift.max(w2 * mx / s2, s2 / (w2 * mn))
    }

    // ---- Cushion shading ----

    private func drawCushion(pixels: UnsafeMutablePointer<UInt8>,
                             stride: Int,
                             imgW: Int,
                             imgH: Int,
                             r: CGRect,
                             color: NSColor) {
        var x0 = Int(r.minX.rounded(.down))
        var y0 = Int(r.minY.rounded(.down))
        var x1 = Int(r.maxX.rounded(.up))
        var y1 = Int(r.maxY.rounded(.up))
        if x0 < 0 { x0 = 0 }
        if y0 < 0 { y0 = 0 }
        if x1 > imgW { x1 = imgW }
        if y1 > imgH { y1 = imgH }
        if x1 - x0 < 1 || y1 - y0 < 1 { return }

        let cx = Double(r.minX) + Double(r.width) * 0.5
        let cy = Double(r.minY) + Double(r.height) * 0.5
        var hw = Double(r.width) * 0.5
        var hh = Double(r.height) * 0.5
        if hw < 0.5 { hw = 0.5 }
        if hh < 0.5 { hh = 0.5 }

        let k = Self.cushionHeight
        let tinyW = (x1 - x0) < 3
        let tinyH = (y1 - y0) < 3
        let (bR, bG, bB) = rgbComponents(color)
        let bRf = Double(bR), bGf = Double(bG), bBf = Double(bB)
        let lx = Self.LX, ly = Self.LY, lz = Self.LZ, lmag = Self.LMag
        let amb = Self.ambient

        for y in y0..<y1 {
            var row = pixels + y * stride + x0 * 4
            var ny = (Double(y) + 0.5 - cy) / hh
            if ny < -1.0 { ny = -1.0 } else if ny > 1.0 { ny = 1.0 }
            let oneMinusNy2 = 1.0 - ny * ny
            for x in x0..<x1 {
                var intensity: Double
                if tinyW || tinyH {
                    intensity = 0.7
                } else {
                    var nx = (Double(x) + 0.5 - cx) / hw
                    if nx < -1.0 { nx = -1.0 } else if nx > 1.0 { nx = 1.0 }
                    let oneMinusNx2 = 1.0 - nx * nx
                    let dzdx = -2.0 * k * nx * oneMinusNy2 / hw
                    let dzdy = -2.0 * k * ny * oneMinusNx2 / hh
                    let nxn = -dzdx, nyn = -dzdy, nzn = 1.0
                    let nmag = (nxn*nxn + nyn*nyn + nzn*nzn).squareRoot()
                    var dot = (lx*nxn + ly*nyn + lz*nzn) / (lmag * nmag)
                    if dot < 0 { dot = 0 }
                    intensity = amb + (1.0 - amb) * dot
                    if intensity > 1.0 { intensity = 1.0 }
                }
                var rr = Int(bRf * intensity)
                var gg = Int(bGf * intensity)
                var bb = Int(bBf * intensity)
                if rr > 255 { rr = 255 }
                if gg > 255 { gg = 255 }
                if bb > 255 { bb = 255 }
                row[0] = UInt8(bb)
                row[1] = UInt8(gg)
                row[2] = UInt8(rr)
                row[3] = 255
                row += 4
            }
        }
    }
}

func rgbComponents(_ color: NSColor) -> (UInt8, UInt8, UInt8) {
    let c = color.usingColorSpace(.deviceRGB) ?? color
    let r = UInt8(min(255, max(0, Int((c.redComponent * 255).rounded()))))
    let g = UInt8(min(255, max(0, Int((c.greenComponent * 255).rounded()))))
    let b = UInt8(min(255, max(0, Int((c.blueComponent * 255).rounded()))))
    return (r, g, b)
}
