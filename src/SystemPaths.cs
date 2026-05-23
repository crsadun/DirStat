using System;

namespace DirStat
{
    // Windows-specific list of paths that are typically "system" — owned by the
    // OS or its installer, locked against ordinary deletion. Excluding them from
    // a treemap focuses attention on files the user can actually act on.
    //
    // When DirStat is ported to Linux / macOS, each port ships its own
    // equivalent. There is no runtime OS detection here — this file is compiled
    // only into the Windows binary.
    public static class SystemPaths
    {
        // Case-insensitive prefix match. A node is "system" if its absolute path
        // equals one of these entries or starts with one followed by a separator.
        // Notable omissions: C:\Program Files\ and C:\ProgramData\ — those
        // contain plenty of user-installed software the user may still want to
        // see, so they stay visible. C:\Program Files\WindowsApps\ is kept
        // because it's locked by TrustedInstaller anyway.
        private static readonly string[] WindowsPaths = new string[]
        {
            @"C:\Windows",
            @"C:\pagefile.sys",
            @"C:\hiberfil.sys",
            @"C:\swapfile.sys",
            @"C:\System Volume Information",
            @"C:\Recovery",
            @"C:\$Recycle.Bin",
            @"C:\Program Files\WindowsApps",
            @"C:\ProgramData\Microsoft\Windows",
        };

        public static bool IsSystem(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string norm = path.TrimEnd('\\', '/');
            for (int i = 0; i < WindowsPaths.Length; i++)
            {
                string p = WindowsPaths[i].TrimEnd('\\', '/');
                if (string.Equals(norm, p, StringComparison.OrdinalIgnoreCase)) return true;
                if (norm.Length > p.Length
                    && norm.StartsWith(p, StringComparison.OrdinalIgnoreCase)
                    && (norm[p.Length] == '\\' || norm[p.Length] == '/'))
                    return true;
            }
            return false;
        }
    }
}
