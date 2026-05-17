import AppKit
import Foundation

final class MainWindowController: NSWindowController,
                                  NSOutlineViewDataSource, NSOutlineViewDelegate,
                                  NSTableViewDataSource, NSTableViewDelegate,
                                  TreemapViewDelegate, NSMenuDelegate {
    static let appVersion = "0.2"

    // ---- UI ----
    private var outlineView: NSOutlineView!
    private var treemapView: TreemapView!
    private var extTable: NSTableView!
    private var statusLabel: NSTextField!
    private var filesLabel: NSTextField!
    private var bytesLabel: NSTextField!
    private var progressIndicator: NSProgressIndicator!

    // ---- Model ----
    private var root: DirNode?
    private var scanner: Scanner?
    private var scanInFlight = false
    private let colorMap = ExtensionColorMap()
    private var extensionEntries: [ExtensionStats.Entry] = []
    private var totalBytes: Int64 = 0
    private var suppressOutlineSelection = false

    // Build the window in a static factory so init stays simple.
    static func makeDefault() -> MainWindowController {
        let style: NSWindow.StyleMask = [.titled, .closable, .miniaturizable, .resizable]
        let win = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 1200, height: 800),
            styleMask: style, backing: .buffered, defer: false)
        win.title = "DirStat \(MainWindowController.appVersion)"
        win.center()
        win.appearance = NSAppearance(named: .darkAqua)
        win.minSize = NSSize(width: 600, height: 400)
        let wc = MainWindowController(window: win)
        wc.setupUI()
        return wc
    }

    // ---- UI construction ----

    private func setupUI() {
        guard let window = window, let content = window.contentView else { return }
        content.wantsLayer = true
        content.layer?.backgroundColor = NSColor(white: 32.0/255.0, alpha: 1).cgColor

        // Tree (NSOutlineView in scroll view)
        let treeScroll = NSScrollView()
        treeScroll.translatesAutoresizingMaskIntoConstraints = false
        treeScroll.hasVerticalScroller = true
        treeScroll.hasHorizontalScroller = true
        treeScroll.borderType = .noBorder
        treeScroll.drawsBackground = false

        outlineView = NSOutlineView()
        outlineView.dataSource = self
        outlineView.delegate = self
        outlineView.headerView = nil
        outlineView.allowsMultipleSelection = false
        outlineView.usesAlternatingRowBackgroundColors = false
        outlineView.backgroundColor = NSColor(white: 40.0/255.0, alpha: 1)
        outlineView.rowSizeStyle = .default
        outlineView.indentationPerLevel = 14
        outlineView.autoresizesOutlineColumn = false
        let treeCol = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("name"))
        treeCol.title = ""
        treeCol.minWidth = 80
        treeCol.width = 280
        treeCol.resizingMask = [.autoresizingMask, .userResizingMask]
        outlineView.addTableColumn(treeCol)
        outlineView.outlineTableColumn = treeCol
        outlineView.menu = makeTreeContextMenuPlaceholder()
        outlineView.target = self
        outlineView.doubleAction = #selector(onTreeDoubleClick)
        treeScroll.documentView = outlineView

        // Treemap
        treemapView = TreemapView(frame: .zero)
        treemapView.translatesAutoresizingMaskIntoConstraints = false
        treemapView.colorMap = colorMap
        treemapView.delegate = self

        // Extension table
        let extScroll = NSScrollView()
        extScroll.translatesAutoresizingMaskIntoConstraints = false
        extScroll.hasVerticalScroller = true
        extScroll.borderType = .noBorder
        extScroll.drawsBackground = false
        extTable = NSTableView()
        extTable.dataSource = self
        extTable.delegate = self
        extTable.backgroundColor = NSColor(white: 40.0/255.0, alpha: 1)
        extTable.headerView = NSTableHeaderView()
        extTable.usesAlternatingRowBackgroundColors = false
        extTable.gridStyleMask = []
        extTable.allowsMultipleSelection = false
        extTable.intercellSpacing = NSSize(width: 0, height: 1)

        let cExt = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("ext")); cExt.title = "Extension"; cExt.width = 160; cExt.minWidth = 100
        let cSize = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("size")); cSize.title = "Size"; cSize.width = 110; cSize.minWidth = 80
        let cPct = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("pct")); cPct.title = "% Bytes"; cPct.width = 80; cPct.minWidth = 60
        let cFiles = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("files")); cFiles.title = "Files"; cFiles.width = 90; cFiles.minWidth = 70
        extTable.addTableColumn(cExt)
        extTable.addTableColumn(cSize)
        extTable.addTableColumn(cPct)
        extTable.addTableColumn(cFiles)
        extScroll.documentView = extTable

        // Right split: treemap (top) | extension table (bottom)
        let rightSplit = NSSplitView()
        rightSplit.translatesAutoresizingMaskIntoConstraints = false
        rightSplit.isVertical = false
        rightSplit.dividerStyle = .thin
        rightSplit.addArrangedSubview(treemapView)
        rightSplit.addArrangedSubview(extScroll)
        rightSplit.setHoldingPriority(.defaultLow, forSubviewAt: 0)
        rightSplit.setHoldingPriority(NSLayoutConstraint.Priority(260), forSubviewAt: 1)

        // Outer split: tree (left) | rightSplit
        let outerSplit = NSSplitView()
        outerSplit.translatesAutoresizingMaskIntoConstraints = false
        outerSplit.isVertical = true
        outerSplit.dividerStyle = .thin
        outerSplit.addArrangedSubview(treeScroll)
        outerSplit.addArrangedSubview(rightSplit)
        outerSplit.setHoldingPriority(NSLayoutConstraint.Priority(260), forSubviewAt: 0)
        outerSplit.setHoldingPriority(.defaultLow, forSubviewAt: 1)

        // Status bar
        let statusBar = NSView()
        statusBar.translatesAutoresizingMaskIntoConstraints = false
        statusBar.wantsLayer = true
        statusBar.layer?.backgroundColor = NSColor(white: 48.0/255.0, alpha: 1).cgColor

        statusLabel = NSTextField(labelWithString: "Ready")
        statusLabel.translatesAutoresizingMaskIntoConstraints = false
        statusLabel.textColor = .white
        statusLabel.lineBreakMode = .byTruncatingMiddle
        statusLabel.font = NSFont.systemFont(ofSize: 11)
        statusLabel.maximumNumberOfLines = 1

        progressIndicator = NSProgressIndicator()
        progressIndicator.translatesAutoresizingMaskIntoConstraints = false
        progressIndicator.style = .spinning
        progressIndicator.controlSize = .small
        progressIndicator.isDisplayedWhenStopped = false

        filesLabel = NSTextField(labelWithString: "0 files")
        filesLabel.translatesAutoresizingMaskIntoConstraints = false
        filesLabel.textColor = .white
        filesLabel.font = NSFont.systemFont(ofSize: 11)
        filesLabel.alignment = .right

        bytesLabel = NSTextField(labelWithString: "0 B")
        bytesLabel.translatesAutoresizingMaskIntoConstraints = false
        bytesLabel.textColor = .white
        bytesLabel.font = NSFont.systemFont(ofSize: 11)
        bytesLabel.alignment = .right

        statusBar.addSubview(statusLabel)
        statusBar.addSubview(progressIndicator)
        statusBar.addSubview(filesLabel)
        statusBar.addSubview(bytesLabel)

        content.addSubview(outerSplit)
        content.addSubview(statusBar)

        NSLayoutConstraint.activate([
            outerSplit.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            outerSplit.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            outerSplit.topAnchor.constraint(equalTo: content.topAnchor),
            outerSplit.bottomAnchor.constraint(equalTo: statusBar.topAnchor),

            statusBar.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            statusBar.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            statusBar.bottomAnchor.constraint(equalTo: content.bottomAnchor),
            statusBar.heightAnchor.constraint(equalToConstant: 22),

            statusLabel.leadingAnchor.constraint(equalTo: statusBar.leadingAnchor, constant: 8),
            statusLabel.centerYAnchor.constraint(equalTo: statusBar.centerYAnchor),
            statusLabel.trailingAnchor.constraint(lessThanOrEqualTo: progressIndicator.leadingAnchor, constant: -8),

            progressIndicator.trailingAnchor.constraint(equalTo: filesLabel.leadingAnchor, constant: -8),
            progressIndicator.centerYAnchor.constraint(equalTo: statusBar.centerYAnchor),
            progressIndicator.widthAnchor.constraint(equalToConstant: 16),
            progressIndicator.heightAnchor.constraint(equalToConstant: 16),

            filesLabel.trailingAnchor.constraint(equalTo: bytesLabel.leadingAnchor, constant: -12),
            filesLabel.centerYAnchor.constraint(equalTo: statusBar.centerYAnchor),
            filesLabel.widthAnchor.constraint(greaterThanOrEqualToConstant: 90),

            bytesLabel.trailingAnchor.constraint(equalTo: statusBar.trailingAnchor, constant: -8),
            bytesLabel.centerYAnchor.constraint(equalTo: statusBar.centerYAnchor),
            bytesLabel.widthAnchor.constraint(greaterThanOrEqualToConstant: 90),
        ])

        // Establish split positions after layout cycle.
        DispatchQueue.main.async {
            outerSplit.setPosition(320, ofDividerAt: 0)
            let h = rightSplit.bounds.height
            if h > 0 { rightSplit.setPosition(h * 0.72, ofDividerAt: 0) }
        }
    }

    // Placeholder menu so NSOutlineView fires `menuNeedsUpdate` and we can
    // populate based on the right-clicked row.
    private func makeTreeContextMenuPlaceholder() -> NSMenu {
        let m = NSMenu()
        m.delegate = self
        return m
    }

    // ---- Public actions (menu targets — selectors with nil target reach
    //      the window controller via the responder chain) ----

    @objc func openFolder(_ sender: Any?) {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = false
        panel.prompt = "Scan"
        panel.message = "Choose a folder to analyze"
        panel.begin { [weak self] resp in
            guard resp == .OK, let url = panel.url else { return }
            self?.startScan(path: url.path)
        }
    }

    @objc func upOneLevel(_ sender: Any?) {
        guard let r = root else { return }
        let current = r.name
        if current.isEmpty { return }
        let parent = (current as NSString).deletingLastPathComponent
        if parent.isEmpty || parent == current {
            statusLabel.stringValue = "Already at the top — \(current) has no parent."
            return
        }
        startScan(path: parent)
    }

    @objc func refresh(_ sender: Any?) {
        if let r = root { startScan(path: r.name) }
    }

    @objc func stopScan(_ sender: Any?) {
        scanner?.requestStop()
    }

    @objc func revealInFinder(_ sender: Any?) {
        guard let n = selectedNode() else { return }
        let url = URL(fileURLWithPath: n.fullPath)
        NSWorkspace.shared.activateFileViewerSelecting([url])
    }

    @objc func moveToTrashAction(_ sender: Any?) {
        guard let n = selectedNode() else { return }
        delete(node: n, permanent: false)
    }

    @objc func deletePermanentAction(_ sender: Any?) {
        guard let n = selectedNode() else { return }
        delete(node: n, permanent: true)
    }

    func openVolume(path: String) {
        startScan(path: path)
    }

    private func selectedNode() -> DirNode? {
        let row = outlineView.selectedRow
        if row >= 0 { return outlineView.item(atRow: row) as? DirNode }
        return nil
    }

    // ---- Scan lifecycle ----

    func startScan(path: String) {
        scanner?.requestStop()
        suppressOutlineSelection = true
        outlineView.reloadData()
        suppressOutlineSelection = false
        root = nil
        extensionEntries = []
        totalBytes = 0
        extTable.reloadData()
        treemapView.setRoot(nil)

        window?.title = "DirStat \(MainWindowController.appVersion) — \(path)"
        statusLabel.stringValue = "Scanning \(path)…"
        filesLabel.stringValue = "0 files"
        bytesLabel.stringValue = "0 B"
        progressIndicator.startAnimation(nil)
        scanInFlight = true

        let scanner = Scanner(rootPath: path) { [weak self] progress in
            DispatchQueue.main.async {
                self?.onScanProgress(progress)
            }
        }
        self.scanner = scanner
        scanner.runAsync { [weak self] result in
            self?.onScanDone(result, path: path)
        }
    }

    private func onScanProgress(_ p: ScanProgress) {
        filesLabel.stringValue = "\(Util.formatCount(p.filesScanned)) files"
        bytesLabel.stringValue = Util.formatBytes(p.bytesScanned)
        if !p.currentPath.isEmpty {
            let s = p.currentPath
            let truncated = s.count > 100 ? "…" + String(s.suffix(99)) : s
            statusLabel.stringValue = "Scanning: \(truncated)"
        }
    }

    private func onScanDone(_ r: ScanResult, path: String) {
        progressIndicator.stopAnimation(nil)
        scanInFlight = false

        if r.canceled {
            statusLabel.stringValue = "Scan canceled."
            return
        }

        root = r.root
        totalBytes = r.root.size

        statusLabel.stringValue = String(format: "Done. %@ files, %@ dirs, %@, %.1fs",
            Util.formatCount(r.root.fileCount),
            Util.formatCount(r.root.dirCount),
            Util.formatBytes(r.root.size),
            r.elapsed)
        filesLabel.stringValue = "\(Util.formatCount(r.root.fileCount)) files"
        bytesLabel.stringValue = Util.formatBytes(r.root.size)

        // Tree
        suppressOutlineSelection = true
        outlineView.reloadData()
        outlineView.expandItem(r.root)
        suppressOutlineSelection = false
        // Select root row
        if outlineView.numberOfRows > 0 {
            outlineView.selectRowIndexes(IndexSet(integer: 0), byExtendingSelection: false)
        }

        // Extensions + colors
        colorMap.build(from: r.extensions)
        extensionEntries = r.extensions.sortedBySize()
        extTable.reloadData()

        // Treemap
        treemapView.setRoot(r.root)
    }

    // ---- NSOutlineView data source ----

    func outlineView(_ outlineView: NSOutlineView, numberOfChildrenOfItem item: Any?) -> Int {
        if item == nil { return root != nil ? 1 : 0 }
        guard let n = item as? DirNode, let ch = n.children else { return 0 }
        return ch.count
    }

    func outlineView(_ outlineView: NSOutlineView, child index: Int, ofItem item: Any?) -> Any {
        if item == nil { return root! }
        guard let n = item as? DirNode, let ch = n.children else { fatalError() }
        return ch[index]
    }

    func outlineView(_ outlineView: NSOutlineView, isItemExpandable item: Any) -> Bool {
        guard let n = item as? DirNode else { return false }
        return n.isDirectory && (n.children?.isEmpty == false)
    }

    // ---- NSOutlineView delegate ----

    func outlineView(_ outlineView: NSOutlineView, viewFor tableColumn: NSTableColumn?, item: Any) -> NSView? {
        guard let n = item as? DirNode else { return nil }
        let id = NSUserInterfaceItemIdentifier("treeCell")
        var cell = outlineView.makeView(withIdentifier: id, owner: self) as? NSTableCellView
        if cell == nil {
            let c = NSTableCellView()
            let tf = NSTextField(labelWithString: "")
            tf.translatesAutoresizingMaskIntoConstraints = false
            tf.lineBreakMode = .byTruncatingMiddle
            tf.font = NSFont.systemFont(ofSize: 12)
            tf.textColor = .white
            tf.maximumNumberOfLines = 1
            c.addSubview(tf)
            c.textField = tf
            NSLayoutConstraint.activate([
                tf.leadingAnchor.constraint(equalTo: c.leadingAnchor, constant: 2),
                tf.trailingAnchor.constraint(equalTo: c.trailingAnchor, constant: -2),
                tf.centerYAnchor.constraint(equalTo: c.centerYAnchor)
            ])
            c.identifier = id
            cell = c
        }
        cell!.textField?.stringValue = formatTreeLabel(n)
        return cell
    }

    func outlineViewSelectionDidChange(_ notification: Notification) {
        if suppressOutlineSelection { return }
        guard let n = selectedNode() else { return }
        treemapView.setHighlight(n)
        statusLabel.stringValue = n.fullPath
    }

    @objc private func onTreeDoubleClick(_ sender: Any?) {
        guard let n = selectedNode() else { return }
        let url = URL(fileURLWithPath: n.fullPath)
        if n.isDirectory {
            NSWorkspace.shared.activateFileViewerSelecting([url])
        } else {
            NSWorkspace.shared.open(url)
        }
    }

    private func formatTreeLabel(_ n: DirNode) -> String {
        if n.isDirectory {
            return "\(n.name)  (\(Util.formatBytes(n.size)), \(Util.formatCount(n.fileCount)) files)"
        }
        return "\(n.name)  (\(Util.formatBytes(n.size)))"
    }

    // ---- NSTableView (extension list) ----

    func numberOfRows(in tableView: NSTableView) -> Int { extensionEntries.count }

    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        guard let col = tableColumn else { return nil }
        let entry = extensionEntries[row]
        let id = col.identifier
        switch id.rawValue {
        case "ext":
            return makeSwatchCell(text: entry.ext, color: colorMap.map[entry.ext] ?? ExtensionColorMap.fallback)
        case "size":
            return makeRightCell(text: Util.formatBytes(entry.totalSize))
        case "pct":
            let pct = totalBytes > 0
                ? String(format: "%.2f%%", 100.0 * Double(entry.totalSize) / Double(totalBytes))
                : ""
            return makeRightCell(text: pct)
        case "files":
            return makeRightCell(text: Util.formatCount(entry.fileCount))
        default:
            return nil
        }
    }

    func tableViewSelectionDidChange(_ notification: Notification) {
        let r = extTable.selectedRow
        if r < 0 || r >= extensionEntries.count {
            treemapView.setExtensionHighlight(nil)
            return
        }
        let ext = extensionEntries[r].ext
        treemapView.setExtensionHighlight(ext)
        statusLabel.stringValue = "Highlighting \(ext)"
    }

    private func makeRightCell(text: String) -> NSView {
        let c = NSTableCellView()
        let tf = NSTextField(labelWithString: text)
        tf.translatesAutoresizingMaskIntoConstraints = false
        tf.alignment = .right
        tf.font = NSFont.systemFont(ofSize: 11)
        tf.textColor = .white
        c.addSubview(tf)
        c.textField = tf
        NSLayoutConstraint.activate([
            tf.leadingAnchor.constraint(equalTo: c.leadingAnchor, constant: 4),
            tf.trailingAnchor.constraint(equalTo: c.trailingAnchor, constant: -6),
            tf.centerYAnchor.constraint(equalTo: c.centerYAnchor)
        ])
        return c
    }

    private func makeSwatchCell(text: String, color: NSColor) -> NSView {
        let c = NSTableCellView()
        let swatch = NSView()
        swatch.translatesAutoresizingMaskIntoConstraints = false
        swatch.wantsLayer = true
        swatch.layer?.backgroundColor = color.cgColor
        swatch.layer?.borderColor = NSColor.black.withAlphaComponent(0.4).cgColor
        swatch.layer?.borderWidth = 1
        swatch.layer?.cornerRadius = 2

        let tf = NSTextField(labelWithString: text)
        tf.translatesAutoresizingMaskIntoConstraints = false
        tf.font = NSFont.systemFont(ofSize: 11)
        tf.textColor = .white
        tf.lineBreakMode = .byTruncatingTail

        c.addSubview(swatch)
        c.addSubview(tf)
        c.textField = tf
        NSLayoutConstraint.activate([
            swatch.leadingAnchor.constraint(equalTo: c.leadingAnchor, constant: 6),
            swatch.centerYAnchor.constraint(equalTo: c.centerYAnchor),
            swatch.widthAnchor.constraint(equalToConstant: 14),
            swatch.heightAnchor.constraint(equalToConstant: 12),
            tf.leadingAnchor.constraint(equalTo: swatch.trailingAnchor, constant: 6),
            tf.trailingAnchor.constraint(equalTo: c.trailingAnchor, constant: -4),
            tf.centerYAnchor.constraint(equalTo: c.centerYAnchor)
        ])
        return c
    }

    // ---- TreemapViewDelegate ----

    func treemapView(_ view: TreemapView, didSelect node: DirNode) {
        selectNodeInTree(node)
        statusLabel.stringValue = node.fullPath
    }

    func treemapView(_ view: TreemapView, didActivate node: DirNode) {
        let url = URL(fileURLWithPath: node.fullPath)
        if node.isDirectory {
            NSWorkspace.shared.activateFileViewerSelecting([url])
        } else {
            NSWorkspace.shared.open(url)
        }
    }

    func treemapView(_ view: TreemapView, didRequestContextFor node: DirNode, at viewPoint: NSPoint) {
        let menu = buildContextMenu(for: node)
        menu.popUp(positioning: nil, at: viewPoint, in: view)
    }

    private func selectNodeInTree(_ node: DirNode) {
        var chain: [DirNode] = []
        var c: DirNode? = node
        while let cur = c { chain.append(cur); c = cur.parent }
        chain.reverse()
        for i in 0..<max(0, chain.count - 1) {
            outlineView.expandItem(chain[i])
        }
        let row = outlineView.row(forItem: node)
        if row >= 0 {
            suppressOutlineSelection = true
            outlineView.selectRowIndexes(IndexSet(integer: row), byExtendingSelection: false)
            outlineView.scrollRowToVisible(row)
            suppressOutlineSelection = false
        }
    }

    // ---- Tree-context menu (via NSMenuDelegate on outlineView.menu) ----

    func menuNeedsUpdate(_ menu: NSMenu) {
        // Only the outline view's context menu uses this path.
        menu.removeAllItems()
        let row = outlineView.clickedRow
        guard row >= 0, let n = outlineView.item(atRow: row) as? DirNode else {
            let it = NSMenuItem(title: "(no selection)", action: nil, keyEquivalent: "")
            it.isEnabled = false
            menu.addItem(it)
            return
        }
        // Select the right-clicked row so subsequent actions act on it.
        outlineView.selectRowIndexes(IndexSet(integer: row), byExtendingSelection: false)
        let built = buildContextMenu(for: n)
        for item in built.items {
            // NSMenu items can only belong to one menu; copy by reassignment.
            built.removeItem(item)
            menu.addItem(item)
        }
    }

    private func buildContextMenu(for node: DirNode) -> NSMenu {
        let menu = NSMenu()
        let path = node.fullPath
        let parentDir = (path as NSString).deletingLastPathComponent

        // Refresh
        if node.isDirectory {
            menu.addItem(makeAction("Refresh") { [weak self] in self?.startScan(path: path) })
        } else {
            menu.addItem(makeAction("Refresh") { [weak self] in self?.refreshFileNode(node) })
        }
        let refreshParent = makeAction("Refresh Parent Directory") { [weak self] in
            if !parentDir.isEmpty { self?.startScan(path: parentDir) }
        }
        refreshParent.isEnabled = !parentDir.isEmpty && node.parent != nil
        menu.addItem(refreshParent)
        menu.addItem(.separator())

        if node.isDirectory {
            menu.addItem(makeAction("Open in Finder") { NSWorkspace.shared.open(URL(fileURLWithPath: path)) })
            menu.addItem(makeAction("Open in Terminal") { [weak self] in self?.openInTerminal(path) })
            if iTermAvailable() {
                menu.addItem(makeAction("Open in iTerm") { [weak self] in self?.openIniTerm(path) })
            }
            menu.addItem(.separator())
            menu.addItem(makeAction("Reveal in Finder") {
                NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: path)])
            })
        } else {
            menu.addItem(makeAction("Open") { NSWorkspace.shared.open(URL(fileURLWithPath: path)) })
            menu.addItem(makeAction("Reveal in Finder") {
                NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: path)])
            })
            menu.addItem(.separator())
            if !parentDir.isEmpty {
                menu.addItem(makeAction("Open Containing Folder") {
                    NSWorkspace.shared.open(URL(fileURLWithPath: parentDir))
                })
                menu.addItem(makeAction("Open Containing Folder in Terminal") { [weak self] in
                    self?.openInTerminal(parentDir)
                })
                if iTermAvailable() {
                    menu.addItem(makeAction("Open Containing Folder in iTerm") { [weak self] in
                        self?.openIniTerm(parentDir)
                    })
                }
            }
        }
        menu.addItem(.separator())
        let trash = makeAction("Move to Trash") { [weak self] in self?.delete(node: node, permanent: false) }
        trash.keyEquivalent = "\u{8}"
        trash.keyEquivalentModifierMask = [.command]
        menu.addItem(trash)
        let perma = makeAction("Delete Permanently") { [weak self] in self?.delete(node: node, permanent: true) }
        perma.keyEquivalent = "\u{8}"
        perma.keyEquivalentModifierMask = [.command, .shift]
        menu.addItem(perma)
        menu.addItem(.separator())
        menu.addItem(makeAction("Copy Path") {
            let pb = NSPasteboard.general
            pb.clearContents()
            pb.setString(path, forType: .string)
        })
        return menu
    }

    private func makeAction(_ title: String, _ handler: @escaping () -> Void) -> NSMenuItem {
        let action = ClosureMenuAction(handler: handler)
        let item = NSMenuItem(title: title,
                              action: #selector(ClosureMenuAction.fire(_:)),
                              keyEquivalent: "")
        item.target = action
        item.representedObject = action  // keep it alive
        return item
    }

    private func iTermAvailable() -> Bool {
        return FileManager.default.fileExists(atPath: "/Applications/iTerm.app")
    }

    private func openInTerminal(_ dir: String) {
        runOpen(args: ["-a", "Terminal", dir])
    }

    private func openIniTerm(_ dir: String) {
        runOpen(args: ["-a", "iTerm", dir])
    }

    private func runOpen(args: [String]) {
        let task = Process()
        task.launchPath = "/usr/bin/open"
        task.arguments = args
        do { try task.run() } catch { NSLog("open failed: \(error)") }
    }

    // ---- Delete ----

    private func delete(node: DirNode, permanent: Bool) {
        let path = node.fullPath
        if path.isEmpty { return }
        let what = node.isDirectory ? "folder and all its contents" : "file"
        let alert = NSAlert()
        alert.alertStyle = permanent ? .warning : .informational
        if permanent {
            alert.messageText = "Delete permanently?"
            alert.informativeText = "Permanently delete this \(what)?\n\n\(path)\n\nThis cannot be undone."
        } else {
            alert.messageText = "Move to Trash?"
            alert.informativeText = "Move this \(what) to the Trash?\n\n\(path)"
        }
        alert.addButton(withTitle: permanent ? "Delete" : "Move to Trash")
        alert.addButton(withTitle: "Cancel")
        // Default to Cancel.
        alert.buttons[0].keyEquivalent = ""
        alert.buttons[1].keyEquivalent = "\r"
        let result = alert.runModal()
        if result != .alertFirstButtonReturn { return }

        // Stop any in-flight scan so we don't fight it for handles.
        scanner?.requestStop()

        let url = URL(fileURLWithPath: path)
        if permanent {
            do {
                try FileManager.default.removeItem(at: url)
            } catch {
                showWarning("Could not delete:\n\(path)\n\n\(error.localizedDescription)")
                return
            }
            postDeleteRefresh(deleted: node, permanent: true)
        } else {
            NSWorkspace.shared.recycle([url]) { [weak self] _, error in
                DispatchQueue.main.async {
                    if let err = error {
                        self?.showWarning("Could not move to Trash:\n\(path)\n\n\(err.localizedDescription)")
                        return
                    }
                    self?.postDeleteRefresh(deleted: node, permanent: false)
                }
            }
        }
    }

    private func postDeleteRefresh(deleted node: DirNode, permanent: Bool) {
        statusLabel.stringValue = (permanent ? "Permanently deleted: " : "Trashed: ") + node.fullPath
        if let r = root, node !== r {
            // Refresh the current scan root so tree & treemap reflect the deletion.
            startScan(path: r.name)
        } else {
            // Deleted node was itself the scan root.
            root = nil
            extensionEntries = []
            outlineView.reloadData()
            extTable.reloadData()
            treemapView.setRoot(nil)
            window?.title = "DirStat \(MainWindowController.appVersion)"
        }
    }

    private func showWarning(_ msg: String) {
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = "DirStat"
        alert.informativeText = msg
        alert.runModal()
    }

    // ---- Refresh single file (parity with Windows version) ----

    private func refreshFileNode(_ fileNode: DirNode) {
        if fileNode.isDirectory { return }
        let path = fileNode.fullPath
        let url = URL(fileURLWithPath: path)
        let fm = FileManager.default
        if !fm.fileExists(atPath: path) {
            if let p = fileNode.parent { startScan(path: p.fullPath) }
            return
        }
        let newSize: Int64
        do {
            let attrs = try fm.attributesOfItem(atPath: path)
            newSize = (attrs[.size] as? NSNumber)?.int64Value ?? 0
        } catch {
            showWarning("Could not refresh:\n\(path)\n\n\(error.localizedDescription)")
            return
        }
        _ = url  // silence unused warning when refactoring
        let delta = newSize - fileNode.size
        fileNode.size = newSize
        var p: DirNode? = fileNode.parent
        while let cur = p {
            cur.size += delta
            p = cur.parent
        }
        outlineView.reloadData()
        treemapView.setRoot(root)
        let sign = delta == 0 ? "no change" : (delta > 0 ? "+" : "-") + Util.formatBytes(abs(delta))
        statusLabel.stringValue = "Refreshed \(path) (\(sign))"
    }
}

// Tiny adapter: lets us hang a closure off an NSMenuItem.
final class ClosureMenuAction: NSObject {
    let handler: () -> Void
    init(handler: @escaping () -> Void) { self.handler = handler }
    @objc func fire(_ sender: Any?) { handler() }
}
