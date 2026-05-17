import AppKit

final class AppDelegate: NSObject, NSApplicationDelegate {
    private var windowController: MainWindowController?
    private var volumesMenuDelegate: VolumesMenuDelegate?

    func applicationDidFinishLaunching(_ notification: Notification) {
        buildMenu()
        let wc = MainWindowController.makeDefault()
        wc.showWindow(nil)
        wc.window?.makeKeyAndOrderFront(nil)
        windowController = wc
        volumesMenuDelegate?.onOpen = { [weak wc] path in wc?.openVolume(path: path) }

        // If launched with a path argument, scan it.
        let args = CommandLine.arguments
        if args.count > 1 {
            let path = args[1]
            var isDir: ObjCBool = false
            if FileManager.default.fileExists(atPath: path, isDirectory: &isDir), isDir.boolValue {
                wc.startScan(path: path)
            }
        }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }

    @objc func showAbout(_ sender: Any?) {
        let alert = NSAlert()
        alert.alertStyle = .informational
        alert.messageText = "DirStat \(MainWindowController.appVersion)"
        alert.informativeText = """
            A tool to visualize disk usage, inspired by WinDirStat.

            Copyright (C) 2026 Cristiano Sadun
            """
        alert.runModal()
    }

    private func buildMenu() {
        let mainMenu = NSMenu()
        let appName = "DirStat"

        // ---- App menu ----
        let appMenu = NSMenu()
        let aboutItem = NSMenuItem(title: "About \(appName)", action: #selector(showAbout(_:)), keyEquivalent: "")
        aboutItem.target = self
        appMenu.addItem(aboutItem)
        appMenu.addItem(.separator())
        appMenu.addItem(NSMenuItem(title: "Hide \(appName)",
                                   action: #selector(NSApplication.hide(_:)), keyEquivalent: "h"))
        let hideOthers = NSMenuItem(title: "Hide Others",
                                    action: #selector(NSApplication.hideOtherApplications(_:)),
                                    keyEquivalent: "h")
        hideOthers.keyEquivalentModifierMask = [.command, .option]
        appMenu.addItem(hideOthers)
        appMenu.addItem(NSMenuItem(title: "Show All",
                                   action: #selector(NSApplication.unhideAllApplications(_:)),
                                   keyEquivalent: ""))
        appMenu.addItem(.separator())
        appMenu.addItem(NSMenuItem(title: "Quit \(appName)",
                                   action: #selector(NSApplication.terminate(_:)),
                                   keyEquivalent: "q"))
        let appHolder = NSMenuItem()
        appHolder.submenu = appMenu
        mainMenu.addItem(appHolder)

        // ---- File menu ----
        let fileMenu = NSMenu(title: "File")
        fileMenu.addItem(item(title: "Open Folder…",
                              selector: #selector(MainWindowController.openFolder(_:)),
                              key: "o", mods: [.command]))
        let openVolume = NSMenuItem(title: "Open Volume", action: nil, keyEquivalent: "")
        let volumesSubmenu = NSMenu(title: "Open Volume")
        let vd = VolumesMenuDelegate()
        volumesSubmenu.delegate = vd
        volumesMenuDelegate = vd
        openVolume.submenu = volumesSubmenu
        fileMenu.addItem(openVolume)
        fileMenu.addItem(.separator())
        let upKey = String(UnicodeScalar(NSUpArrowFunctionKey)!)
        fileMenu.addItem(item(title: "Up One Level",
                              selector: #selector(MainWindowController.upOneLevel(_:)),
                              key: upKey, mods: [.command]))
        fileMenu.addItem(item(title: "Refresh",
                              selector: #selector(MainWindowController.refresh(_:)),
                              key: "r", mods: [.command]))
        fileMenu.addItem(item(title: "Stop",
                              selector: #selector(MainWindowController.stopScan(_:)),
                              key: ".", mods: [.command]))
        fileMenu.addItem(.separator())
        let trashItem = item(title: "Move to Trash",
                             selector: #selector(MainWindowController.moveToTrashAction(_:)),
                             key: "\u{8}", mods: [.command])
        fileMenu.addItem(trashItem)
        let permaItem = item(title: "Delete Permanently",
                             selector: #selector(MainWindowController.deletePermanentAction(_:)),
                             key: "\u{8}", mods: [.command, .shift])
        fileMenu.addItem(permaItem)
        let fileHolder = NSMenuItem(); fileHolder.title = "File"; fileHolder.submenu = fileMenu
        mainMenu.addItem(fileHolder)

        // ---- View menu ----
        let viewMenu = NSMenu(title: "View")
        viewMenu.addItem(item(title: "Reveal in Finder",
                              selector: #selector(MainWindowController.revealInFinder(_:)),
                              key: "e", mods: [.command]))
        let viewHolder = NSMenuItem(); viewHolder.title = "View"; viewHolder.submenu = viewMenu
        mainMenu.addItem(viewHolder)

        // ---- Window menu (standard items) ----
        let winMenu = NSMenu(title: "Window")
        winMenu.addItem(NSMenuItem(title: "Minimize",
                                   action: #selector(NSWindow.performMiniaturize(_:)),
                                   keyEquivalent: "m"))
        winMenu.addItem(NSMenuItem(title: "Zoom",
                                   action: #selector(NSWindow.performZoom(_:)),
                                   keyEquivalent: ""))
        let winHolder = NSMenuItem(); winHolder.title = "Window"; winHolder.submenu = winMenu
        mainMenu.addItem(winHolder)
        NSApp.windowsMenu = winMenu

        // ---- Help menu ----
        let helpMenu = NSMenu(title: "Help")
        let helpAbout = NSMenuItem(title: "About \(appName)",
                                   action: #selector(showAbout(_:)), keyEquivalent: "")
        helpAbout.target = self
        helpMenu.addItem(helpAbout)
        let helpHolder = NSMenuItem(); helpHolder.title = "Help"; helpHolder.submenu = helpMenu
        mainMenu.addItem(helpHolder)

        NSApp.mainMenu = mainMenu
    }

    private func item(title: String, selector: Selector,
                      key: String, mods: NSEvent.ModifierFlags) -> NSMenuItem {
        let it = NSMenuItem(title: title, action: selector, keyEquivalent: key)
        it.keyEquivalentModifierMask = mods
        // target == nil → routed via responder chain to MainWindowController
        return it
    }
}

/// Owns the "Open Volume" submenu and refreshes its contents whenever it
/// opens — so newly-mounted USB / network volumes show up.
final class VolumesMenuDelegate: NSObject, NSMenuDelegate {
    var onOpen: ((String) -> Void)?

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        let fm = FileManager.default
        let keys: [URLResourceKey] = [.volumeNameKey, .volumeTotalCapacityKey, .volumeAvailableCapacityKey, .volumeIsRemovableKey]
        guard let volumes = fm.mountedVolumeURLs(includingResourceValuesForKeys: keys, options: []) else {
            addDisabled(menu, "(no volumes detected)")
            return
        }
        if volumes.isEmpty {
            addDisabled(menu, "(no volumes detected)")
            return
        }
        for v in volumes {
            let label: String
            do {
                let r = try v.resourceValues(forKeys: Set(keys))
                let name = r.volumeName ?? v.lastPathComponent
                let avail = r.volumeAvailableCapacity ?? 0
                let total = r.volumeTotalCapacity ?? 0
                if total > 0 {
                    label = "\(name)  —  \(Util.formatBytes(Int64(avail))) free of \(Util.formatBytes(Int64(total)))"
                } else {
                    label = name
                }
            } catch {
                label = v.path
            }
            let item = NSMenuItem(title: label, action: #selector(openVolume(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = v.path
            item.toolTip = v.path
            menu.addItem(item)
        }
    }

    @objc private func openVolume(_ sender: NSMenuItem) {
        if let path = sender.representedObject as? String {
            onOpen?(path)
        }
    }

    private func addDisabled(_ menu: NSMenu, _ title: String) {
        let i = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        i.isEnabled = false
        menu.addItem(i)
    }
}
