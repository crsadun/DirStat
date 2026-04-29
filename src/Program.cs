using System;
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

            var form = new MainForm();
            if (args != null && args.Length > 0 && System.IO.Directory.Exists(args[0]))
            {
                form.Shown += (s, e) =>
                {
                    // Defer until the form is fully visible.
                    var mi = typeof(MainForm).GetMethod("StartScan",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (mi != null) mi.Invoke(form, new object[] { args[0] });
                };
            }
            Application.Run(form);
        }
    }
}
