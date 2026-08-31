using System;
using System.Drawing;
using System.Windows.Forms;

namespace YouTubeDownloader
{
    public class PromptForm : Form
    {
        public string Chosen;

        private PromptForm(string title, string message, string[] buttons, bool warning)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            BackColor = Theme.Back;
            ClientSize = new Size(520, 190);
            Font = Theme.Regular;

            Label lbl = new Label();
            lbl.Text = message;
            lbl.Location = new Point(14, 12);
            lbl.Size = new Size(492, 126);
            lbl.ForeColor = warning ? Theme.Red : Theme.Light;
            Controls.Add(lbl);

            int x = 520 - 14;
            for (int i = buttons.Length - 1; i >= 0; i--)
            {
                string b = buttons[i];
                Button btn = new Button();
                btn.Text = b;
                btn.Size = new Size(175, 32);
                x -= btn.Width;
                if (i < buttons.Length - 1) x -= 8;
                btn.Location = new Point(x, 146);
                Theme.StyleButton(btn);
                string chosen = b;
                btn.Click += delegate { Chosen = chosen; Close(); };
                Controls.Add(btn);
            }

            float k = ChromeApi.GetDpiForWindowAt(Cursor.Position) / 96f;
            if (k > 0.999f && k < 1.001f) k = 1f;
            if (k != 1f) Scale(new SizeF(k, k));
        }

        public static string Show(Form owner, string title, string message, bool warning, params string[] buttons)
        {
            using (PromptForm f = new PromptForm(title, message, buttons, warning))
            {
                if (owner == null || owner.IsDisposed)
                {
                    if (Application.OpenForms.Count > 0) owner = Application.OpenForms[0];
                }
                if (owner != null && !owner.IsDisposed) f.ShowDialog(owner);
                else f.ShowDialog();
                return f.Chosen;
            }
        }
    }
}
