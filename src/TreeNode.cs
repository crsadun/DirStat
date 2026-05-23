using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace DirStat
{
    public sealed class DirNode
    {
        public string Name;
        public DirNode Parent;
        public List<DirNode> Children;
        public bool IsDirectory;
        public long Size;
        public long FileCount;
        public long DirCount;

        // Mirror counters that respect the user's minimum-file-size filter.
        // After scan and on every threshold change they're set by RecomputeDisplay;
        // when no filter is active they equal the unfiltered values above.
        public long DisplaySize;
        public long DisplayFileCount;
        public long DisplayDirCount;

        // Marked at scan completion when this node is a recognised OS system
        // path (or a descendant of one). When the "exclude system files" toggle
        // is on, RecomputeDisplay treats these subtrees as size 0.
        public bool IsSystem;

        private long _atomicSize;
        private long _atomicFileCount;
        private long _atomicDirCount;

        public DirNode(string name, DirNode parent, bool isDirectory)
        {
            Name = name;
            Parent = parent;
            IsDirectory = isDirectory;
            if (isDirectory)
                Children = new List<DirNode>();
        }

        public string FullPath
        {
            get
            {
                if (Parent == null) return Name;
                string p = Parent.FullPath;
                if (p.EndsWith("\\") || p.EndsWith("/"))
                    return p + Name;
                return p + Path.DirectorySeparatorChar + Name;
            }
        }

        public void AddSize(long bytes)
        {
            Interlocked.Add(ref _atomicSize, bytes);
        }

        public void IncrementFile()
        {
            Interlocked.Increment(ref _atomicFileCount);
        }

        public void IncrementDir()
        {
            Interlocked.Increment(ref _atomicDirCount);
        }

        public void FreezeAtomicCounts()
        {
            Size = Interlocked.Read(ref _atomicSize);
            FileCount = Interlocked.Read(ref _atomicFileCount);
            DirCount = Interlocked.Read(ref _atomicDirCount);
        }

        // Recursively bubble live counters up the tree at scan completion.
        // Children must already have FreezeAtomicCounts called.
        public void Finalize_PostScan()
        {
            if (Children != null)
            {
                foreach (var c in Children)
                    c.Finalize_PostScan();
                Children.Sort((a, b) => b.Size.CompareTo(a.Size));
            }
            FreezeAtomicCounts();
            // Display* are populated by the caller via RecomputeDisplay so that
            // file/dir counts can be recursive (unlike the immediate-only
            // FileCount/DirCount above) and react to the size filter.
        }

        // Recompute Display* across this subtree given a minimum file size in
        // bytes and whether the user has enabled "exclude system files".
        // minBytes == 0 disables the size filter; excludeSystem == false
        // disables the system-files filter. Returns this node's DisplaySize.
        public long RecomputeDisplay(long minBytes, bool excludeSystem)
        {
            if (excludeSystem && IsSystem)
            {
                DisplaySize = 0;
                DisplayFileCount = 0;
                DisplayDirCount = 0;
                return 0;
            }
            if (!IsDirectory)
            {
                DisplaySize = (Size >= minBytes) ? Size : 0;
                DisplayFileCount = (DisplaySize > 0) ? 1 : 0;
                DisplayDirCount = 0;
                return DisplaySize;
            }

            // DisplayFileCount and DisplayDirCount are recursive (total
            // surviving files / dirs in the subtree), unlike the original
            // FileCount/DirCount which only count immediate children.
            // Recursive is far more informative in tree labels.
            long sumSize = 0, sumFiles = 0, sumDirs = 0;
            if (Children != null)
            {
                foreach (var c in Children)
                {
                    long cs = c.RecomputeDisplay(minBytes, excludeSystem);
                    sumSize += cs;
                    if (c.IsDirectory)
                    {
                        if (cs > 0) sumDirs += 1 + c.DisplayDirCount;
                        sumFiles += c.DisplayFileCount;
                    }
                    else if (cs > 0)
                    {
                        sumFiles += 1;
                    }
                }
            }
            DisplaySize = sumSize;
            DisplayFileCount = sumFiles;
            DisplayDirCount = sumDirs;
            return sumSize;
        }
    }

    // Aggregates files by extension across the scanned tree.
    public sealed class ExtensionStats
    {
        private readonly Dictionary<string, ExtEntry> _map =
            new Dictionary<string, ExtEntry>(StringComparer.OrdinalIgnoreCase);

        private readonly object _gate = new object();

        public sealed class ExtEntry
        {
            public string Extension;
            public long TotalSize;
            public long FileCount;
        }

        public void Add(string extension, long size)
        {
            if (extension == null) extension = "";
            lock (_gate)
            {
                ExtEntry e;
                if (!_map.TryGetValue(extension, out e))
                {
                    e = new ExtEntry { Extension = extension };
                    _map[extension] = e;
                }
                e.TotalSize += size;
                e.FileCount += 1;
            }
        }

        public List<ExtEntry> GetSortedBySize()
        {
            lock (_gate)
            {
                var list = new List<ExtEntry>(_map.Values);
                list.Sort((a, b) => b.TotalSize.CompareTo(a.TotalSize));
                return list;
            }
        }
    }
}
