// Configuracoes do WT Sidebar: arquivo settings.json, idioma da interface e a janela de configuracoes.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WtSidebar
{
    // Textos da interface: cada chamada traz o portugues e o ingles lado a lado.
    static class L
    {
        public static bool English { get; private set; }

        // "auto" segue o idioma do Windows: portugues so se ele estiver em portugues.
        public static void Apply(string language)
        {
            if (language == "pt") English = false;
            else if (language == "en") English = true;
            else English = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "pt";
        }

        public static string T(string pt, string en) { return English ? en : pt; }
    }

    class AgentCommand
    {
        public string Name { get; set; }
        public string Command { get; set; }
    }

    class AppSettings
    {
        public const int MinWidth = 160, MaxWidth = 400;

        public string Language { get; set; }
        public bool AutoUpdate { get; set; }
        public int Width { get; set; }
        public bool StartCollapsed { get; set; }
        public List<AgentCommand> Agents { get; set; }

        public AppSettings()
        {
            Language = "auto";
            AutoUpdate = true;
            Width = 220;
            Agents = new List<AgentCommand>
            {
                new AgentCommand { Name = "Claude Code", Command = "claude" },
                new AgentCommand { Name = "Codex", Command = "codex" }
            };
        }

        static string FilePath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WtSidebar", "settings.json");
            }
        }

        public static AppSettings Load()
        {
            var s = new AppSettings();
            try
            {
                if (File.Exists(FilePath))
                    s = new JavaScriptSerializer().Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
            catch (Exception ex) { SidebarForm.Log(ex); }
            if (s.Language != "pt" && s.Language != "en") s.Language = "auto";
            s.Width = Math.Max(MinWidth, Math.Min(MaxWidth, s.Width));
            if (s.Agents == null) s.Agents = new List<AgentCommand>();
            s.Agents.RemoveAll(a => a == null || string.IsNullOrEmpty(a.Name) || string.IsNullOrEmpty(a.Command));
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(this), Encoding.UTF8);
            }
            catch (Exception ex) { SidebarForm.Log(ex); }
        }

        // ---------- iniciar com o Windows (HKCU\...\Run) ----------

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "WtSidebar";

        // Atalho que versoes antigas do instalador criavam na pasta Inicializar.
        static string LegacyStartupShortcut
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "WtSidebar.lnk"); }
        }

        public static bool StartWithWindows
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                    if (key != null && key.GetValue(RunValue) != null) return true;
                return File.Exists(LegacyStartupShortcut);
            }
            set
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) key.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
                    else key.DeleteValue(RunValue, false);
                }
                if (File.Exists(LegacyStartupShortcut)) File.Delete(LegacyStartupShortcut);
            }
        }
    }

    // Janela de configuracoes, escura como a sidebar. Os tamanhos sao em pixels de 96 dpi vezes a escala.
    class SettingsForm : Form
    {
        static readonly Color Back = Color.FromArgb(0x1f, 0x1f, 0x1f);
        static readonly Color Field = Color.FromArgb(0x2b, 0x2b, 0x2b);
        static readonly Color Line = Color.FromArgb(0x45, 0x45, 0x45);
        static readonly Color Fg = Color.FromArgb(0xE6, 0xE6, 0xE6);
        static readonly Color FgDim = Color.FromArgb(0x9a, 0x9a, 0x9a);

        readonly float scale;
        readonly ComboBox language = new ComboBox();
        readonly CheckBox startWithWindows = new CheckBox();
        readonly CheckBox autoUpdate = new CheckBox();
        readonly NumericUpDown width = new NumericUpDown();
        readonly CheckBox startCollapsed = new CheckBox();
        readonly DataGridView agents = new DataGridView();
        static readonly string[] LanguageCodes = { "auto", "pt", "en" };

        public AppSettings Result { get; private set; }

        int S(int v) { return (int)Math.Round(v * scale); }

        public SettingsForm(AppSettings current, float scale)
        {
            this.scale = scale;
            Text = L.T("Configurações do WT Sidebar", "WT Sidebar settings");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Back;
            ForeColor = Fg;
            Font = new Font("Segoe UI", 12f * scale, GraphicsUnit.Pixel);
            ClientSize = new Size(S(460), S(520));

            int x = S(20), y = S(18), w = ClientSize.Width - S(40);

            AddLabel(L.T("Idioma", "Language"), x, y + S(4));
            language.DropDownStyle = ComboBoxStyle.DropDownList;
            language.Items.AddRange(new object[] { L.T("Automático (idioma do Windows)", "Automatic (Windows language)"), "Português", "English" });
            language.SelectedIndex = Math.Max(0, Array.IndexOf(LanguageCodes, current.Language));
            StyleField(language);
            language.SetBounds(x + S(150), y, w - S(150), S(26));
            y += S(40);

            AddLabel(L.T("Largura (px)", "Width (px)"), x, y + S(4));
            width.Minimum = AppSettings.MinWidth;
            width.Maximum = AppSettings.MaxWidth;
            width.Increment = 10;
            width.Value = current.Width;
            StyleField(width);
            width.SetBounds(x + S(150), y, S(90), S(26));
            y += S(40);

            AddCheck(startCollapsed, L.T("Começar recolhida", "Start collapsed"), current.StartCollapsed, x, ref y);
            AddCheck(startWithWindows, L.T("Iniciar com o Windows", "Start with Windows"), AppSettings.StartWithWindows, x, ref y);
            AddCheck(autoUpdate, L.T("Buscar atualizações automaticamente", "Check for updates automatically"), current.AutoUpdate, x, ref y);
            y += S(10);

            AddLabel(L.T("Comandos de agente (menu ⌄ ao lado de Nova aba)", "Agent commands (⌄ menu next to New tab)"), x, y);
            y += S(24);
            SetupAgents(current.Agents);
            agents.SetBounds(x, y, w, S(170));
            y += S(178);
            var hint = AddLabel(L.T("Cada comando abre numa aba nova, na pasta escolhida. Delete remove a linha.",
                "Each command opens in a new tab, in the chosen folder. Delete removes the row."), x, y);
            hint.ForeColor = FgDim;
            y += S(34);

            var keys = AddButton(L.T("Configurar atalhos do terminal", "Set up terminal shortcuts"), x, y, S(250));
            keys.Click += delegate { SetupTerminalKeys(); };

            var cancel = AddButton(L.T("Cancelar", "Cancel"), ClientSize.Width - S(20) - S(90), ClientSize.Height - S(48), S(90));
            cancel.DialogResult = DialogResult.Cancel;
            var ok = AddButton("OK", cancel.Left - S(8) - S(90), cancel.Top, S(90));
            ok.Click += delegate { Accept(); };
            AcceptButton = ok;
            CancelButton = cancel;
        }

        // Barra de titulo escura (Windows 10 20H1+; em versoes anteriores o atributo e ignorado).
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int dark = 1;
            Native.DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        }

        Label AddLabel(string text, int x, int y)
        {
            var label = new Label { Text = text, AutoSize = true, ForeColor = Fg, BackColor = Back };
            label.Location = new Point(x, y);
            Controls.Add(label);
            return label;
        }

        void AddCheck(CheckBox box, string text, bool value, int x, ref int y)
        {
            box.Text = text;
            box.Checked = value;
            box.AutoSize = true;
            box.ForeColor = Fg;
            box.Location = new Point(x, y);
            Controls.Add(box);
            y += S(30);
        }

        Button AddButton(string text, int x, int y, int w)
        {
            var b = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = Field, ForeColor = Fg };
            b.FlatAppearance.BorderColor = Line;
            b.SetBounds(x, y, w, S(30));
            Controls.Add(b);
            return b;
        }

        void StyleField(Control c)
        {
            c.BackColor = Field;
            c.ForeColor = Fg;
            Controls.Add(c);
        }

        void SetupAgents(List<AgentCommand> list)
        {
            agents.AllowUserToAddRows = true;
            agents.AllowUserToDeleteRows = true;
            agents.AllowUserToResizeRows = false;
            agents.RowHeadersVisible = false;
            agents.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            agents.BackgroundColor = Field;
            agents.BorderStyle = BorderStyle.FixedSingle;
            agents.GridColor = Line;
            agents.EnableHeadersVisualStyles = false;
            agents.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            agents.ColumnHeadersDefaultCellStyle.BackColor = Back;
            agents.ColumnHeadersDefaultCellStyle.ForeColor = FgDim;
            agents.ColumnHeadersDefaultCellStyle.SelectionBackColor = Back;
            agents.DefaultCellStyle.BackColor = Field;
            agents.DefaultCellStyle.ForeColor = Fg;
            agents.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0x3d, 0x3d, 0x3d);
            agents.DefaultCellStyle.SelectionForeColor = Fg;
            agents.RowTemplate.Height = S(24);
            agents.ColumnHeadersHeight = S(26);
            agents.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            agents.Columns.Add("name", L.T("Nome", "Name"));
            agents.Columns.Add("command", L.T("Comando", "Command"));
            agents.Columns[0].FillWeight = 40;
            agents.Columns[1].FillWeight = 60;
            foreach (var a in list) agents.Rows.Add(a.Name, a.Command);
            Controls.Add(agents);
        }

        void Accept()
        {
            agents.EndEdit();
            var result = new AppSettings
            {
                Language = LanguageCodes[Math.Max(0, language.SelectedIndex)],
                Width = (int)width.Value,
                StartCollapsed = startCollapsed.Checked,
                AutoUpdate = autoUpdate.Checked,
                Agents = new List<AgentCommand>()
            };
            foreach (DataGridViewRow row in agents.Rows)
            {
                if (row.IsNewRow) continue;
                string name = Convert.ToString(row.Cells[0].Value).Trim();
                string command = Convert.ToString(row.Cells[1].Value).Trim();
                if (name.Length > 0 && command.Length > 0)
                    result.Agents.Add(new AgentCommand { Name = name, Command = command });
            }
            try
            {
                if (startWithWindows.Checked != AppSettings.StartWithWindows)
                    AppSettings.StartWithWindows = startWithWindows.Checked;
            }
            catch (Exception ex) { SidebarForm.Log(ex); }
            Result = result;
            DialogResult = DialogResult.OK;
        }

        // Roda a etapa de atalhos do install.ps1 (o instalado fica ao lado do exe; num clone, tambem).
        void SetupTerminalKeys()
        {
            string script = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "install.ps1");
            if (!File.Exists(script))
            {
                MessageBox.Show(this, L.T("Não achei o install.ps1 ao lado do WtSidebar.exe.", "install.ps1 was not found next to WtSidebar.exe."),
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -KeysOnly -Lang " + (L.English ? "en" : "pt"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            string output;
            Cursor = Cursors.WaitCursor;
            try
            {
                using (var p = Process.Start(psi))
                {
                    output = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit();
                }
            }
            catch (Exception ex) { output = ex.Message; }
            finally { Cursor = Cursors.Default; }
            MessageBox.Show(this, output.Length > 0 ? output : L.T("Nada a fazer.", "Nothing to do."),
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
