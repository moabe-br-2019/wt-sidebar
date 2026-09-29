// Instalador grafico do WT Sidebar para quem nao quer usar o PowerShell.
// Leva o install.ps1 embutido e roda ele: baixa a ultima release, compila, instala e abre o app.
// Nao precisa ser recompilado a cada versao, so quando o install.ps1 mudar. Compila com build-setup.ps1.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace WtSidebarSetup
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            SetProcessDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    }

    class SetupForm : Form
    {
        static readonly Color Back = Color.FromArgb(0x1f, 0x1f, 0x1f);
        static readonly Color Field = Color.FromArgb(0x2b, 0x2b, 0x2b);
        static readonly Color Line = Color.FromArgb(0x45, 0x45, 0x45);
        static readonly Color Fg = Color.FromArgb(0xE6, 0xE6, 0xE6);
        static readonly Color FgDim = Color.FromArgb(0x9a, 0x9a, 0x9a);

        static readonly bool Pt = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pt";
        static string T(string pt, string en) { return Pt ? pt : en; }

        readonly float scale;
        readonly TextBox log = new TextBox();
        readonly CheckBox startup = new CheckBox();
        readonly Button install = new Button();
        readonly Button close = new Button();
        readonly Label status = new Label();
        Process process;

        int S(int v) { return (int)Math.Round(v * scale); }

        public SetupForm()
        {
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            Text = T("Instalar WT Sidebar", "Install WT Sidebar");
            using (var s = typeof(SetupForm).Assembly.GetManifestResourceStream("WtSidebar.ico"))
                if (s != null) Icon = new Icon(s);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Back;
            ForeColor = Fg;
            Font = new Font("Segoe UI", 12f * scale, GraphicsUnit.Pixel);
            ClientSize = new Size(S(560), S(400));

            int x = S(20), w = ClientSize.Width - S(40);
            var title = new Label
            {
                Text = "WT Sidebar",
                Font = new Font("Segoe UI Semibold", 20f * scale, GraphicsUnit.Pixel),
                AutoSize = true,
                Location = new Point(x, S(16))
            };
            var about = new Label
            {
                Text = T("Abas verticais para o Windows Terminal. O instalador baixa a versão mais recente do GitHub, " +
                         "compila no seu computador (sem instalar mais nada) e abre o app.",
                         "Vertical tabs for Windows Terminal. The installer downloads the latest version from GitHub, " +
                         "builds it on your computer (nothing else to install) and starts the app."),
                ForeColor = FgDim,
                Location = new Point(x, S(52)),
                Size = new Size(w, S(40))
            };
            startup.Text = T("Iniciar com o Windows", "Start with Windows");
            startup.Checked = true;
            startup.AutoSize = true;
            startup.Location = new Point(x, S(100));

            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Vertical;
            log.BackColor = Field;
            log.ForeColor = Fg;
            log.BorderStyle = BorderStyle.FixedSingle;
            log.Font = new Font("Consolas", 11f * scale, GraphicsUnit.Pixel);
            log.SetBounds(x, S(134), w, S(196));

            status.AutoSize = false;
            status.SetBounds(x, S(348), w - S(250), S(36));
            status.ForeColor = FgDim;

            StyleButton(close, T("Fechar", "Close"), ClientSize.Width - S(20) - S(110));
            StyleButton(install, T("Instalar", "Install"), close.Left - S(8) - S(110));
            close.Click += delegate { Close(); };
            install.Click += delegate { Start(); };
            AcceptButton = install;

            Controls.AddRange(new Control[] { title, about, startup, log, status, install, close });
        }

        void StyleButton(Button b, string text, int left)
        {
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Field;
            b.ForeColor = Fg;
            b.FlatAppearance.BorderColor = Line;
            b.SetBounds(left, ClientSize.Height - S(50), S(110), S(32));
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int dark = 1;
            DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        void Start()
        {
            install.Enabled = startup.Enabled = false;
            log.Clear();
            status.Text = T("Instalando…", "Installing…");

            string script = Path.Combine(Path.GetTempPath(), "WtSidebar-install.ps1");
            using (var s = typeof(SetupForm).Assembly.GetManifestResourceStream("install.ps1"))
            using (var f = File.Create(script))
                s.CopyTo(f);

            // Saida em UTF-8 para os acentos chegarem certos no log.
            string command = "[Console]::OutputEncoding = [Text.Encoding]::UTF8; & '" + script.Replace("'", "''") +
                "' -Lang " + (Pt ? "pt" : "en") + (startup.Checked ? "" : " -NoStartup");
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command \"" + command + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (o, e) => Append(e.Data);
            process.ErrorDataReceived += (o, e) => Append(e.Data);
            process.Exited += delegate { BeginInvoke((Action)Finished); };
            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                Append(ex.Message);
                Finished();
            }
        }

        void Append(string line)
        {
            if (line == null || IsDisposed) return;
            BeginInvoke((Action)delegate { log.AppendText(line + Environment.NewLine); });
        }

        void Finished()
        {
            bool ok = process != null && process.HasExited && process.ExitCode == 0;
            if (ok)
            {
                status.Text = T("Pronto! O WT Sidebar está instalado e aberto ao lado do Windows Terminal.",
                    "Done! WT Sidebar is installed and running next to Windows Terminal.");
                status.ForeColor = Color.FromArgb(0x3F, 0xB9, 0x50);
                install.Visible = false;
                AcceptButton = close;
            }
            else
            {
                status.Text = T("A instalação falhou. Veja o log acima.", "The installation failed. See the log above.");
                status.ForeColor = Color.FromArgb(0xE5, 0x6B, 0x6B);
                install.Text = T("Tentar de novo", "Try again");
                install.Enabled = startup.Enabled = true;
            }
        }
    }
}
