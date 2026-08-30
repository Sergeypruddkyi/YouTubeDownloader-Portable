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

        public string Language
        {
            get { return Get("Language"); }
            set { Set("Language", value); }
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

        public void Load()
        {
            _values.Clear();
            try
            {
                if (!File.Exists(_path)) return;
                string[] lines = File.ReadAllLines(_path, Encoding.UTF8);
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#") || line.StartsWith("[")) continue;
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    _values[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
            }
            catch
            {
            }
        }

        public void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("[General]");
                foreach (KeyValuePair<string, string> kv in _values)
                    sb.AppendLine(kv.Key + "=" + kv.Value);
                File.WriteAllText(_path, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
            }
        }
    }
}
