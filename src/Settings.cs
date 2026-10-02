using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace YouTubeDownloader
{
    public class Settings
    {
        private readonly string _path;
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public Settings(string path)
        {
            _path = path;
        }

        public string LastFolder
        {
            get { return Get("LastFolder"); }
            set { Set("LastFolder", value); }
        }

        public bool HasChosenFolder
        {
            get { return string.Equals(Get("FolderChosen"), "true", StringComparison.OrdinalIgnoreCase); }
            set { Set("FolderChosen", value ? "true" : null); }
        }

        // The Cutter (editor) remembers the last folder of each of its own
        // dialogs SEPARATELY: Open Video, Open Image and Save frame (PNG). One
        // dialog therefore never moves another one's starting folder. Save
        // video (trim) has no folder dialog of its own - the result always
        // lands next to the source - so it needs no key here.
        public string OpenFolder
        {
            get { return Get("OpenFolder"); }
            set { Set("OpenFolder", value); }
        }

        public string FrameFolder
        {
            get { return Get("FrameFolder"); }
            set { Set("FrameFolder", value); }
        }

        // Open Image is its own action with its own folder. Deliberately NOT
        // FrameFolder (that one is where PNG frames are written) and NOT
        // OpenFolder (that one is video), so none of the three can move another
        // one's dialog.
        public string ImageFolder
        {
            get { return Get("ImageFolder"); }
            set { Set("ImageFolder", value); }
        }

        public string Language
        {
            get { return Get("Language"); }
            set { Set("Language", value); }
        }

        public DownloadQuality Quality
        {
            get { return YtDlpRunner.ParseQuality(Get("Quality")); }
            set { Set("Quality", YtDlpRunner.QualityToSetting(value)); }
        }

        public string Get(string key)
        {
            string v;
            return _values.TryGetValue(key, out v) ? v : null;
        }

        public void Set(string key, string value)
        {
            if (string.IsNullOrEmpty(value)) _values.Remove(key);
            else _values[key] = value;
        }

        private static bool _loadFailedWarned;
        private static bool _saveFailedWarned;

        private static void WarnOnce(ref bool warned, string action, Exception ex)
        {
            if (warned) return;
            warned = true;
            try
            {
                System.Windows.Forms.MessageBox.Show("Failed to " + action + " settings:\n" + ex.Message, "YouTube Downloader", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
            }
            catch { }
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;
                string[] lines = File.ReadAllLines(_path, Encoding.UTF8);
                _values.Clear();
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#") || line.StartsWith("[")) continue;
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    _values[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
            }
            catch (Exception ex)
            {
                WarnOnce(ref _loadFailedWarned, "load", ex);
            }
        }

        public void Save()
        {
            string tmp = _path + ".tmp";
            string bak = _path + ".bak";
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("[General]");
                foreach (KeyValuePair<string, string> kv in _values)
                    sb.AppendLine(kv.Key + "=" + kv.Value);

                string content = sb.ToString();
                try
                {
                    File.WriteAllText(tmp, content, Encoding.UTF8);
                    if (File.Exists(_path))
                    {
                        try
                        {
                            File.Replace(tmp, _path, bak, true);
                        }
                        catch (IOException)
                        {
                            try { if (File.Exists(bak)) File.Delete(bak); }
                            catch { }
                            File.WriteAllText(_path, content, Encoding.UTF8);
                        }
                    }
                    else
                    {
                        File.Move(tmp, _path);
                    }
                }
                finally
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); }
                    catch { }
                    try { if (File.Exists(bak)) File.Delete(bak); }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                WarnOnce(ref _saveFailedWarned, "save", ex);
            }
        }
    }
}
