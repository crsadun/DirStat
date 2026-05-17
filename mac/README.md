# DirStat for macOS

A native macOS port of DirStat — same WinDirStat-inspired treemap visualization,
written in Swift + AppKit. Builds to a self-contained `.app` you can
double-click from Finder, with no virtual machine, runtime, or Xcode project.

The Swift sources live alongside the Windows C# sources in this repo; the two
ports share nothing at the code level but mirror each other feature-for-feature.

## Build

```
./build.sh                # builds for the host CPU
./build.sh universal      # builds a fat arm64 + x86_64 binary
```

Output: `build/DirStat.app` — drag it to `/Applications` or run in place.

Requirements: the **Xcode Command Line Tools** (a few hundred MB, ships with
macOS by default on most modern installs; otherwise `xcode-select --install`).
No Xcode, no SwiftPM, no Homebrew dependencies.

## Run

```
open build/DirStat.app
build/DirStat.app/Contents/MacOS/DirStat "/path/to/scan"   # auto-scan on launch
```

The first time you launch from Finder, macOS may warn that the app is from an
unidentified developer (it isn't signed). Right-click the `.app` → **Open** to
get past Gatekeeper once; subsequent launches work normally. The CLI form
(`open …` or running the inner binary directly) bypasses this check.

## Features

Mirrors the Windows version:

- **File** menu: Open Folder (⌘O), Open Volume (lists every mounted volume
  with free / total space), Up One Level (⌘↑), Refresh (⌘R), Stop (⌘.),
  Move to Trash (⌘⌫), Delete Permanently (⌘⇧⌫)
- **View** menu: Reveal in Finder (⌘E)
- Tree view (left) ↔ treemap (right) ↔ extension legend (bottom-right),
  all bidirectionally synced
- Right-click context menu on tree rows and treemap cells:
  - Folders: Open in Finder, Open in Terminal (and iTerm if installed),
    Reveal in Finder, Refresh, Trash / Permanent delete, Copy Path
  - Files: Open (with default app), Reveal in Finder, Open Containing Folder,
    open it in Terminal / iTerm, Refresh, Trash / Permanent delete, Copy Path
- Squarified treemap with per-pixel cushion shading, computed directly into a
  CGContext bitmap at full backing-store resolution (sharp on Retina)
- Parallel scan that fans out across the root's immediate children via
  `DispatchQueue.concurrentPerform`; symbolic links are skipped to avoid loops
- Lazy outline view — `NSOutlineView` only asks for child rows as the user
  expands a directory, so the UI stays responsive on huge trees

## macOS-specific differences from the Windows version

- Drives → **Volumes**: lists everything under `/Volumes` plus the boot volume,
  picked up live via `FileManager.mountedVolumeURLs`
- "Show in Explorer" → **Reveal in Finder**
- "Open in cmd / PowerShell" → **Open in Terminal** (and **iTerm**, when it
  is present at `/Applications/iTerm.app`)
- "Delete to Recycle Bin" → **Move to Trash** (uses `NSWorkspace.recycle`,
  so items appear in `~/.Trash` and can be put back)
- Symlinks are skipped during scan (equivalent to Windows reparse points)
- Sizes are logical file lengths (parity with Windows `FileInfo.Length`), not
  on-disk allocated sizes — so compressed APFS clones report their nominal size

## Source layout

```
mac/
  Sources/
    main.swift                  — NSApplication bootstrap
    AppDelegate.swift           — menu bar, volume submenu refresh
    MainWindowController.swift  — window, splits, outline + table, context menus, scan lifecycle
    TreemapView.swift           — NSView wrapping the rendered treemap CGImage; hit-testing
    TreemapRenderer.swift       — squarified layout + per-pixel cushion shading into BGRA buffer
    Scanner.swift               — parallel filesystem walk, throttled progress reporting
    DirNode.swift               — tree model
    ExtensionStats.swift        — per-extension aggregation + palette assignment
    Util.swift                  — formatBytes, AtomicInt64/Bool, extension helper
  Resources/
    Info.plist                  — bundle metadata
  build.sh
```

## Things to consider next

- App is not signed or notarized — distributing as a `.dmg` would benefit from
  an ad-hoc codesign at minimum
- No `.icns` icon yet
- Treemap is recomputed on every window resize; could debounce harder
- No persistence: scans aren't cached between launches
