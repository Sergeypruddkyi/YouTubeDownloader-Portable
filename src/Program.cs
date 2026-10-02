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
                string editorSmokeFile = null;
                int editorSmokeStay = 20;
                string scrubBenchFile = null;
                string scrubBenchMode = "baseline";
                string playTestFile = null;
                string openTimingFile = null;
                int openTimingStay = 25;
                bool openTimingViaMainForm = false;
                string acceptFile = null;
                int acceptStay = 90;
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i];
                    if (string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)) selftest = true;
                    else if (string.Equals(a, "--net", StringComparison.OrdinalIgnoreCase)) net = true;
                    else if (string.Equals(a, "--playtest", StringComparison.OrdinalIgnoreCase))
                    {
                        if (i + 1 < args.Length) playTestFile = args[++i];
                    }
                    else if (string.Equals(a, "--cliptest", StringComparison.OrdinalIgnoreCase))
                    {
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) clipboardExpected = args[++i];
                    }
                    else if (string.Equals(a, "--title", StringComparison.OrdinalIgnoreCase))
                    {
                        if (i + 1 < args.Length) titleUrl = args[++i];
                    }
                    else if (string.Equals(a, "--editortest", StringComparison.OrdinalIgnoreCase))
                    {
                        if (i + 1 < args.Length) editorSmokeFile = args[++i];
                        // Optional seconds the window stays open after the checks, so a
                        // real-mouse pass can also exercise the trim itself.
                        int stay;
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("--")
                            && int.TryParse(args[i + 1], out stay) && stay > 0)
                        {
                            editorSmokeStay = stay;
                            i++;
                        }
                    }
                    else if (string.Equals(a, "--accept", StringComparison.OrdinalIgnoreCase))
                    {
                        // End-to-end acceptance: MainForm -> Trim/Frame -> Editor.
                        if (i + 1 < args.Length) acceptFile = args[++i];
                        int stay;
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("--")
                            && int.TryParse(args[i + 1], out stay) && stay > 0)
                        {
                            acceptStay = stay;
                            i++;
                        }
                    }
                    else if (string.Equals(a, "--vlcdiag", StringComparison.OrdinalIgnoreCase))
                    {
                        // Diagnostic only: turn the LibVLC seek-trail log on for any
                        // manual run of the editor.
                        string tag = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "manual";
                        VlcDiag.Enable(tag);
                    }
                    else if (string.Equals(a, "--scrubbench", StringComparison.OrdinalIgnoreCase))
                    {
                        if (i + 1 < args.Length) scrubBenchFile = args[++i];
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) scrubBenchMode = args[++i];
                    }
                    else if (string.Equals(a, "--opentiming", StringComparison.OrdinalIgnoreCase))
                    {
                        // Diagnostic only: measures the Editor open route.
                        if (i + 1 < args.Length) openTimingFile = args[++i];
                        if (i + 1 < args.Length && string.Equals(args[i + 1], "--via-main", StringComparison.OrdinalIgnoreCase))
                        {
                            openTimingViaMainForm = true;
                            i++;
                        }
                        int stay;
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("--")
                            && int.TryParse(args[i + 1], out stay) && stay > 0)
                        {
                            openTimingStay = stay;
                            i++;
                        }
                    }
                }

                if (scrubBenchFile != null)
                {
                    Environment.Exit(ScrubBench.Run(scrubBenchFile, scrubBenchMode));
                }

                if (openTimingFile != null)
                {
                    Environment.Exit(SelfTest.RunEditorOpenTiming(openTimingFile, openTimingStay, openTimingViaMainForm));
                }

                if (acceptFile != null)
                {
                    VlcDiag.Enable("accept " + acceptFile);
                    Environment.Exit(AcceptRun.Run(acceptFile, acceptStay));
                }

                if (playTestFile != null)
                {
                    Environment.Exit(SelfTest.RunLibVlcPlayback(playTestFile));
                }

                if (clipboardExpected != null)
                {
                    Environment.Exit(SelfTest.RunClipboard(clipboardExpected));
                }
                if (titleUrl != null)
                {
                    Environment.Exit(SelfTest.RunTitle(titleUrl));
                }
                if (editorSmokeFile != null)
                {
                    Environment.Exit(SelfTest.RunEditorSmoke(editorSmokeFile, editorSmokeStay));
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
