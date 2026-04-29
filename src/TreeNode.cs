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
