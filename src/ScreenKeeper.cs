using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("화면지킴이")]
[assembly: System.Reflection.AssemblyDescription("입력이 없을 때 화면 보호기와 자동 절전을 방지합니다.")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (c) 2026 ScreenKeeper contributors. MIT License.")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName=".NET Framework 4.8")]

namespace ScreenKeeper
{
    static class Native
    {
        public const uint Continuous = 0x80000000, Display = 2, SystemRequired = 1;
        [StructLayout(LayoutKind.Sequential)] public struct LastInput { public uint size, time; }
        [StructLayout(LayoutKind.Sequential)] public struct MouseInput
        { public int x, y; public uint data, flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] public struct KeyboardInput
        { public ushort key, scan; public uint flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Explicit)] public struct InputUnion
        { [FieldOffset(0)] public MouseInput mouse; [FieldOffset(0)] public KeyboardInput keyboard; }
        [StructLayout(LayoutKind.Sequential)] public struct Input { public uint type; public InputUnion value; }
        [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LastInput input);
        [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
        [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);
        [DllImport("user32.dll")] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder text, uint length, out uint needed);
        [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool UnregisterHotKey(IntPtr window, int id);

        [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
        static extern bool QuerySession(IntPtr server, int session, int info, out IntPtr buffer, out uint bytes);
        [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr buffer);
        public static bool SessionActive()
        {
            IntPtr buffer; uint size;
            if (!QuerySession(IntPtr.Zero, -1, 8, out buffer, out size)) return true; // RDS 조회 불가 시 입력 데스크톱 검사로 판단합니다.
            try { return size >= 4 && Marshal.ReadInt32(buffer) == 0; }
            finally { WTSFreeMemory(buffer); }
        }

        public static uint Elapsed(uint now, uint before) { return unchecked(now - before); }
        public static bool IsDue(uint idle, uint sinceAction, uint interval) { return idle >= interval && sinceAction >= interval; }
        public static bool Idle(out uint milliseconds)
        {
            LastInput last = new LastInput(); last.size = (uint)Marshal.SizeOf(typeof(LastInput));
            bool ok = GetLastInputInfo(ref last);
            milliseconds = ok ? Elapsed(unchecked((uint)Environment.TickCount), last.time) : 0;
            return ok;
        }
        public static bool DesktopAvailable()
        {
            IntPtr handle = OpenInputDesktop(0, false, 1);
            if (handle == IntPtr.Zero) return false;
            try
            {
                uint needed; StringBuilder name = new StringBuilder(128);
                return GetUserObjectInformation(handle, 2, name, 256, out needed) && name.ToString() == "Default";
            }
            finally { CloseDesktop(handle); }
        }
        public static bool ButtonsHeld()
        {
            for (int key = 1; key <= 254; key++)
                if ((GetAsyncKeyState(key) & 0x8000) != 0) return true;
            return false;
        }
        public static int Normalize(int coordinate, int origin, int size)
        { return (int)Math.Min(65535L, Math.Max(0L, ((long)(coordinate - origin) * 65536L + 32768L) / size)); }
        public static Input Mouse(int x, int y, uint flags)
        {
            Input input = new Input(); input.value.mouse.x = x; input.value.mouse.y = y;
            input.value.mouse.flags = flags; return input;
        }
        public static bool Pulse(bool click)
        {
            Input[] inputs;
            if (click) inputs = new Input[] { Mouse(0, 0, 2), Mouse(0, 0, 4) };
            else
            {
                Rectangle desktop = SystemInformation.VirtualScreen; Point point = Cursor.Position;
                int other = point.X < desktop.Right - 1 ? point.X + 1 : Math.Max(desktop.Left, point.X - 1);
                int y = Normalize(point.Y, desktop.Top, desktop.Height);
                const uint flags = 0x0001 | 0x2000 | 0x4000 | 0x8000;
                inputs = new Input[] { Mouse(Normalize(other, desktop.Left, desktop.Width), y, flags),
                    Mouse(Normalize(point.X, desktop.Left, desktop.Width), y, flags) };
            }
            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input)));
            if (click && sent == 1) SendInput(1, new Input[] { Mouse(0, 0, 4) }, Marshal.SizeOf(typeof(Input)));
            return sent == inputs.Length;
        }
    }

    sealed class GlobalShortcut : IDisposable
    {
        public const uint DefaultModifiers = 3;
        public const Keys DefaultKey = Keys.F8;
        public uint Modifiers { get; private set; }
        public Keys Key { get; private set; }
        public int Id { get; private set; }
        public bool Registered { get; private set; }
        public int LastError { get; private set; }
        IntPtr window;
        public GlobalShortcut() { Modifiers = DefaultModifiers; Key = DefaultKey; Id = 100; }
        public static bool Valid(uint modifiers, Keys key)
        {
            bool mainKey = (key >= Keys.F1 && key <= Keys.F24 && key != Keys.F12) ||
                (key >= Keys.A && key <= Keys.Z) || (key >= Keys.D0 && key <= Keys.D9) ||
                (key >= Keys.NumPad0 && key <= Keys.NumPad9);
            return mainKey && (modifiers & ~7u) == 0 && (modifiers & 3u) != 0;
        }
        public void Load(uint modifiers, Keys key)
        { if (Valid(modifiers, key) && !Registered) { Modifiers = modifiers; Key = key; } }
        public static string KeyName(Keys key)
        {
            if (key >= Keys.D0 && key <= Keys.D9) return ((int)key - (int)Keys.D0).ToString();
            if (key >= Keys.NumPad0 && key <= Keys.NumPad9) return "숫자패드 " + ((int)key - (int)Keys.NumPad0);
            return key.ToString();
        }
        public string DisplayName
        {
            get { return ((Modifiers & 2) != 0 ? "Ctrl+" : "") + ((Modifiers & 1) != 0 ? "Alt+" : "") +
                ((Modifiers & 4) != 0 ? "Shift+" : "") + KeyName(Key); }
        }
        public bool Bind(IntPtr handle)
        { Dispose(); window = handle; return TryApply(Modifiers, Key); }
        public bool TryApply(uint modifiers, Keys key)
        {
            if (!Valid(modifiers, key) || window == IntPtr.Zero) return false;
            if (Registered && modifiers == Modifiers && key == Key) { LastError = 0; return true; }
            int candidate = Id == 100 ? 101 : 100;
            // 새 조합을 먼저 등록하여 충돌이 발생해도 기존 단축키가 유지됩니다.
            if (!Native.RegisterHotKey(window, candidate, modifiers | 0x4000, (uint)key)) { LastError = Marshal.GetLastWin32Error(); return false; }
            if (Registered && !Native.UnregisterHotKey(window, Id))
            { LastError = Marshal.GetLastWin32Error(); Native.UnregisterHotKey(window, candidate); return false; }
            Id = candidate; Modifiers = modifiers; Key = key; Registered = true; LastError = 0; return true;
        }
        public void Dispose()
        { if (Registered) Native.UnregisterHotKey(window, Id); Registered = false; window = IntPtr.Zero; }
    }

    sealed class ShortcutDialog : Form
    {
        readonly CheckBox control, alt, shift;
        readonly ComboBox key;
        readonly Label error;
        public ShortcutDialog(GlobalShortcut current, Func<uint, Keys, string> apply)
        {
            Text = "켜기·끄기 단축키 변경"; ClientSize = new Size(438, 281);
            Font = new Font("맑은 고딕", 10); BackColor = Color.FromArgb(244, 247, 250);
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            Controls.Add(new Label { Text = "함께 누를 키와 기본 키를 선택하세요.", Location = new Point(22, 18), Size = new Size(395, 28) });
            control = new CheckBox { Text = "Ctrl", Location = new Point(24, 59), Size = new Size(90, 28) };
            alt = new CheckBox { Text = "Alt", Location = new Point(130, 59), Size = new Size(90, 28) };
            shift = new CheckBox { Text = "Shift", Location = new Point(236, 59), Size = new Size(90, 28) };
            Controls.AddRange(new Control[] { control, alt, shift });
            Controls.Add(new Label { Text = "기본 키", Location = new Point(24, 104), Size = new Size(90, 27) });
            key = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(130, 100), Size = new Size(284, 30), AccessibleName = "단축키 기본 키" };
            for (Keys k = Keys.F1; k <= Keys.F24; k++) if (k != Keys.F12) AddKey(k);
            for (Keys k = Keys.A; k <= Keys.Z; k++) AddKey(k);
            for (Keys k = Keys.D0; k <= Keys.D9; k++) AddKey(k);
            for (Keys k = Keys.NumPad0; k <= Keys.NumPad9; k++) AddKey(k);
            Controls.Add(key); SelectShortcut(current.Modifiers, current.Key);
            error = new Label { Text = "Ctrl 또는 Alt를 하나 이상 포함해야 합니다.", Location = new Point(24, 147), Size = new Size(390, 55), ForeColor = Color.FromArgb(94, 109, 126) };
            Controls.Add(error);
            Button reset = new Button { Text = "기본값", Location = new Point(24, 218), Size = new Size(112, 37) };
            Button save = new Button { Text = "적용", Location = new Point(164, 218), Size = new Size(112, 37) };
            Button cancel = new Button { Text = "취소", Location = new Point(302, 218), Size = new Size(112, 37), DialogResult = DialogResult.Cancel };
            Controls.AddRange(new Control[] { reset, save, cancel }); AcceptButton = save; CancelButton = cancel;
            reset.Click += delegate { SelectShortcut(GlobalShortcut.DefaultModifiers, GlobalShortcut.DefaultKey); error.Text = "기본값을 선택했습니다. 적용을 누르면 저장합니다."; };
            save.Click += delegate
            {
                uint modifiers = (control.Checked ? 2u : 0) | (alt.Checked ? 1u : 0) | (shift.Checked ? 4u : 0);
                Keys selected = (Keys)((KeyChoice)key.SelectedItem).Key;
                if (!GlobalShortcut.Valid(modifiers, selected))
                { error.Text = "Ctrl 또는 Alt를 하나 이상 선택해 주세요."; return; }
                string message = apply(modifiers, selected);
                if (message != null) { error.Text = message; error.ForeColor = Color.FromArgb(168, 77, 24); return; }
                DialogResult = DialogResult.OK; Close();
            };
        }
        sealed class KeyChoice
        {
            public Keys Key;
            public override string ToString() { return GlobalShortcut.KeyName(Key); }
        }
        void AddKey(Keys value) { key.Items.Add(new KeyChoice { Key = value }); }
        void SelectShortcut(uint modifiers, Keys value)
        {
            control.Checked = (modifiers & 2) != 0; alt.Checked = (modifiers & 1) != 0; shift.Checked = (modifiers & 4) != 0;
            for (int i = 0; i < key.Items.Count; i++) if (((KeyChoice)key.Items[i]).Key == value) { key.SelectedIndex = i; break; }
        }
    }

    sealed class StatusBadge : Form
    {
        readonly Label detail, shortcut, title;
        public StatusBadge(Action show, Action stop, Func<Screen> screen)
        {
            Text = "화면 보호 방지 켜짐"; FormBorderStyle = FormBorderStyle.FixedToolWindow;
            ControlBox = false; ShowInTaskbar = false; TopMost = true;
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
            Font = new Font("맑은 고딕", 9); BackColor = Color.FromArgb(19, 42, 39);
            ClientSize = new Size(360, 110); StartPosition = FormStartPosition.Manual;
            title = new Label { Text = "●  화면 보호 방지 켜짐", ForeColor = Color.FromArgb(113, 239, 189),
                Location = new Point(14, 10), Size = new Size(215, 25), Font = new Font(Font, FontStyle.Bold) };
            detail = new Label { Text = "입력이 없으면 30초마다 동작", ForeColor = Color.White,
                Location = new Point(14, 41), Size = new Size(240, 26) };
            shortcut = new Label { ForeColor = Color.FromArgb(185, 210, 202), Location = new Point(14, 78), Size = new Size(330, 23) };
            Button settings = new Button { Text = "설정", Location = new Point(266, 9), Size = new Size(80, 29) };
            Button end = new Button { Text = "중지", Location = new Point(266, 44), Size = new Size(80, 29) };
            settings.Click += delegate { show(); }; end.Click += delegate { stop(); show(); };
            Controls.AddRange(new Control[] { title, detail, shortcut, settings, end });
            MouseEventHandler drag = delegate(object sender, MouseEventArgs e)
            { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero); } };
            title.MouseDown += drag; detail.MouseDown += drag; shortcut.MouseDown += drag; MouseDown += drag;
            Shown += delegate
            {
                Rectangle area = screen().WorkingArea;
                Location = new Point(area.Right - Width - 18, area.Top + 18); EnsureOnScreen();
            };
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        { get { CreateParams p = base.CreateParams; p.ExStyle |= 0x08000000; return p; } }
        public void UpdateDetail(string text) { detail.Text = text; }
        public void SetPaused(bool paused) { title.Text = paused ? "●  화면 보호 방지 일시 대기" : "●  화면 보호 방지 켜짐"; Text = title.Text; }
        public void EnsureOnScreen()
        {
            foreach (Screen screen in Screen.AllScreens)
                if (screen.WorkingArea.Contains(Bounds)) return;
            Rectangle area = Screen.FromPoint(Location).WorkingArea;
            Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)), Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
        }
        public void UpdateShortcut(string text) { shortcut.Text = text; }
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    }

    sealed class KeeperForm : Form
    {
        readonly Color ink = Color.FromArgb(30, 45, 62), muted = Color.FromArgb(94, 109, 126), accent = Color.FromArgb(18, 120, 94);
        readonly NumericUpDown seconds;
        readonly ComboBox mode;
        readonly Label status, countdown, lastAction, note, shortcutLabel, shortcutHint;
        readonly Button start, stop;
        readonly NotifyIcon tray;
        readonly ToolStripMenuItem trayStart, trayStop;
        readonly System.Windows.Forms.Timer timer;
        readonly StatusBadge badge;
        readonly GlobalShortcut shortcut = new GlobalShortcut();
        readonly KeepAwakeEngine engine = new KeepAwakeEngine(new WindowsPlatform());
        bool closing, shortcutReady, editingShortcut;
        long displayedActions;
        readonly EventWaitHandle activation;
        bool running { get { return engine.Running; } }
        readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenKeeper", "settings.txt");

        public KeeperForm(EventWaitHandle activationEvent)
        {
            activation = activationEvent;
            Text = "화면지킴이"; ClientSize = new Size(540, 632); BackColor = Color.FromArgb(244, 247, 250);
            Font = new Font("맑은 고딕", 10); ForeColor = ink;
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
            Icon = CreateIcon();
            Label title = LabelAt("화면지킴이", 28, 24, 480, 40); title.Font = new Font(Font.FontFamily, 23, FontStyle.Bold);
            LabelAt("잠시 자리를 비워도 화면을 켜 둡니다.", 30, 71, 480, 27).ForeColor = muted;
            Panel card = new Panel { Location = new Point(28, 115), Size = new Size(484, 120), BackColor = Color.White };
            Controls.Add(card);
            status = new Label { Text = "●  꺼짐", Location = new Point(18, 16), Size = new Size(444, 29), ForeColor = muted, Font = new Font(Font, FontStyle.Bold) };
            countdown = new Label { Text = "시작을 누르면 화면 보호 방지를 켭니다.", Location = new Point(18, 49), Size = new Size(448, 27) };
            lastAction = new Label { Text = "동작 횟수 0회", Location = new Point(18, 82), Size = new Size(448, 23), ForeColor = muted, Font = new Font(Font.FontFamily, 9) };
            card.Controls.AddRange(new Control[] { status, countdown, lastAction });
            LabelAt("입력이 없을 때 동작 간격", 30, 257, 260, 28);
            seconds = new NumericUpDown { Minimum = 5, Maximum = 3600, Value = 30, Location = new Point(348, 254), Size = new Size(120, 29), TextAlign = HorizontalAlignment.Right, AccessibleName = "동작 간격(초)" };
            Controls.Add(seconds); LabelAt("초", 477, 258, 28, 26);
            LabelAt("동작 방식", 30, 309, 130, 28);
            mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(184, 305), Size = new Size(326, 30), AccessibleName = "동작 방식" };
            mode.Items.AddRange(new object[] { "마우스 미세 이동 후 복귀 (권장)", "현재 위치에서 왼쪽 한 번 클릭" }); mode.SelectedIndex = 0; Controls.Add(mode);
            note = LabelAt("미세 이동은 현재 위치에서 1픽셀 이동 후 돌아옵니다.", 30, 350, 480, 42); note.ForeColor = muted; note.Font = new Font(Font.FontFamily, 9);
            LabelAt("켜기·끄기 단축키", 30, 403, 170, 27);
            shortcutLabel = LabelAt("Ctrl+Alt+F8", 210, 403, 186, 27); shortcutLabel.Font = new Font(Font, FontStyle.Bold);
            Button changeShortcut = ButtonAt("변경", 420, 397, 90, 35);
            shortcutHint = LabelAt("다른 앱을 사용하거나 창을 숨겨도 작동합니다.", 30, 441, 480, 43); shortcutHint.Font = new Font(Font.FontFamily, 9); shortcutHint.ForeColor = muted;
            start = ButtonAt("시작", 28, 495, 234, 48); start.BackColor = accent; start.ForeColor = Color.White;
            stop = ButtonAt("중지", 278, 495, 234, 48); stop.Enabled = false;
            Button hide = ButtonAt("알림 영역으로 숨기기", 28, 561, 235, 38);
            Button exit = ButtonAt("종료", 278, 561, 234, 38);
            Label footer = LabelAt("실행 중 항상 위에 표시 · 최소화해도 켜짐 표시 유지", 30, 608, 390, 20); footer.Font = new Font(Font.FontFamily, 8); footer.ForeColor = muted;
            Button about = ButtonAt("정보", 429, 604, 82, 25); about.Font = new Font(Font.FontFamily, 9); about.Click += delegate { ShowAbout(); };
            badge = new StatusBadge(ShowMain, delegate { StopKeeping("중지했습니다."); }, delegate { return Screen.FromControl(this); });
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("설정 창 열기", null, delegate { ShowMain(); });
            trayStart = new ToolStripMenuItem("시작", null, delegate { StartKeeping(); });
            trayStop = new ToolStripMenuItem("중지", null, delegate { StopKeeping("중지했습니다."); }); trayStop.Enabled = false;
            menu.Items.Add(trayStart); menu.Items.Add(trayStop); menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("종료", null, delegate { Close(); });
            tray = new NotifyIcon { Icon = Icon, Text = "화면지킴이 · 꺼짐", Visible = true, ContextMenuStrip = menu };
            tray.DoubleClick += delegate { ShowMain(); };
            changeShortcut.Click += delegate { EditShortcut(); };
            start.Click += delegate { StartKeeping(); }; stop.Click += delegate { StopKeeping("중지했습니다."); };
            hide.Click += delegate { Hide(); }; exit.Click += delegate { Close(); };
            Resize += delegate { if (WindowState == FormWindowState.Minimized) Hide(); };
            mode.SelectedIndexChanged += delegate { UpdateNote(); };
            timer = new System.Windows.Forms.Timer { Interval = 250 }; timer.Tick += Tick; timer.Start();
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            LoadSettings(); UpdateNote();
        }
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!shortcutReady) { shortcutReady = true; shortcut.Bind(Handle); UpdateShortcutDisplay(); }
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (shortcutReady && !closing) { shortcut.Bind(Handle); UpdateShortcutDisplay(); }
        }
        protected override void OnHandleDestroyed(EventArgs e)
        { if (shortcut != null) shortcut.Dispose(); base.OnHandleDestroyed(e); }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0312 && shortcut != null && shortcut.Registered && message.WParam.ToInt32() == shortcut.Id)
            {
                if (!closing && !editingShortcut)
                { if (running) StopKeeping("단축키로 중지했습니다."); else StartKeeping(); }
                return;
            }
            base.WndProc(ref message);
        }
        void UpdateShortcutDisplay()
        {
            shortcutLabel.Text = shortcut.DisplayName;
            shortcutHint.Text = shortcut.Registered ? "다른 앱을 사용하거나 창을 숨겨도 작동합니다." : "단축키 등록 실패: 다른 앱과 겹칠 수 있습니다. 변경해 주세요.";
            shortcutHint.ForeColor = shortcut.Registered ? muted : Color.FromArgb(168, 77, 24);
            badge.UpdateShortcut(shortcut.Registered ? "켜기·끄기: " + shortcut.DisplayName : "단축키 사용 불가 · 설정에서 변경");
            trayStart.ShortcutKeyDisplayString = trayStop.ShortcutKeyDisplayString = shortcut.Registered ? shortcut.DisplayName : "";
        }
        void EditShortcut()
        {
            editingShortcut = true;
            try
            {
                using (ShortcutDialog dialog = new ShortcutDialog(shortcut, delegate(uint modifiers, Keys key)
                {
                    uint oldModifiers = shortcut.Modifiers; Keys oldKey = shortcut.Key;
                    if (!shortcut.TryApply(modifiers, key)) return "다른 앱이 사용 중이거나 등록할 수 없는 조합입니다.\n기존 단축키를 유지합니다. 다른 조합을 선택해 주세요.";
                    if (!SaveSettings())
                    {
                        bool restored = shortcut.TryApply(oldModifiers, oldKey); UpdateShortcutDisplay();
                        return restored ? "설정을 저장하지 못해 기존 단축키로 되돌렸습니다.\n설정 저장 위치의 쓰기 권한을 확인해 주세요." : "설정 저장과 이전 단축키 복원에 실패했습니다.\n현재 표시된 단축키를 확인하고 다시 적용해 주세요.";
                    }
                    UpdateShortcutDisplay();
                    return null;
                })) { dialog.TopMost = TopMost; dialog.ShowDialog(this); }
            }
            finally { editingShortcut = false; }
        }
        Label LabelAt(string text, int x, int y, int w, int h)
        { Label label = new Label { Text = text, Location = new Point(x, y), Size = new Size(w, h) }; Controls.Add(label); return label; }
        Button ButtonAt(string text, int x, int y, int w, int h)
        { Button b = new Button { Text = text, Location = new Point(x, y), Size = new Size(w, h), FlatStyle = FlatStyle.Flat, BackColor = Color.White, Cursor = Cursors.Hand }; b.FlatAppearance.BorderColor = Color.FromArgb(211, 220, 229); Controls.Add(b); return b; }
        static Icon CreateIcon()
        {
            using (Icon icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                return icon == null ? (Icon)SystemIcons.Application.Clone() : (Icon)icon.Clone();
        }
        void UpdateNote()
        {
            note.Text = mode.SelectedIndex == 0 ? "미세 이동은 현재 위치에서 1픽셀 이동 후 돌아옵니다." : "클릭 방식은 포인터 아래의 버튼이나 링크를 실행할 수 있습니다.";
            note.ForeColor = mode.SelectedIndex == 0 ? muted : Color.FromArgb(168, 77, 24);
        }
        void ShowAbout()
        {
            using (Form dialog = new Form())
            {
                dialog.Text = "화면지킴이 정보"; dialog.Font = Font; dialog.ClientSize = new Size(520, 430);
                dialog.StartPosition = FormStartPosition.CenterParent; dialog.ShowInTaskbar = false;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog; dialog.MaximizeBox = dialog.MinimizeBox = false; dialog.TopMost = TopMost;
                string license;
                using (Stream stream = typeof(Program).Assembly.GetManifestResourceStream("ScreenKeeper.LICENSE"))
                using (StreamReader reader = new StreamReader(stream)) license = reader.ReadToEnd();
                TextBox text = new TextBox { Multiline = true, ReadOnly = true, TabStop = false, ScrollBars = ScrollBars.Vertical,
                    Text = "화면지킴이 1.0.0\r\n\r\n단일 EXE · 개인정보 수집 및 네트워크 통신 없음\r\n\r\n" + license.Replace("\r\n", "\n").Replace("\n", "\r\n"),
                    Location = new Point(16, 16), Size = new Size(488, 352), BackColor = Color.White, Font = new Font("맑은 고딕", 9) };
                Button close = new Button { Text = "닫기", Location = new Point(390, 384), Size = new Size(114, 32), DialogResult = DialogResult.OK };
                dialog.Controls.AddRange(new Control[] { text, close }); dialog.AcceptButton = dialog.CancelButton = close; dialog.ShowDialog(this);
            }
        }
        public void ShowMain() { Show(); WindowState = FormWindowState.Normal; TopMost = running; Activate(); }
        void StartKeeping()
        {
            if (running) return;
            engine.Start((int)seconds.Value, mode.SelectedIndex == 1); displayedActions = 0;
            lastAction.Text = "동작 횟수 0회";
            RefreshRunState();
            if (!SaveSettings()) shortcutHint.Text = "설정 저장에 실패했습니다. 이번 실행에는 설정이 적용됩니다.";
        }
        void StopKeeping(string reason) { engine.Stop(reason); RefreshRunState(); }
        void RefreshRunState()
        {
            start.Enabled = trayStart.Enabled = !running; stop.Enabled = trayStop.Enabled = running;
            seconds.Enabled = mode.Enabled = !running; TopMost = running;
            countdown.Text = engine.Message;
            if (!running)
            { badge.Hide(); status.Text = "●  꺼짐"; status.ForeColor = muted; tray.Text = "화면지킴이 · 꺼짐"; return; }
            status.Text = engine.Paused ? "●  화면 보호 방지 일시 대기" : "●  화면 보호 방지 켜짐";
            status.ForeColor = accent; badge.SetPaused(engine.Paused);
            tray.Text = engine.Paused ? "화면지킴이 · 일시 대기" : "화면지킴이 · 화면 보호 방지 켜짐";
            badge.UpdateDetail(engine.Paused ? "입력과 전원 유지 요청을 잠시 멈췄습니다." : seconds.Value + "초 간격 · " + engine.ActionCount + "회 동작");
            if (!badge.Visible) badge.Show();
            if (displayedActions != engine.ActionCount)
            {
                displayedActions = engine.ActionCount;
                lastAction.Text = "동작 횟수 " + displayedActions + "회 · 마지막 동작 " + DateTime.Now.ToString("HH:mm:ss");
            }
        }
        void Tick(object sender, EventArgs args)
        {
            if (activation != null && activation.WaitOne(0) && !editingShortcut) ShowMain();
            engine.Tick(); if (running || stop.Enabled) RefreshRunState();
        }
        void OnUiThread(Action action)
        {
            if (closing || !IsHandleCreated) return;
            try { BeginInvoke((Action)delegate { if (!closing) action(); }); }
            catch (InvalidOperationException) { }
        }
        void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            OnUiThread(delegate
            {
                if (e.Reason == SessionSwitchReason.SessionLock) engine.SetLocked(true);
                if (e.Reason == SessionSwitchReason.SessionUnlock) engine.SetLocked(false);
                if (e.Reason == SessionSwitchReason.RemoteDisconnect || e.Reason == SessionSwitchReason.ConsoleDisconnect) engine.SetDisconnected(true);
                if (e.Reason == SessionSwitchReason.RemoteConnect || e.Reason == SessionSwitchReason.ConsoleConnect) engine.SetDisconnected(false);
                if (running) RefreshRunState();
            });
        }
        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            OnUiThread(delegate
            {
                if (e.Mode == PowerModes.Suspend) engine.SetSuspended(true);
                if (e.Mode == PowerModes.Resume) engine.SetSuspended(false);
                if (running) RefreshRunState();
            });
        }
        void OnDisplaySettingsChanged(object sender, EventArgs e) { OnUiThread(delegate { badge.EnsureOnScreen(); }); }
        void LoadSettings()
        {
            AppSettings settings = AppSettings.Load(settingsPath);
            seconds.Value = settings.IntervalSeconds; shortcut.Load(settings.Modifiers, settings.ShortcutKey);
            // 실행 상태와 클릭 방식은 저장하지 않습니다.
        }
        bool SaveSettings()
        {
            return new AppSettings { IntervalSeconds = (int)seconds.Value, Modifiers = shortcut.Modifiers, ShortcutKey = shortcut.Key }.Save(settingsPath);
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e); if (e.Cancel || closing) return;
            SaveSettings(); Cleanup();
        }
        void Cleanup()
        {
            if (closing) return; closing = true;
            if (timer != null) timer.Stop(); engine.Dispose(); shortcut.Dispose();
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            if (tray != null) { tray.Visible = false; tray.ContextMenuStrip.Dispose(); tray.Dispose(); }
            if (badge != null) badge.Dispose(); if (timer != null) timer.Dispose(); if (Icon != null) Icon.Dispose();
        }
        protected override void Dispose(bool disposing) { if (disposing) Cleanup(); base.Dispose(disposing); }
    }

    static class Program
    {
        [STAThread] static int Main(string[] args)
        {
            bool first;
            try
            {
                using (Mutex mutex = new Mutex(true, "Local\\ScreenKeeper.75F3E779", out first))
                using (EventWaitHandle activation = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\ScreenKeeper.Show.75F3E779"))
                {
                    if (!first) { activation.Set(); return 0; }
                    Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                    Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
                    {
                        Native.SetThreadExecutionState(Native.Continuous);
                        MessageBox.Show("오류로 화면지킴이를 종료합니다.\n" + e.Exception.Message, "화면지킴이", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        Application.Exit();
                    };
                    try { Native.SetProcessDPIAware(); using (KeeperForm form = new KeeperForm(activation)) Application.Run(form); }
                    finally { Native.SetThreadExecutionState(Native.Continuous); mutex.ReleaseMutex(); }
                    return 0;
                }
            }
            catch (Exception e)
            {
                Native.SetThreadExecutionState(Native.Continuous);
                MessageBox.Show("화면지킴이를 실행하지 못했습니다.\n" + e.Message, "화면지킴이", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
