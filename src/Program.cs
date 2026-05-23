using System;
using System.IO;
using System.Windows.Forms;

namespace DirStat
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Resolve a startup path from a CLI argument if one was given. If the
            // argument is a file, fall back to its containing folder (matches the
            // dialog's behaviour).
            string startupPath = null;
            if (args != null && args.Length > 0 && !string.IsNullOrEmpty(args[0]))
            {
                string a = args[0];
                if (Directory.Exists(a)) startupPath = a;
                else if (File.Exists(a))
                {
                    try { startupPath = Path.GetDirectoryName(a); }
                    catch { startupPath = null; }
                }
            }

            var form = new MainForm();
            form.Shown += delegate(object s, EventArgs e)
            {
                if (!string.IsNullOrEmpty(startupPath))
                {
                    form.BeginScan(startupPath);
                    return;
                }
                // No CLI path → show the WinDirStat-style picker so the user
                // doesn't have to traverse menus to start a scan.
                using (var dlg = new OpenDialog())
                {
                    if (dlg.ShowDialog(form) == DialogResult.OK
                        && !string.IsNullOrEmpty(dlg.SelectedPath))
                    {
                        form.BeginScan(dlg.SelectedPath);
                    }
                }
            };
            Application.Run(form);
        }
    }
}
