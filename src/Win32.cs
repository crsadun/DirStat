using System;
using System.Runtime.InteropServices;

namespace DirStat
{
    internal static class Win32
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr[] apidl, uint dwFlags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr ILCreateFromPathW(string pszPath);

        [DllImport("shell32.dll")]
        private static extern void ILFree(IntPtr pidl);

        public static void OpenInExplorer(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            IntPtr pidl = ILCreateFromPathW(path);
            if (pidl == IntPtr.Zero) return;
            try
            {
                SHOpenFolderAndSelectItems(pidl, 0, null, 0);
            }
            finally
            {
                ILFree(pidl);
            }
        }

        // ----- SHFileOperation: delete to recycle bin or permanently -----

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        private const uint FO_DELETE = 0x0003;
        private const ushort FOF_ALLOWUNDO = 0x0040;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_WANTNUKEWARNING = 0x4000;

        // Returns true on success. The caller has already confirmed; we still pass
        // FOF_WANTNUKEWARNING for recycle so the shell warns when the item is too
        // large for the bin and would otherwise be silently nuked.
        public static bool Delete(IntPtr ownerHwnd, string path, bool toRecycleBin)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var op = new SHFILEOPSTRUCT
            {
                hwnd = ownerHwnd,
                wFunc = FO_DELETE,
                // SHFILEOPSTRUCT requires a double-null-terminated source list; the
                // marshaller appends one \0, so we add another explicitly.
                pFrom = path + "\0",
                pTo = null,
                fFlags = (ushort)(FOF_NOCONFIRMATION
                    | (toRecycleBin ? (FOF_ALLOWUNDO | FOF_WANTNUKEWARNING) : 0))
            };
            int rc = SHFileOperation(ref op);
            return rc == 0 && !op.fAnyOperationsAborted;
        }
    }
}
