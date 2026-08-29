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
            ClientSize = new Size(470, 150);
            Font = new Font("Segoe UI", 9F);

            Label lbl = new Label();
            lbl.Text = message;
            lbl.Location = new Point(14, 12);
            lbl.Size = new Size(442, 84);
            Controls.Add(lbl);

            int x = 470 - 14;
            for (int i = buttons.Length - 1; i >= 0; i--)
            {
                string b = buttons[i];
                Button btn = new Button();
                btn.Text = b;
                btn.Size = new Size(160, 28);
                x -= btn.Width;
                if (i < buttons.Length - 1) x -= 8;
                btn.Location = new Point(x, 108);
                string chosen = b;
                btn.Click += delegate { Chosen = chosen; Close(); };
                Controls.Add(btn);
            }

            if (warning) lbl.ForeColor = Color.FromArgb(150, 40, 40);
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
