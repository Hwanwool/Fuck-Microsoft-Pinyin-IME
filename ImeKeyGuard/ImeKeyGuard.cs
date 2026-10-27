using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;

namespace ImeKeyGuard
{
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
        const int WM_KEYDOWN = 0x0100;
        const int WM_SYSKEYDOWN = 0x0104;

        const uint VK_TAB = 0x09;
        const uint VK_OEM_1 = 0xBA;
        const uint VK_SHIFT = 0x10;
        const uint VK_CONTROL = 0x11;
        const uint VK_MENU = 0x12;

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

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
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
        LowLevelKeyboardProc hookProc;
        volatile bool enabled = true;
        volatile bool semicolonFeature = true;
        volatile bool composing = false;
        long lastSemiTick = 0;

        public TrayApp()
        {
            hookProc = new LowLevelKeyboardProc(HookCallback);
            hook = SetWindowsHookEx(WH_KEYBOARD_LL, hookProc, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero)
            {
                MessageBox.Show("Failed to install keyboard hook. Error " + Marshal.GetLastWin32Error(),
                    "ImeKeyGuard", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

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

        IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && enabled)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    KBDLLHOOKSTRUCT k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                    uint vk = k.vkCode;
                    bool shift = (GetAsyncKeyState((int)VK_SHIFT) & 0x8000) != 0;
                    bool ctrl = (GetAsyncKeyState((int)VK_CONTROL) & 0x8000) != 0;
                    bool alt = (GetAsyncKeyState((int)VK_MENU) & 0x8000) != 0;

                    if (IsChineseActive())
                    {
                        TrackComposition(vk);

                        if (!shift && !ctrl && !alt)
                        {
                            if (vk == VK_TAB)
                            {
                                return (IntPtr)1;
                            }
                            if (vk == VK_OEM_1 && semicolonFeature)
                            {
                                long now = Environment.TickCount;
                                long last = Interlocked.Read(ref lastSemiTick);
                                if (now - last > 80)
                                {
                                    Interlocked.Exchange(ref lastSemiTick, now);
                                    ThreadPool.QueueUserWorkItem(new WaitCallback(HandleSemicolon));
                                }
                                return (IntPtr)1;
                            }
                        }
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

        void HandleSemicolon(object state)
        {
            bool isComposing = DetectComposing();
            if (isComposing)
            {
                SendKey(0x20, 0x39, 0);
                Thread.Sleep(45);
            }
            SendUnicode((char)0xFF1B);
            composing = false;
        }

        bool DetectComposing()
        {
            try
            {
                Task<bool> t = Task.Factory.StartNew<bool>(IsComposingByUia);
                if (t.Wait(300)) return t.Result;
            }
            catch { }
            return composing;
        }

        static bool IsComposingByUia()
        {
            try
            {
                AutomationElement el = AutomationElement.FocusedElement;
                if (el == null) return false;
                string text = null;
                object pat;
                if (el.TryGetCurrentPattern(TextPattern.Pattern, out pat))
                {
                    TextPattern tp = (TextPattern)pat;
                    text = tp.DocumentRange.GetText(300);
                }
                else if (el.TryGetCurrentPattern(ValuePattern.Pattern, out pat))
                {
                    text = ((ValuePattern)pat).Current.Value;
                }
                if (text == null) return false;
                if (text.Length > 40) text = text.Substring(text.Length - 40);
                return Regex.IsMatch(text, "[a-z\u0101-\u01ff']+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            catch
            {
                return false;
            }
        }

        static void SendKey(ushort vk, ushort scan, uint flags)
        {
            INPUT[] a = new INPUT[2];
            a[0].type = 1; a[0].u.ki.wVk = vk; a[0].u.ki.wScan = scan; a[0].u.ki.dwFlags = flags;
            a[1].type = 1; a[1].u.ki.wVk = vk; a[1].u.ki.wScan = scan; a[1].u.ki.dwFlags = flags | 0x0002;
            SendInput(2, a, Marshal.SizeOf(typeof(INPUT)));
        }

        static void SendUnicode(char c)
        {
            INPUT[] a = new INPUT[2];
            a[0].type = 1; a[0].u.ki.wVk = 0; a[0].u.ki.wScan = c; a[0].u.ki.dwFlags = 0x0004;
            a[1].type = 1; a[1].u.ki.wVk = 0; a[1].u.ki.wScan = c; a[1].u.ki.dwFlags = 0x0004 | 0x0002;
            SendInput(2, a, Marshal.SizeOf(typeof(INPUT)));
        }

        public void Dispose()
        {
            if (hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(hook);
                hook = IntPtr.Zero;
            }
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
                tray = null;
            }
        }
    }
}
