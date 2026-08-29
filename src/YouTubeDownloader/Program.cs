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
                string clipboardExpected = null;
                string titleUrl = null;
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i];
                    if (string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)) selftest = true;
                    else if (string.Equals(a, "--net", StringComparison.OrdinalIgnoreCase)) net = true;
                    else if (string.Equals(a, "--cliptest", StringComparison.OrdinalIgnoreCase))
                    {
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) clipboardExpected = args[++i];
                    }
                    else if (string.Equals(a, "--title", StringComparison.OrdinalIgnoreCase))
                    {
                        if (i + 1 < args.Length) titleUrl = args[++i];
                    }
                }

                if (clipboardExpected != null)
                {
                    Environment.Exit(SelfTest.RunClipboard(clipboardExpected));
                }
                if (titleUrl != null)
                {
                    Environment.Exit(SelfTest.RunTitle(titleUrl));
                }
                if (selftest)
                {
                    Environment.Exit(SelfTest.Run(net));
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
