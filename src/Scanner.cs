using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DirStat
{
    public sealed class ScanProgress
    {
        public long FilesScanned;
        public long DirsScanned;
        public long BytesScanned;
        public string CurrentPath;
    }

    public sealed class ScanResult
    {
        public DirNode Root;
        public ExtensionStats Extensions;
        public TimeSpan Elapsed;
        public List<string> Errors;
    }

    public sealed class Scanner
    {
        private readonly string _rootPath;
        private readonly CancellationToken _cancel;
        private readonly Action<ScanProgress> _onProgress;
        private readonly ScanProgress _progress = new ScanProgress();
        private readonly ExtensionStats _extensions = new ExtensionStats();
        private readonly ConcurrentBag<string> _errors = new ConcurrentBag<string>();
        private long _scannedFiles;
        private long _scannedDirs;
        private long _scannedBytes;
        private DateTime _lastReport = DateTime.UtcNow;

        public Scanner(string rootPath, Action<ScanProgress> onProgress, CancellationToken cancel)
        {
            _rootPath = rootPath;
            _onProgress = onProgress;
            _cancel = cancel;
        }

        public Task<ScanResult> RunAsync()
        {
            return Task.Run(() => Run());
        }

        private ScanResult Run()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            string normalized = _rootPath;
            if (normalized.EndsWith("\\") && normalized.Length > 3)
                normalized = normalized.TrimEnd('\\');

            string displayName = normalized;

            var root = new DirNode(displayName, null, true);

            // Use a list of immediate children so we can fan out scanning.
            DirectoryInfo rootInfo;
            try
            {
                rootInfo = new DirectoryInfo(_rootPath);
            }
            catch (Exception ex)
            {
                _errors.Add(_rootPath + ": " + ex.Message);
                return BuildResult(root, sw.Elapsed);
            }

            // Enumerate immediate children, then scan each in parallel.
            var topChildren = new List<FileSystemInfo>();
            try
            {
                foreach (var child in rootInfo.EnumerateFileSystemInfos())
                {
                    if (_cancel.IsCancellationRequested) break;
                    topChildren.Add(child);
                }
            }
            catch (Exception ex)
            {
                _errors.Add(_rootPath + ": " + ex.Message);
            }

            int parallelism = Math.Max(2, Math.Min(Environment.ProcessorCount * 2, 32));
            var po = new ParallelOptions
            {
                MaxDegreeOfParallelism = parallelism,
                CancellationToken = _cancel
            };

            try
            {
                Parallel.ForEach(topChildren, po, child =>
                {
                    if (_cancel.IsCancellationRequested) return;
                    DirNode n = MakeChild(root, child);
                    if (n == null) return;
                    lock (root.Children) { root.Children.Add(n); }

                    var dirInfo = child as DirectoryInfo;
                    if (dirInfo != null)
                    {
                        ScanDirectoryRecursive(dirInfo, n);
                    }
                });
            }
            catch (OperationCanceledException) { }

            return BuildResult(root, sw.Elapsed);
        }

        private DirNode MakeChild(DirNode parent, FileSystemInfo info)
        {
            try
            {
                bool isDir = (info.Attributes & FileAttributes.Directory) != 0;
                bool isReparse = (info.Attributes & FileAttributes.ReparsePoint) != 0;
                if (isReparse) return null; // skip junctions/symlinks to avoid loops & double-counting

                if (isDir)
                {
                    var node = new DirNode(info.Name, parent, true);
                    parent.IncrementDir();
                    Interlocked.Increment(ref _scannedDirs);
                    return node;
                }
                else
                {
                    long len = ((FileInfo)info).Length;
                    var node = new DirNode(info.Name, parent, false);
                    node.AddSize(len);
                    BubbleSize(parent, len);
                    parent.IncrementFile();
                    string ext = Path.GetExtension(info.Name);
                    if (string.IsNullOrEmpty(ext)) ext = "<no extension>";
                    _extensions.Add(ext, len);
                    Interlocked.Increment(ref _scannedFiles);
                    Interlocked.Add(ref _scannedBytes, len);
                    MaybeReport(info.FullName);
                    return node;
                }
            }
            catch (Exception ex)
            {
                _errors.Add(info.FullName + ": " + ex.Message);
                return null;
            }
        }

        private void ScanDirectoryRecursive(DirectoryInfo dir, DirNode node)
        {
            if (_cancel.IsCancellationRequested) return;

            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = dir.EnumerateFileSystemInfos();
            }
            catch (UnauthorizedAccessException ex)
            {
                _errors.Add(dir.FullName + ": " + ex.Message);
                return;
            }
            catch (DirectoryNotFoundException) { return; }
            catch (PathTooLongException ex) { _errors.Add(dir.FullName + ": " + ex.Message); return; }
            catch (IOException ex) { _errors.Add(dir.FullName + ": " + ex.Message); return; }

            try
            {
                foreach (var child in entries)
                {
                    if (_cancel.IsCancellationRequested) return;
                    DirNode childNode = MakeChild(node, child);
                    if (childNode == null) continue;
                    node.Children.Add(childNode);

                    var subDir = child as DirectoryInfo;
                    if (subDir != null)
                    {
                        ScanDirectoryRecursive(subDir, childNode);
                    }
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                _errors.Add(dir.FullName + ": " + ex.Message);
            }
            catch (IOException ex)
            {
                _errors.Add(dir.FullName + ": " + ex.Message);
            }
        }

        private static void BubbleSize(DirNode start, long bytes)
        {
            DirNode cur = start;
            while (cur != null)
            {
                cur.AddSize(bytes);
                cur = cur.Parent;
            }
        }

        private void MaybeReport(string currentPath)
        {
            // Report every ~16k files OR every 100ms (whichever comes first).
            // The mod check is cheap; the time check throttles when files are huge & sparse.
            long files = Interlocked.Read(ref _scannedFiles);
            if ((files & 0x3FFF) == 0)
            {
                DateTime now = DateTime.UtcNow;
                if ((now - _lastReport).TotalMilliseconds >= 100)
                {
                    _lastReport = now;
                    if (_onProgress != null)
                    {
                        _progress.FilesScanned = files;
                        _progress.DirsScanned = Interlocked.Read(ref _scannedDirs);
                        _progress.BytesScanned = Interlocked.Read(ref _scannedBytes);
                        _progress.CurrentPath = currentPath;
                        try { _onProgress(_progress); } catch { /* UI errors must not abort scan */ }
                    }
                }
            }
        }

        private ScanResult BuildResult(DirNode root, TimeSpan elapsed)
        {
            // Bubble sub-tree counts and finalize sorted children.
            root.Finalize_PostScan();

            // Final progress report
            if (_onProgress != null)
            {
                _progress.FilesScanned = Interlocked.Read(ref _scannedFiles);
                _progress.DirsScanned = Interlocked.Read(ref _scannedDirs);
                _progress.BytesScanned = Interlocked.Read(ref _scannedBytes);
                _progress.CurrentPath = "";
                try { _onProgress(_progress); } catch { }
            }

            var errs = new List<string>(_errors);
            return new ScanResult
            {
                Root = root,
                Extensions = _extensions,
                Elapsed = elapsed,
                Errors = errs
            };
        }
    }
}
