using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;

namespace ImeKeyGuard
{
    enum ComposeState { Unknown, Composing, NotComposing }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, "ImeKeyGuard_SingleInstance_Mutex", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var app = new TrayApp())
                {
                    Application.Run();
                }
            }
        }
    }

    class TrayApp : IDisposable
    {
        const int WH_KEYBOARD_LL = 13;
        const int WH_MOUSE_LL = 14;
        const int WM_KEYDOWN = 0x0100;
        const int WM_SYSKEYDOWN = 0x0104;
        const int WM_LBUTTONDOWN = 0x0201;
        const int WM_RBUTTONDOWN = 0x0204;

        const uint VK_TAB = 0x09;
        const uint VK_OEM_1 = 0xBA;
        const uint VK_SHIFT = 0x10;
        const uint VK_CONTROL = 0x11;
        const uint VK_MENU = 0x12;

        static readonly IntPtr MARKER = (IntPtr)0x1EA00001;
        const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
        delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookExW")]
        static extern IntPtr SetWindowsHookEx(int idHook, Delegate lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")]
        static extern IntPtr GetKeyboardLayout(uint idThread);
        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")]
        static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
        [DllImport("user32.dll")]
        static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }
        [StructLayout(LayoutKind.Explicit)]
        struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public INPUTUNION u; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        NotifyIcon tray;
        IntPtr hook = IntPtr.Zero;
        IntPtr mouseHook = IntPtr.Zero;
        IntPtr winEventHook = IntPtr.Zero;
        LowLevelKeyboardProc hookProc;
        LowLevelMouseProc mouseProc;
        WinEventDelegate winEventProc;
        volatile bool enabled = true;
        volatile bool semicolonFeature = true;
        volatile bool composing = false;
        long lastSemiTick = 0;

        static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log");
        static readonly object LogLock = new object();

        static void Log(string msg)
        {
            try
            {
                lock (LogLock)
                {
                    FileInfo fi = new FileInfo(LogPath);
                    if (fi.Exists && fi.Length > 512 * 1024) File.Delete(LogPath);
                    File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + Environment.NewLine);
                }
            }
            catch { }
        }

        static string FgHkl()
        {
            IntPtr fg = GetForegroundWindow();
            uint pid;
            uint tid = GetWindowThreadProcessId(fg, out pid);
            return (GetKeyboardLayout(tid).ToInt64() & 0xFFFF).ToString("X4");
        }

        public TrayApp()
        {
            hookProc = new LowLevelKeyboardProc(HookCallback);
            mouseProc = new LowLevelMouseProc(MouseHookCallback);
            winEventProc = new WinEventDelegate(OnForegroundChanged);

            hook = SetWindowsHookEx(WH_KEYBOARD_LL, hookProc, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero)
            {
                MessageBox.Show("Failed to install keyboard hook. Error " + Marshal.GetLastWin32Error(),
                    "ImeKeyGuard", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, GetModuleHandle(null), 0);
            winEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);

            tray = new NotifyIcon();
            tray.Icon = SystemIcons.Shield;
            tray.Text = "ImeKeyGuard - ON";
            ContextMenuStrip menu = new ContextMenuStrip();

            ToolStripMenuItem miEnable = new ToolStripMenuItem("Enabled");
            miEnable.Checked = true;
            miEnable.Click += delegate
            {
                enabled = !enabled;
                miEnable.Checked = enabled;
                UpdateTrayText();
            };

            ToolStripMenuItem miSemi = new ToolStripMenuItem("Semicolon: commit + fullwidth ;");
            miSemi.Checked = true;
            miSemi.Click += delegate
            {
                semicolonFeature = !semicolonFeature;
                miSemi.Checked = semicolonFeature;
            };

            ToolStripMenuItem miExit = new ToolStripMenuItem("Exit");
            miExit.Click += delegate
            {
                Dispose();
                Application.Exit();
            };

            menu.Items.Add(miEnable);
            menu.Items.Add(miSemi);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miExit);
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            UpdateTrayText();
            Log("start hook=" + hook + " mouse=" + mouseHook + " winevent=" + winEventHook);
        }

        void UpdateTrayText()
        {
            if (tray != null) tray.Text = "ImeKeyGuard - " + (enabled ? "ON" : "OFF");
        }

        static bool IsChineseActive()
        {
            IntPtr fg = GetForegroundWindow();
            uint pid;
            uint tid = GetWindowThreadProcessId(fg, out pid);
            IntPtr hkl = GetKeyboardLayout(tid);
            return ((long)hkl & 0xFFFF) == 0x0804;
        }

        void OnForegroundChanged(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            composing = false;
        }

        IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN) composing = false;
            }
            return CallNextHookEx(mouseHook, nCode, wParam, lParam);
        }

        IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && enabled)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    KBDLLHOOKSTRUCT k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                    if (k.dwExtraInfo == MARKER)
                    {
                        return CallNextHookEx(hook, nCode, wParam, lParam);
                    }

                    uint vk = k.vkCode;
                    bool shift = (GetAsyncKeyState((int)VK_SHIFT) & 0x8000) != 0;
                    bool ctrl = (GetAsyncKeyState((int)VK_CONTROL) & 0x8000) != 0;
                    bool alt = (GetAsyncKeyState((int)VK_MENU) & 0x8000) != 0;

                    if (vk == VK_TAB || vk == VK_OEM_1)
                    {
                        Log(string.Format("key vk=0x{0:X2} cn={1} comp={2} sh={3} ct={4} al={5} en={6} fg={7}",
                            vk, IsChineseActive(), composing, shift, ctrl, alt, enabled, FgHkl()));
                    }

                    if (IsChineseActive())
                    {
                        if (!shift && !ctrl && !alt)
                        {
                            if (vk == VK_TAB && composing)
                            {
                                Log("  -> swallow TAB");
                                ThreadPool.QueueUserWorkItem(new WaitCallback(VerifyTab));
                                return (IntPtr)1;
                            }
                            if (vk == VK_OEM_1 && semicolonFeature && composing)
                            {
                                long now = Environment.TickCount;
                                long last = Interlocked.Read(ref lastSemiTick);
                                if (now - last > 80)
                                {
                                    Log("  -> swallow SEMI");
                                    Interlocked.Exchange(ref lastSemiTick, now);
                                    ThreadPool.QueueUserWorkItem(new WaitCallback(HandleSemicolon));
                                }
                                return (IntPtr)1;
                            }
                        }
                        TrackComposition(vk);
                    }
                    else
                    {
                        composing = false;
                    }
                }
            }
            return CallNextHookEx(hook, nCode, wParam, lParam);
        }

        void TrackComposition(uint vk)
        {
            if (vk >= 0x41 && vk <= 0x5A)
            {
                composing = true;
                return;
            }
            if (vk == 0x20 || vk == 0x0D || vk == 0x1B || (vk >= 0x31 && vk <= 0x39))
            {
                composing = false;
                return;
            }
            if (vk == 0xBC || vk == 0xBE || vk == 0xBA || vk == 0xBF || vk == 0xBD ||
                vk == 0xDE || vk == 0xDB || vk == 0xDD || vk == 0xDC || vk == 0xBB)
            {
                composing = false;
                return;
            }
        }

        void VerifyTab(object state)
        {
            ComposeState s = DetectComposing();
            Log("  verifyTab state=" + s);
            if (s == ComposeState.NotComposing)
            {
                composing = false;
                SendKey((ushort)VK_TAB, 0x0F, 0);
            }
            else if (s == ComposeState.Composing)
            {
                composing = true;
            }
        }

        void HandleSemicolon(object state)
        {
            ComposeState s = DetectComposing();
            Log("  semi state=" + s);
            if (s == ComposeState.NotComposing)
            {
                composing = false;
                SendKey((ushort)VK_OEM_1, 0x27, 0);
                return;
            }
            SendKey(0x20, 0x39, 0);
            Thread.Sleep(45);
            SendUnicode((char)0xFF1B);
            composing = false;
        }

        ComposeState DetectComposing()
        {
            try
            {
                Task<ComposeState> t = Task.Factory.StartNew<ComposeState>(ReadComposeState);
                if (t.Wait(300)) return t.Result;
            }
            catch { }
            return ComposeState.Unknown;
        }

        static ComposeState ReadComposeState()
        {
            try
            {
                AutomationElement el = AutomationElement.FocusedElement;
                if (el == null) return ComposeState.Unknown;

                string cls = "";
                try { cls = el.Current.ClassName ?? ""; } catch { }
                if (cls == "TermControl" || cls == "ConsoleWindowClass" || cls == "xterm-helper-textarea" ||
                    cls.IndexOf("TermControl", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    cls.IndexOf("ConsoleWindowClass", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Log("    uia skip terminal cls=" + cls);
                    return ComposeState.Unknown;
                }

                string text = null;
                object pat;
                if (el.TryGetCurrentPattern(TextPattern.Pattern, out pat))
                {
                    text = ((TextPattern)pat).DocumentRange.GetText(300);
                }
                else if (el.TryGetCurrentPattern(ValuePattern.Pattern, out pat))
                {
                    text = ((ValuePattern)pat).Current.Value;
                }
                if (text == null) return ComposeState.Unknown;
                if (text.Length > 40) text = text.Substring(text.Length - 40);
                string ct = "";
                try { ct = el.Current.ControlType.ProgrammaticName; } catch { }
                string tail = text.Length > 10 ? text.Substring(text.Length - 10) : text;
                Log("    uia ct=" + ct + " cls=" + cls + " len=" + text.Length + " tail=[" + tail + "]");
                return Regex.IsMatch(text, "[a-z\u0101-\u01ff']+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                    ? ComposeState.Composing
                    : ComposeState.NotComposing;
            }
            catch
            {
                return ComposeState.Unknown;
            }
        }

        static void SendKey(ushort vk, ushort scan, uint flags)
        {
            INPUT[] a = new INPUT[2];
            a[0].type = 1; a[0].u.ki.wVk = vk; a[0].u.ki.wScan = scan; a[0].u.ki.dwFlags = flags; a[0].u.ki.dwExtraInfo = MARKER;
            a[1].type = 1; a[1].u.ki.wVk = vk; a[1].u.ki.wScan = scan; a[1].u.ki.dwFlags = flags | 0x0002; a[1].u.ki.dwExtraInfo = MARKER;
            SendInput(2, a, Marshal.SizeOf(typeof(INPUT)));
        }

        static void SendUnicode(char c)
        {
            INPUT[] a = new INPUT[2];
            a[0].type = 1; a[0].u.ki.wVk = 0; a[0].u.ki.wScan = c; a[0].u.ki.dwFlags = 0x0004; a[0].u.ki.dwExtraInfo = MARKER;
            a[1].type = 1; a[1].u.ki.wVk = 0; a[1].u.ki.wScan = c; a[1].u.ki.dwFlags = 0x0004 | 0x0002; a[1].u.ki.dwExtraInfo = MARKER;
            SendInput(2, a, Marshal.SizeOf(typeof(INPUT)));
        }

        public void Dispose()
        {
            if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            if (mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
            if (winEventHook != IntPtr.Zero) { UnhookWinEvent(winEventHook); winEventHook = IntPtr.Zero; }
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
                tray = null;
            }
        }
    }
}

