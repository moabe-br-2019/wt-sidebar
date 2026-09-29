// Sidebar de abas verticais para o Windows Terminal.
// Le as abas via UI Automation e fica colada na borda esquerda da janela do terminal.
// Compila com o csc do .NET Framework 4 (C# 5): ver build.ps1.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;

namespace WtSidebar
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, "WtSidebar.SingleInstance", out created))
            {
                if (!created) return;
                Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new SidebarForm());
            }
        }
    }

    class TabInfo
    {
        public string Title;
        public bool Selected;
        public AutomationElement Element;
        public Color TabColor = Color.Empty;
        public Bitmap Icon;
        public int IconHash;
    }

    class SampledColor
    {
        public Color Color;
        public bool FromSelected;
    }

    class SidebarForm : Form
    {
        const int ExpandedWidth = 220;
        const int CollapsedWidth = 44;
        const int HeaderHeight = 32;
        const int ItemHeight = 34;
        const int HotkeyId = 1;

        const int HitNone = -1;
        const int HitToggle = -2;
        const int HitNewTab = -3;
        const int HitProfileMenu = -4;
        const int HitHeaderMenu = -5;

        static readonly Color Bg = Color.FromArgb(0x1f, 0x1f, 0x1f);
        static readonly Color HeaderBg = Color.FromArgb(0x18, 0x18, 0x18);
        static readonly Color HoverBg = Color.FromArgb(0x2a, 0x2a, 0x2a);
        static readonly Color SelectedBg = Color.FromArgb(0x33, 0x33, 0x33);
        static readonly Color Accent = Color.FromArgb(0xD9, 0x77, 0x57);
        static readonly Color Fg = Color.FromArgb(0xE6, 0xE6, 0xE6);
        static readonly Color FgDim = Color.FromArgb(0x9a, 0x9a, 0x9a);
        static readonly Color Border = Color.FromArgb(0x33, 0x33, 0x33);

        IntPtr wt = IntPtr.Zero;
        IntPtr hookWindow = IntPtr.Zero;
        IntPtr hookLocation = IntPtr.Zero;
        IntPtr hookForeground = IntPtr.Zero;
        readonly Native.WinEventDelegate winEventProc;

        bool collapsed;
        bool pseudoMax;
        Native.RECT savedRect;
        bool dragging;
        bool adjusting;
        bool hotkeyRegistered;

        List<TabInfo> tabs = new List<TabInfo>();
        // Usados so pela thread do Scan.
        readonly Dictionary<string, SampledColor> colorCache = new Dictionary<string, SampledColor>();
        readonly Dictionary<string, TabInfo> iconCache = new Dictionary<string, TabInfo>();
        static readonly Condition ImageCondition =
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Image);
        string tabsSignature = "";
        int hover = HitNone;
        bool hoverClose;
        int scroll;
        float scale = 1f;
        Font itemFont;
        Font glyphFont;

        readonly System.Windows.Forms.Timer findTimer = new System.Windows.Forms.Timer();
        System.Threading.Timer scanTimer;
        int scanning;
        readonly ToolTip toolTip = new ToolTip();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly ContextMenuStrip profileMenu = new ContextMenuStrip();
        readonly ContextMenuStrip appMenu = new ContextMenuStrip();
        readonly List<ToolStripItem> tabMenuItems = new List<ToolStripItem>(); // so aparecem com clique numa aba
        TabInfo menuTarget;

        static readonly Condition TabItemCondition =
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);

        public SidebarForm()
        {
            winEventProc = OnWinEvent;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(-32000, -32000, 1, 1);
            BackColor = Bg;
            Text = "WT Sidebar";
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            UpdateFonts();

            // Mesmos itens do menu de botao direito da guia no terminal. Cada um seleciona a aba e manda o
            // atalho da acao. Os Ctrl+Alt+Shift foram criados no settings.json do terminal, porque essas
            // acoes nao tem atalho padrao; Alt+Shift+D (dividir) ja era do usuario.
            var move = new ToolStripMenuItem("Mover aba");
            move.DropDownItems.Add(TabAction("Para nova janela", "^%+n", "Ctrl+Alt+Shift+N"));
            move.DropDownItems.Add(TabAction("Para a esquerda", "^%+{LEFT}", "Ctrl+Alt+Shift+←"));
            move.DropDownItems.Add(TabAction("Para a direita", "^%+{RIGHT}", "Ctrl+Alt+Shift+→"));
            var close = new ToolStripMenuItem("Fechar");
            close.DropDownItems.Add(TabAction("Fechar outras abas", "^%+o", "Ctrl+Alt+Shift+O"));
            close.DropDownItems.Add(TabAction("Fechar abas à direita", "^%+w", "Ctrl+Alt+Shift+W"));
            foreach (var sub in new[] { move, close })
            {
                var dd = (ToolStripDropDownMenu)sub.DropDown;
                dd.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
                dd.ShowImageMargin = false;
            }

            tabMenuItems.Add(TabAction("Alterar cor da aba", "^%+c", "Ctrl+Alt+Shift+C"));
            tabMenuItems.Add(TabAction("Renomear aba", "^%+r", "Ctrl+Alt+Shift+R"));
            tabMenuItems.Add(TabAction("Duplicar aba", "^+d", "Ctrl+Shift+D"));
            tabMenuItems.Add(TabAction("Dividir aba", "%+d", "Alt+Shift+D"));
            tabMenuItems.Add(move);
            tabMenuItems.Add(TabAction("Exportar texto", "^%+e", "Ctrl+Alt+Shift+E"));
            tabMenuItems.Add(TabAction("Localizar", "^+f", "Ctrl+Shift+F"));
            tabMenuItems.Add(new ToolStripSeparator());
            tabMenuItems.Add(close);
            tabMenuItems.Add(new ToolStripMenuItem("Fechar aba", null, delegate { CloseTab(menuTarget); }));
            tabMenuItems.Add(new ToolStripSeparator());
            foreach (var item in tabMenuItems) menu.Items.Add(item);
            menu.Items.Add("Recolher / expandir  (Ctrl+Shift+B)", null, delegate { ToggleCollapse(); });
            menu.Items.Add("Nova aba", null, delegate { NewTab(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Sair", null, delegate { Close(); });
            appMenu.Items.Add(new ToolStripMenuItem("Fechar WT Sidebar", null, delegate { Close(); }));
            appMenu.ShowImageMargin = false;
            foreach (var m in new[] { menu, profileMenu, appMenu })
            {
                m.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
                m.ForeColor = Fg;
                m.ShowImageMargin = true;
            }

            findTimer.Interval = 1000;
            findTimer.Tick += delegate { EnsureAttached(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        int SidebarWidth { get { return S(collapsed ? CollapsedWidth : ExpandedWidth); } }
        int S(int v) { return (int)Math.Round(v * scale); }

        void UpdateFonts()
        {
            if (itemFont != null) itemFont.Dispose();
            if (glyphFont != null) glyphFont.Dispose();
            itemFont = new Font("Segoe UI", 13f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            glyphFont = new Font("Segoe UI", 15f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            EnsureAttached();
            findTimer.Start();
            scanTimer = new System.Threading.Timer(delegate { Scan(); }, null, 0, 500);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Reposition();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            findTimer.Stop();
            if (scanTimer != null) scanTimer.Dispose();
            Unhook();
            SetHotkey(false);
            if (pseudoMax && wt != IntPtr.Zero && Native.IsWindow(wt))
            {
                Native.ShowWindow(wt, Native.SW_MAXIMIZE);
            }
            base.OnFormClosing(e);
        }

        // ---------- encontrar e acoplar na janela do terminal ----------

        static IntPtr FindTerminalWindow()
        {
            IntPtr found = IntPtr.Zero;
            var cls = new StringBuilder(64);
            Native.EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                cls.Length = 0;
                Native.GetClassName(h, cls, cls.Capacity);
                if (cls.ToString() == "CASCADIA_HOSTING_WINDOW_CLASS" && Native.IsWindowVisible(h))
                {
                    found = h;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        void EnsureAttached()
        {
            if (wt != IntPtr.Zero && Native.IsWindow(wt) && Native.IsWindowVisible(wt)) return;
            var h = FindTerminalWindow();
            if (h == wt) return;
            AttachTo(h);
        }

        void AttachTo(IntPtr h)
        {
            Unhook();
            wt = h;
            pseudoMax = false;
            dragging = false;
            SetTabs(new List<TabInfo>(), "");

            if (wt == IntPtr.Zero)
            {
                Native.SetWindowLongPtr(Handle, Native.GWLP_HWNDPARENT, IntPtr.Zero);
                Hide();
                return;
            }

            uint pid;
            Native.GetWindowThreadProcessId(wt, out pid);
            hookWindow = Native.SetWinEventHook(Native.EVENT_SYSTEM_MOVESIZESTART, Native.EVENT_SYSTEM_MINIMIZEEND,
                IntPtr.Zero, winEventProc, pid, 0, Native.WINEVENT_OUTOFCONTEXT);
            hookLocation = Native.SetWinEventHook(Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE,
                IntPtr.Zero, winEventProc, pid, 0, Native.WINEVENT_OUTOFCONTEXT);
            hookForeground = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, winEventProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);

            // Janela "dona": o Windows mantem a sidebar acima do terminal e minimiza as duas juntas.
            Native.SetWindowLongPtr(Handle, Native.GWLP_HWNDPARENT, wt);

            UpdateScale();
            SetHotkey(Native.GetForegroundWindow() == wt);
            HandleLocation();
        }

        void Unhook()
        {
            if (hookWindow != IntPtr.Zero) Native.UnhookWinEvent(hookWindow);
            if (hookLocation != IntPtr.Zero) Native.UnhookWinEvent(hookLocation);
            if (hookForeground != IntPtr.Zero) Native.UnhookWinEvent(hookForeground);
            hookWindow = hookLocation = hookForeground = IntPtr.Zero;
        }

        void UpdateScale()
        {
            float s = Native.GetDpiForWindow(wt) / 96f;
            if (s <= 0) s = 1f;
            if (Math.Abs(s - scale) < 0.01f) return;
            scale = s;
            UpdateFonts();
            Invalidate();
        }

        void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (evt == Native.EVENT_SYSTEM_FOREGROUND)
            {
                if (hwnd == wt) SetHotkey(true);
                else if (hwnd != Handle) SetHotkey(false);
                return;
            }
            if (hwnd != wt || idObject != Native.OBJID_WINDOW) return;

            switch (evt)
            {
                case Native.EVENT_SYSTEM_MOVESIZESTART:
                    dragging = true;
                    pseudoMax = false;
                    break;
                case Native.EVENT_SYSTEM_MOVESIZEEND:
                    dragging = false;
                    HandleLocation();
                    break;
                case Native.EVENT_SYSTEM_MINIMIZESTART:
                    Hide();
                    break;
                case Native.EVENT_SYSTEM_MINIMIZEEND:
                case Native.EVENT_OBJECT_LOCATIONCHANGE:
                    HandleLocation();
                    break;
            }
        }

        // ---------- geometria ----------

        void HandleLocation()
        {
            if (wt == IntPtr.Zero) return;
            if (!adjusting)
            {
                if (Native.IsZoomed(wt))
                {
                    HandleMaximize();
                }
                else if (!dragging && !Native.IsIconic(wt) && !IsFullscreen())
                {
                    if (pseudoMax) FitToWorkArea();
                    else EnsureRoom();
                }
            }
            Reposition();
        }

        // O terminal maximizado nao deixa espaco para a sidebar: troca por "tela inteira menos a sidebar".
        // Maximizar de novo volta ao tamanho de antes.
        void HandleMaximize()
        {
            adjusting = true;
            try
            {
                bool wasPseudo = pseudoMax;
                Native.ShowWindow(wt, Native.SW_RESTORE);
                if (wasPseudo)
                {
                    pseudoMax = false;
                    Native.SetWindowPos(wt, IntPtr.Zero, savedRect.Left, savedRect.Top,
                        savedRect.Right - savedRect.Left, savedRect.Bottom - savedRect.Top,
                        Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                    EnsureRoom();
                }
                else
                {
                    Native.GetWindowRect(wt, out savedRect);
                    pseudoMax = true;
                    FitToWorkArea();
                }
            }
            finally
            {
                adjusting = false;
            }
        }

        void FitToWorkArea()
        {
            var work = WorkArea();
            SetTerminalFrame(new Native.RECT
            {
                Left = work.Left + SidebarWidth,
                Top = work.Top,
                Right = work.Right,
                Bottom = work.Bottom
            });
        }

        // Se a sidebar ficaria fora da tela, empurra o terminal para a direita.
        void EnsureRoom()
        {
            var frame = TerminalFrame();
            var work = WorkArea();
            int minLeft = work.Left + SidebarWidth;
            if (frame.Left >= minLeft) return;
            int shift = minLeft - frame.Left;
            int right = Math.Min(frame.Right + shift, work.Right);
            if (right - minLeft < S(300)) return;
            SetTerminalFrame(new Native.RECT { Left = minLeft, Top = frame.Top, Right = right, Bottom = frame.Bottom });
        }

        void SetTerminalFrame(Native.RECT frame)
        {
            Native.RECT win;
            Native.GetWindowRect(wt, out win);
            var ext = TerminalFrame();
            int l = ext.Left - win.Left, t = ext.Top - win.Top;
            int r = win.Right - ext.Right, b = win.Bottom - ext.Bottom;
            bool wasAdjusting = adjusting;
            adjusting = true;
            try
            {
                Native.SetWindowPos(wt, IntPtr.Zero, frame.Left - l, frame.Top - t,
                    frame.Right - frame.Left + l + r, frame.Bottom - frame.Top + t + b,
                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            }
            finally
            {
                adjusting = wasAdjusting;
            }
        }

        // Borda visivel da janela (GetWindowRect inclui bordas invisiveis de redimensionamento).
        Native.RECT TerminalFrame()
        {
            Native.RECT r;
            if (Native.DwmGetWindowAttribute(wt, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(Native.RECT))) != 0)
                Native.GetWindowRect(wt, out r);
            return r;
        }

        Native.MONITORINFO Monitor()
        {
            var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO)) };
            Native.GetMonitorInfo(Native.MonitorFromWindow(wt, Native.MONITOR_DEFAULTTONEAREST), ref mi);
            return mi;
        }

        Native.RECT WorkArea() { return Monitor().rcWork; }

        bool IsFullscreen()
        {
            var f = TerminalFrame();
            var m = Monitor().rcMonitor;
            return f.Left <= m.Left && f.Top <= m.Top && f.Right >= m.Right && f.Bottom >= m.Bottom;
        }

        void Reposition()
        {
            if (wt == IntPtr.Zero || !Native.IsWindow(wt) || !Native.IsWindowVisible(wt)
                || Native.IsIconic(wt) || IsFullscreen())
            {
                if (Visible) Hide();
                return;
            }
            UpdateScale();
            var f = TerminalFrame();
            int w = SidebarWidth;
            var target = new Rectangle(f.Left - w, f.Top, w, f.Bottom - f.Top);
            if (Bounds != target)
            {
                Native.SetWindowPos(Handle, IntPtr.Zero, target.X, target.Y, target.Width, target.Height,
                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                ClampScroll();
                Invalidate();
            }
            if (!Visible) Show();
        }

        void ToggleCollapse()
        {
            collapsed = !collapsed;
            HandleLocation();
            Invalidate();
        }

        void SetHotkey(bool on)
        {
            if (on == hotkeyRegistered) return;
            if (on) hotkeyRegistered = Native.RegisterHotKey(Handle, HotkeyId, Native.MOD_CONTROL | Native.MOD_SHIFT | Native.MOD_NOREPEAT, (uint)Keys.B);
            else
            {
                Native.UnregisterHotKey(Handle, HotkeyId);
                hotkeyRegistered = false;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            {
                ToggleCollapse();
                return;
            }
            if (m.Msg == Native.WM_MOUSEACTIVATE)
            {
                m.Result = new IntPtr(Native.MA_NOACTIVATE);
                return;
            }
            base.WndProc(ref m);
        }

        // ---------- abas (UI Automation) ----------

        void Scan()
        {
            if (Interlocked.Exchange(ref scanning, 1) == 1) return;
            try
            {
                IntPtr h = wt;
                if (h == IntPtr.Zero) return;
                var list = new List<TabInfo>();
                var sig = new StringBuilder();
                // So da para ler a cor da tela quando o terminal esta na frente (nada cobrindo as guias).
                bool canSample = Native.GetForegroundWindow() == h;
                try
                {
                    var root = AutomationElement.FromHandle(h);
                    foreach (AutomationElement el in root.FindAll(TreeScope.Descendants, TabItemCondition))
                    {
                        object pattern;
                        bool selected = el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern)
                            && ((SelectionItemPattern)pattern).Current.IsSelected;
                        var t = new TabInfo { Title = el.Current.Name, Selected = selected, Element = el };
                        string key = string.Join(",", el.GetRuntimeId());
                        if (canSample)
                        {
                            Color bg = SampleTabColor(el, key, selected);
                            if (!bg.IsEmpty) CaptureIcon(el, key, bg);
                        }
                        SampledColor sc;
                        if (colorCache.TryGetValue(key, out sc)) t.TabColor = sc.Color;
                        TabInfo cached;
                        if (iconCache.TryGetValue(key, out cached))
                        {
                            t.Icon = cached.Icon;
                            t.IconHash = cached.IconHash;
                        }
                        list.Add(t);
                        sig.Append(selected ? '*' : ' ').Append(t.TabColor.ToArgb()).Append(' ').Append(t.IconHash)
                            .Append(t.Title).Append('\n');
                    }
                }
                catch (ElementNotAvailableException) { return; }
                catch (COMException) { return; }
                catch (Exception ex)
                {
                    Log(ex);
                    return;
                }

                // Com um menu aberto no terminal, a UI Automation so expoe o menu e a lista vem vazia.
                // Todo terminal tem ao menos uma aba, entao mantem a lista anterior.
                if (list.Count == 0) return;
                string s = sig.ToString();
                if (s == tabsSignature || !IsHandleCreated) return;
                BeginInvoke(new Action(delegate { if (h == wt) SetTabs(list, s); }));
            }
            finally
            {
                Interlocked.Exchange(ref scanning, 0);
            }
        }

        // Le um pixel da guia, entre a borda esquerda e o icone. Guia sem cor e cinza; com cor e saturada.
        // A guia selecionada mostra a cor cheia, entao essa leitura tem prioridade sobre a das outras.
        // Devolve a cor lida (fundo da guia) ou Color.Empty se a guia nao esta visivel.
        Color SampleTabColor(AutomationElement el, string key, bool selected)
        {
            var r = el.Current.BoundingRectangle;
            if (r.IsEmpty || el.Current.IsOffscreen) return Color.Empty;
            Color c = Native.ScreenPixel((int)r.Left + 4, (int)(r.Top + r.Height / 2));
            bool colored = c.GetSaturation() > 0.25f && c.GetBrightness() > 0.12f;

            SampledColor sc;
            colorCache.TryGetValue(key, out sc);
            if (selected || sc == null || !sc.FromSelected)
            {
                if (colored) colorCache[key] = new SampledColor { Color = c, FromSelected = selected };
                else colorCache.Remove(key);
            }
            return c;
        }

        // Copia da tela o icone do perfil que o terminal desenha na guia.
        // Os pixels com a cor de fundo da guia viram transparentes, para o icone assentar na sidebar.
        void CaptureIcon(AutomationElement tab, string key, Color bg)
        {
            var img = tab.FindFirst(TreeScope.Children, ImageCondition);
            if (img == null) return;
            var r = img.Current.BoundingRectangle;
            if (r.IsEmpty || r.Width < 4 || r.Width > 64) return;

            var bmp = new Bitmap((int)r.Width, (int)r.Height);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen((int)r.Left, (int)r.Top, 0, 0, bmp.Size);
            int hash = 17;
            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++)
                {
                    Color p = bmp.GetPixel(x, y);
                    int d = Math.Abs(p.R - bg.R) + Math.Abs(p.G - bg.G) + Math.Abs(p.B - bg.B);
                    if (d < 30) bmp.SetPixel(x, y, Color.Transparent);
                    else hash = hash * 31 + p.ToArgb();
                }

            TabInfo cached;
            if (iconCache.TryGetValue(key, out cached) && cached.IconHash == hash)
            {
                bmp.Dispose();
                return;
            }
            // O bitmap antigo pode estar sendo desenhado na thread da UI; fica para o GC.
            iconCache[key] = new TabInfo { Icon = bmp, IconHash = hash };
        }

        static void Log(Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "WtSidebar.log"),
                    DateTime.Now.ToString("s") + " " + ex + Environment.NewLine);
            }
            catch (Exception) { }
        }

        void SetTabs(List<TabInfo> list, string signature)
        {
            tabs = list;
            tabsSignature = signature;
            ClampScroll();
            Invalidate();
        }

        void SelectTab(int index)
        {
            if (index < 0 || index >= tabs.Count) return;
            var el = tabs[index].Element;
            for (int i = 0; i < tabs.Count; i++) tabs[i].Selected = i == index;
            tabsSignature = "";
            Invalidate();
            Native.SetForegroundWindow(wt);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    ((SelectionItemPattern)el.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                }
                catch (Exception) { }
            });
        }

        // Aciona o botao "x" da propria aba no terminal (AutomationId CloseButton).
        void CloseTab(TabInfo tab)
        {
            if (tab == null) return;
            var el = tab.Element;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    var button = el.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "CloseButton"));
                    if (button != null)
                        ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                }
                catch (Exception) { }
            });
        }

        ToolStripMenuItem TabAction(string text, string keys, string shortcut)
        {
            return new ToolStripMenuItem(text, null, delegate { SendToTab(menuTarget, keys); })
            {
                ShortcutKeyDisplayString = shortcut,
                ForeColor = Fg
            };
        }

        // Seleciona a aba e manda o atalho para o terminal (os atalhos agem sobre a aba ativa).
        void SendToTab(TabInfo tab, string keys)
        {
            if (tab == null) return;
            var el = tab.Element;
            Native.SetForegroundWindow(wt);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    ((SelectionItemPattern)el.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                }
                catch (Exception) { return; }
                Thread.Sleep(150);
                BeginInvoke(new Action(delegate { SendToTerminal(keys); }));
            });
        }

        void NewTab()
        {
            NewTab(null);
        }

        void NewTab(string profile)
        {
            string args = "-w 0 nt";
            if (profile != null) args += " -p \"" + profile + "\"";
            try
            {
                Process.Start(new ProcessStartInfo("wt.exe", args) { UseShellExecute = true });
            }
            catch (Exception) { }
        }

        // Atalhos padrao do Windows Terminal, enviados para a janela dele.
        void SendToTerminal(string keys)
        {
            Native.SetForegroundWindow(wt);
            SendKeys.SendWait(keys);
        }

        // ---------- menu de perfis (igual ao "⌄" do terminal) ----------

        static string SettingsPath()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] candidates =
            {
                Path.Combine(local, @"Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\settings.json"),
                Path.Combine(local, @"Packages\Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe\LocalState\settings.json"),
                Path.Combine(local, @"Microsoft\Windows Terminal\settings.json")
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            return null;
        }

        // settings.json aceita comentarios e virgulas sobrando; o JavaScriptSerializer nao.
        static string StripJsonc(string s)
        {
            var sb = new StringBuilder(s.Length);
            bool inString = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inString)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < s.Length) sb.Append(s[++i]);
                    else if (c == '"') inString = false;
                }
                else if (c == '"') { inString = true; sb.Append(c); }
                else if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
                {
                    while (i < s.Length && s[i] != '\n') i++;
                    sb.Append('\n');
                }
                else if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) i++;
                    i++;
                }
                else if (c == ',')
                {
                    int j = i + 1;
                    while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
                    if (j < s.Length && (s[j] == '}' || s[j] == ']')) continue;
                    sb.Append(c);
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v != null ? v.ToString() : null;
        }

        // Icone do perfil: arquivo configurado em "icon" ou, sem ele, o icone do executavel.
        Image ProfileIcon(Dictionary<string, object> p)
        {
            int size = S(16);
            try
            {
                string icon = Str(p, "icon");
                if (icon != null)
                {
                    icon = Environment.ExpandEnvironmentVariables(icon);
                    if (File.Exists(icon))
                    {
                        if (icon.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                            using (var ico = new Icon(icon, size, size)) return ico.ToBitmap();
                        using (var img = Image.FromFile(icon)) return new Bitmap(img, size, size);
                    }
                }
                string cmd = Str(p, "commandline");
                if (cmd == null) return null;
                cmd = Environment.ExpandEnvironmentVariables(cmd).Trim();
                string exe = cmd.StartsWith("\"") ? cmd.Substring(1, Math.Max(0, cmd.IndexOf('"', 1) - 1)) : cmd.Split(' ')[0];
                if (!File.Exists(exe)) return null;
                using (var ico = Icon.ExtractAssociatedIcon(exe))
                    return new Bitmap(ico.ToBitmap(), size, size);
            }
            catch (Exception) { return null; }
        }

        // ---------- abrir Claude Code / Codex numa pasta ----------

        const int MaxRecentFolders = 10;

        static string RecentFoldersFile
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WtSidebar", "recent-folders.txt");
            }
        }

        static List<string> LoadRecentFolders()
        {
            var list = new List<string>();
            try
            {
                if (File.Exists(RecentFoldersFile))
                    foreach (var line in File.ReadAllLines(RecentFoldersFile))
                        if (line.Trim().Length > 0 && Directory.Exists(line) && list.Count < MaxRecentFolders)
                            list.Add(line);
            }
            catch (Exception ex) { Log(ex); }
            return list;
        }

        static void RememberFolder(string dir)
        {
            var list = LoadRecentFolders();
            list.RemoveAll(d => string.Equals(d, dir, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, dir);
            if (list.Count > MaxRecentFolders) list.RemoveRange(MaxRecentFolders, list.Count - MaxRecentFolders);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RecentFoldersFile));
                File.WriteAllLines(RecentFoldersFile, list.ToArray());
            }
            catch (Exception ex) { Log(ex); }
        }

        // Submenu com as pastas recentes e "Escolher pasta...", que abre o comando numa aba nova.
        ToolStripMenuItem AgentMenu(string label, string command)
        {
            var menuItem = new ToolStripMenuItem(label) { ForeColor = Fg };
            var dd = (ToolStripDropDownMenu)menuItem.DropDown;
            dd.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
            dd.ShowImageMargin = false;
            dd.ShowItemToolTips = true;
            dd.Font = profileMenu.Font;

            foreach (var dir in LoadRecentFolders())
            {
                string folder = dir;
                string name = Path.GetFileName(folder.TrimEnd('\\'));
                if (name.Length == 0) name = folder;
                menuItem.DropDownItems.Add(new ToolStripMenuItem(name, null, delegate { LaunchAgent(command, folder); })
                    { ForeColor = Fg, ToolTipText = folder });
            }
            if (menuItem.DropDownItems.Count > 0) menuItem.DropDownItems.Add(new ToolStripSeparator());
            menuItem.DropDownItems.Add(new ToolStripMenuItem("Escolher pasta…", null, delegate
            {
                string dir = PickFolder();
                if (dir != null) LaunchAgent(command, dir);
            }) { ForeColor = Fg });
            return menuItem;
        }

        string PickFolder()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Pasta do projeto";
                dlg.ShowNewFolderButton = true;
                var recent = LoadRecentFolders();
                if (recent.Count > 0) dlg.SelectedPath = recent[0];
                return dlg.ShowDialog(this) == DialogResult.OK ? dlg.SelectedPath : null;
            }
        }

        // Aba nova com o perfil padrao, na pasta, rodando o comando. -NoExit deixa o shell aberto
        // quando o Claude/Codex sai.
        void LaunchAgent(string command, string dir)
        {
            RememberFolder(dir);
            // "C:\" terminaria em \" e escaparia a aspa na linha de comando.
            string d = dir.EndsWith("\\") ? dir + "." : dir;
            string args = "-w 0 nt -d \"" + d + "\" powershell.exe -NoExit -Command " + command;
            try
            {
                Process.Start(new ProcessStartInfo("wt.exe", args) { UseShellExecute = true });
            }
            catch (Exception ex) { Log(ex); }
        }

        void ShowProfileMenu(Point at)
        {
            foreach (ToolStripItem old in profileMenu.Items)
                if (old.Image != null) old.Image.Dispose();
            profileMenu.Items.Clear();
            profileMenu.Font = new Font("Segoe UI", 12f * scale, GraphicsUnit.Pixel);
            profileMenu.ImageScalingSize = new Size(S(16), S(16));

            try
            {
                string path = SettingsPath();
                var root = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(
                    StripJsonc(File.ReadAllText(path)));
                string defaultProfile = Str(root, "defaultProfile");
                var profiles = (Dictionary<string, object>)root["profiles"];
                var list = (object[])profiles["list"];
                int n = 0;
                foreach (Dictionary<string, object> p in list)
                {
                    if (Str(p, "hidden") == "True") continue;
                    n++;
                    string guid = Str(p, "guid");
                    string name = Str(p, "name");
                    var item = new ToolStripMenuItem(name, ProfileIcon(p), delegate { NewTab(guid ?? name); });
                    item.ForeColor = Fg;
                    if (guid != null && guid == defaultProfile) item.Font = new Font(profileMenu.Font, FontStyle.Bold);
                    if (n <= 9) item.ShortcutKeyDisplayString = "Ctrl+Shift+" + n;
                    profileMenu.Items.Add(item);
                }
            }
            catch (Exception)
            {
                profileMenu.Items.Add(new ToolStripMenuItem("Nova aba", null, delegate { NewTab(); }) { ForeColor = Fg });
            }

            profileMenu.Items.Add(new ToolStripSeparator());
            profileMenu.Items.Add(AgentMenu("Claude Code em", "claude"));
            profileMenu.Items.Add(AgentMenu("Codex em", "codex"));

            profileMenu.Items.Add(new ToolStripSeparator());
            profileMenu.Items.Add(new ToolStripMenuItem("Configurações", null, delegate { SendToTerminal("^,"); })
                { ForeColor = Fg, ShortcutKeyDisplayString = "Ctrl+," });
            profileMenu.Items.Add(new ToolStripMenuItem("Paleta de comandos", null, delegate { SendToTerminal("^+p"); })
                { ForeColor = Fg, ShortcutKeyDisplayString = "Ctrl+Shift+P" });
            profileMenu.Show(this, at);
        }

        // ---------- desenho e mouse ----------

        int ListTop { get { return S(HeaderHeight); } }
        int RowCount { get { return tabs.Count + 1; } } // +1 = linha "nova aba"

        void ClampScroll()
        {
            int max = Math.Max(0, RowCount * S(ItemHeight) - (ClientSize.Height - ListTop));
            scroll = Math.Max(0, Math.Min(scroll, max));
        }

        // Area do "WT Sidebar ⌄" no cabecalho.
        Rectangle HeaderMenuRect()
        {
            int pad = S(12);
            int textW = TextRenderer.MeasureText("WT Sidebar", itemFont, Size.Empty, TextFormatFlags.NoPadding).Width;
            return new Rectangle(pad - S(6), S(4), textW + S(22), ListTop - S(8));
        }

        int HitTest(Point p)
        {
            if (p.Y < ListTop)
            {
                if (collapsed || p.X >= ClientSize.Width - S(36)) return HitToggle;
                return HeaderMenuRect().Contains(p) ? HitHeaderMenu : HitNone;
            }
            int row = (p.Y - ListTop + scroll) / S(ItemHeight);
            if (row < tabs.Count) return row;
            if (row == tabs.Count)
                return !collapsed && p.X >= ClientSize.Width - S(32) ? HitProfileMenu : HitNewTab;
            return HitNone;
        }

        // Claude Code prefixa o titulo com o estado: ✳ parado esperando, ◐◓◑◒ trabalhando (gira).
        // A fonte nao desenha esses simbolos, entao remove o prefixo e devolve-o para desenhar o marcador.
        static string CleanTitle(string title, out char marker)
        {
            title = title ?? "";
            int i = 0;
            while (i < title.Length && !char.IsLetterOrDigit(title[i]) && !char.IsPunctuation(title[i])) i++;
            string prefix = title.Substring(0, i).Trim();
            marker = prefix.Length > 0 ? prefix[0] : '\0';
            return title.Substring(i);
        }

        static readonly Color Idle = Color.FromArgb(0x3F, 0xB9, 0x50);

        // ✳ = quadrado verde com asterisco; ◐◓◑◒ = circulo com metade preenchida no mesmo lado do simbolo.
        void DrawMarker(Graphics g, char marker, Rectangle r)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            if (marker == '✳')
            {
                using (var b = new SolidBrush(Idle))
                    g.FillRectangle(b, r);
                using (var p = new Pen(Color.White, Math.Max(1f, 1.3f * scale)))
                {
                    float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, k = r.Width * 0.3f;
                    g.DrawLine(p, cx - k, cy, cx + k, cy);
                    g.DrawLine(p, cx, cy - k, cx, cy + k);
                    g.DrawLine(p, cx - k * 0.7f, cy - k * 0.7f, cx + k * 0.7f, cy + k * 0.7f);
                    g.DrawLine(p, cx - k * 0.7f, cy + k * 0.7f, cx + k * 0.7f, cy - k * 0.7f);
                }
            }
            else
            {
                float start = 90; // ◐ metade esquerda
                if (marker == '◓') start = 180; // ◓ metade de cima
                else if (marker == '◑') start = 270; // ◑ metade direita
                else if (marker == '◒') start = 0; // ◒ metade de baixo
                using (var b = new SolidBrush(Accent))
                    g.FillPie(b, r, start, 180);
                using (var p = new Pen(Accent, Math.Max(1f, 1.2f * scale)))
                    g.DrawEllipse(p, r);
            }
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
        }

        // Area do "x" que aparece na direita da aba sob o mouse (so com a sidebar expandida).
        Rectangle CloseRect(int row)
        {
            int ih = S(ItemHeight);
            int size = S(22);
            int y = ListTop + row * ih - scroll;
            return new Rectangle(ClientSize.Width - S(8) - size, y + (ih - size) / 2, size, size);
        }

        bool InCloseZone(Point p, int h)
        {
            return !collapsed && h >= 0 && h < tabs.Count && CloseRect(h).Contains(p);
        }

        static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        static string Initial(string title)
        {
            foreach (char c in title ?? "")
                if (char.IsLetterOrDigit(c)) return char.ToUpperInvariant(c).ToString();
            return "?";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Bg);
            int w = ClientSize.Width;
            int ih = S(ItemHeight);
            int pad = S(12);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;

            // cabecalho
            using (var b = new SolidBrush(HeaderBg))
                g.FillRectangle(b, 0, 0, w, ListTop);
            var headerRect = new Rectangle(0, 0, w, ListTop);
            if (collapsed)
            {
                if (hover == HitToggle)
                    using (var b = new SolidBrush(HoverBg))
                        g.FillRectangle(b, headerRect);
                TextRenderer.DrawText(g, "»", glyphFont, headerRect, FgDim, flags | TextFormatFlags.HorizontalCenter);
            }
            else
            {
                var menuZone = HeaderMenuRect();
                var toggleZone = new Rectangle(w - S(36), 0, S(36), ListTop);
                if (hover == HitHeaderMenu || hover == HitToggle)
                    using (var b = new SolidBrush(HoverBg))
                        g.FillRectangle(b, hover == HitHeaderMenu ? menuZone : toggleZone);

                // "WT Sidebar ⌄": abre o menu com Fechar
                string label = "WT Sidebar";
                int textW = TextRenderer.MeasureText(g, label, itemFont, Size.Empty, TextFormatFlags.NoPadding).Width;
                TextRenderer.DrawText(g, label, itemFont, new Rectangle(pad, 0, textW + S(4), ListTop), FgDim, flags | TextFormatFlags.NoPadding);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var pen = new Pen(FgDim, Math.Max(1f, 1.3f * scale)))
                {
                    float cx = pad + textW + S(10), cy = ListTop / 2f, k = S(3);
                    g.DrawLines(pen, new[] { new PointF(cx - k, cy - k / 2), new PointF(cx, cy + k / 2), new PointF(cx + k, cy - k / 2) });
                }
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;

                TextRenderer.DrawText(g, "«", glyphFont, new Rectangle(w - S(36), 0, S(28), ListTop), FgDim, flags | TextFormatFlags.HorizontalCenter);
            }

            g.SetClip(new Rectangle(0, ListTop, w, ClientSize.Height - ListTop));
            for (int i = 0; i <= tabs.Count; i++)
            {
                int y = ListTop + i * ih - scroll;
                if (y + ih < ListTop || y > ClientSize.Height) continue;
                var row = new Rectangle(0, y, w, ih);
                bool isNew = i == tabs.Count;
                bool selected = !isNew && tabs[i].Selected;
                bool hovered = isNew ? hover == HitNewTab || hover == HitProfileMenu : hover == i;
                Color tabColor = isNew ? Color.Empty : tabs[i].TabColor;

                if (selected || hovered)
                {
                    Color bg = selected ? SelectedBg : HoverBg;
                    if (selected && !tabColor.IsEmpty) bg = Blend(bg, tabColor, 0.25f);
                    using (var b = new SolidBrush(bg))
                        g.FillRectangle(b, row);
                }
                // Barra na esquerda: cor da guia (sempre) ou laranja so na selecionada.
                if (!tabColor.IsEmpty)
                    using (var b = new SolidBrush(tabColor))
                        g.FillRectangle(b, 0, y + S(selected ? 4 : 8), S(selected ? 4 : 3), ih - S(selected ? 8 : 16));
                else if (selected)
                    using (var b = new SolidBrush(Accent))
                        g.FillRectangle(b, 0, y + S(6), S(3), ih - S(12));

                if (isNew)
                {
                    string label = collapsed ? "+" : "+  Nova aba";
                    var tf = collapsed ? flags | TextFormatFlags.HorizontalCenter : flags;
                    var rect = collapsed ? row : new Rectangle(pad, y, w - pad * 2, ih);
                    TextRenderer.DrawText(g, label, itemFont, rect, FgDim, tf);
                    if (!collapsed)
                    {
                        // setinha que abre o menu de perfis
                        var zone = new Rectangle(w - S(32), y + S(5), S(26), ih - S(10));
                        if (hover == HitProfileMenu)
                            using (var b = new SolidBrush(Color.FromArgb(0x48, 0x48, 0x48)))
                                g.FillRectangle(b, zone);
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        using (var pen = new Pen(hover == HitProfileMenu ? Fg : FgDim, Math.Max(1f, 1.4f * scale)))
                        {
                            float cx = zone.X + zone.Width / 2f, cy = zone.Y + zone.Height / 2f, k = S(4);
                            g.DrawLines(pen, new[] { new PointF(cx - k, cy - k / 2), new PointF(cx, cy + k / 2), new PointF(cx + k, cy - k / 2) });
                        }
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
                    }
                }
                else
                {
                    char marker;
                    string title = CleanTitle(tabs[i].Title, out marker);
                    bool marked = marker != '\0';
                    int dot = S(collapsed ? 8 : 12);
                    int iconSize = S(16);
                    var icon = tabs[i].Icon;
                    if (marked)
                    {
                        int dx = collapsed ? w - S(13) : pad + (icon != null ? iconSize + S(8) : 0);
                        int dy = collapsed ? y + S(5) : y + (ih - dot) / 2;
                        DrawMarker(g, marker, new Rectangle(dx, dy, dot, dot));
                    }
                    if (collapsed)
                    {
                        if (icon != null)
                            g.DrawImage(icon, new Rectangle((w - iconSize) / 2, y + (ih - iconSize) / 2, iconSize, iconSize));
                        else
                            TextRenderer.DrawText(g, Initial(title), glyphFont, row, selected ? Fg : FgDim, flags | TextFormatFlags.HorizontalCenter);
                    }
                    else
                    {
                        int tx = pad;
                        if (icon != null)
                        {
                            g.DrawImage(icon, new Rectangle(tx, y + (ih - iconSize) / 2, iconSize, iconSize));
                            tx += iconSize + S(8);
                        }
                        if (marked) tx += dot + S(8);
                        int right = pad;
                        if (hovered)
                        {
                            var cr = CloseRect(i);
                            right = w - cr.Left + S(4);
                            if (hoverClose)
                                using (var b = new SolidBrush(Color.FromArgb(0x48, 0x48, 0x48)))
                                    g.FillRectangle(b, cr);
                            TextRenderer.DrawText(g, "×", glyphFont, cr, hoverClose ? Fg : FgDim,
                                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                        }
                        TextRenderer.DrawText(g, title, itemFont, new Rectangle(tx, y, w - tx - right, ih), selected ? Fg : FgDim, flags);
                    }
                }
            }
            g.ResetClip();

            using (var p = new Pen(Border))
                g.DrawLine(p, w - 1, 0, w - 1, ClientSize.Height);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitTest(e.Location);
            bool hc = InCloseZone(e.Location, h);
            if (h == hover && hc == hoverClose) return;
            hover = h;
            hoverClose = hc;
            string tip = null;
            if (hc) tip = "Fechar aba";
            else if (h >= 0 && h < tabs.Count) tip = tabs[h].Title;
            else if (h == HitNewTab) tip = collapsed ? "Nova aba (botão direito: perfis)" : "Nova aba";
            else if (h == HitProfileMenu) tip = "Abrir perfil";
            else if (h == HitHeaderMenu) tip = "Menu do WT Sidebar";
            else if (h == HitToggle) tip = collapsed ? "Expandir (Ctrl+Shift+B)" : "Recolher (Ctrl+Shift+B)";
            toolTip.SetToolTip(this, tip);
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = HitNone;
            hoverClose = false;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            int h = HitTest(e.Location);
            if (e.Button == MouseButtons.Right && (h == HitNewTab || h == HitProfileMenu))
            {
                ShowProfileMenu(e.Location);
                return;
            }
            if (e.Button == MouseButtons.Right)
            {
                menuTarget = h >= 0 && h < tabs.Count ? tabs[h] : null;
                foreach (var item in tabMenuItems) item.Visible = menuTarget != null;
                menu.Show(this, e.Location);
                return;
            }
            if (e.Button == MouseButtons.Middle)
            {
                if (h >= 0 && h < tabs.Count) CloseTab(tabs[h]);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (InCloseZone(e.Location, h)) CloseTab(tabs[h]);
            else if (h == HitToggle) ToggleCollapse();
            else if (h == HitHeaderMenu)
            {
                var r = HeaderMenuRect();
                appMenu.Show(this, new Point(r.Left, r.Bottom + S(4)));
            }
            else if (h == HitNewTab) NewTab();
            else if (h == HitProfileMenu) ShowProfileMenu(new Point(ClientSize.Width - S(32), ListTop + tabs.Count * S(ItemHeight) - scroll + S(ItemHeight)));
            else if (h >= 0) SelectTab(h);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            scroll -= e.Delta / 120 * S(ItemHeight);
            ClampScroll();
            Invalidate();
        }
    }

    // Menus escuros, no estilo do Windows Terminal.
    class DarkMenuColors : ProfessionalColorTable
    {
        static readonly Color Back = Color.FromArgb(0x2b, 0x2b, 0x2b);
        static readonly Color Hover = Color.FromArgb(0x3d, 0x3d, 0x3d);
        static readonly Color Line = Color.FromArgb(0x45, 0x45, 0x45);
        public override Color ToolStripDropDownBackground { get { return Back; } }
        public override Color ImageMarginGradientBegin { get { return Back; } }
        public override Color ImageMarginGradientMiddle { get { return Back; } }
        public override Color ImageMarginGradientEnd { get { return Back; } }
        public override Color MenuBorder { get { return Line; } }
        public override Color MenuItemBorder { get { return Hover; } }
        public override Color MenuItemSelected { get { return Hover; } }
        public override Color SeparatorDark { get { return Line; } }
        public override Color SeparatorLight { get { return Back; } }
    }

    static class Native
    {
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int GWLP_HWNDPARENT = -8;
        public const int WM_HOTKEY = 0x0312;
        public const int WM_MOUSEACTIVATE = 0x0021;
        public const int MA_NOACTIVATE = 3;
        public const uint MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;

        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        public const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
        public const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
        public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
        public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
        public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        public const uint WINEVENT_OUTOFCONTEXT = 0;
        public const int OBJID_WINDOW = 0;

        public const int SW_MAXIMIZE = 3;
        public const int SW_RESTORE = 9;
        public const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        public const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        public delegate void WinEventDelegate(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventDelegate proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr hdc, int x, int y);

        public static Color ScreenPixel(int x, int y)
        {
            IntPtr dc = GetDC(IntPtr.Zero);
            try
            {
                uint c = GetPixel(dc, x, y);
                return Color.FromArgb((int)(c & 0xFF), (int)((c >> 8) & 0xFF), (int)((c >> 16) & 0xFF));
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, dc);
            }
        }
    }
}
