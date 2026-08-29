using System;
using System.Windows.Forms;

namespace YouTubeDownloader
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args != null && args.Length > 0)
            {
                bool selftest = false;
                bool net = false;
                foreach (string a in args)
                {
                    if (string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)) selftest = true;
                    if (string.Equals(a, "--net", StringComparison.OrdinalIgnoreCase)) net = true;
                }
                if (selftest)
                {
                    Environment.Exit(SelfTest.Run(net));
                    return;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
