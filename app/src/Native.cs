using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace MouseTranslate
{
    /// <summary>Win32 互操作：全局鼠标钩子、光标、键盘注入、窗口样式、DPI。</summary>
    internal static class Native
    {
        public const int WH_MOUSE_LL = 14;
        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONDOWN = 0x0204;

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const byte VK_CONTROL = 0x11;
        public const byte VK_C = 0x43;
        public const byte VK_ESC = 0x1B;

        public const int SWP_NOSIZE = 0x0001;
        public const int SWP_NOZORDER = 0x0004;
        public const int SWP_NOACTIVATE = 0x0010;

        public const int WM_NCLBUTTONDOWN = 0x00A1;
        public const int HTCAPTION = 2;

        // ---- 落点过滤 / 权限检测（F1） ----
        public const uint GA_ROOT = 2;
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint PROCESS_QUERY_INFORMATION = 0x0400;
        public const uint TOKEN_QUERY = 0x0008;
        public const int TokenElevation = 20;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        // ---- 分层窗口（给徽标做真透明 + 抗锯齿） ----
        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int cx; public int cy; public SIZE(int w, int h) { cx = w; cy = h; } }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        public const int ULW_ALPHA = 0x00000002;
        public const byte AC_SRC_ALPHA = 0x01;
        public const int WS_EX_LAYERED = 0x00080000;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
            ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hDC);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);


        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);


        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);


        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        /// <summary>剪贴板序列号：每次剪贴板内容变化都会变化，用来判定 Ctrl+C 是否真的复制到了新内容。</summary>
        [DllImport("user32.dll")]
        public static extern uint GetClipboardSequenceNumber();

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(POINT Point);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentProcessId();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass,
            IntPtr TokenInformation, int TokenInformationLength, out int ReturnLength);

        /// <summary>
        /// 查询某个进程是否以管理员（高完整性）身份运行。
        /// 先用 PROCESS_QUERY_LIMITED_INFORMATION 打开（跨完整性级别也能读），
        /// 不行再用 PROCESS_QUERY_INFORMATION 兜一次。
        /// 返回 false 表示"查不到"（受保护进程 / 权限不够等），调用方自行决定是否保守跳过。
        /// </summary>
        public static bool TryGetProcessElevation(int pid, out bool elevated)
        {
            elevated = false;
            if (pid <= 0) return false;
            if (TryGetProcessElevationWith(pid, PROCESS_QUERY_LIMITED_INFORMATION, out elevated)) return true;
            return TryGetProcessElevationWith(pid, PROCESS_QUERY_INFORMATION, out elevated);
        }

        private static bool TryGetProcessElevationWith(int pid, uint access, out bool elevated)
        {
            elevated = false;
            IntPtr hProc = IntPtr.Zero;
            IntPtr hToken = IntPtr.Zero;
            IntPtr buf = IntPtr.Zero;
            try
            {
                hProc = OpenProcess(access, false, pid);
                if (hProc == IntPtr.Zero) return false;
                if (!OpenProcessToken(hProc, TOKEN_QUERY, out hToken) || hToken == IntPtr.Zero) return false;
                buf = Marshal.AllocHGlobal(4);
                Marshal.WriteInt32(buf, 0);
                int ret;
                if (!GetTokenInformation(hToken, TokenElevation, buf, 4, out ret)) return false;
                elevated = Marshal.ReadInt32(buf) != 0;
                return true;
            }
            catch { return false; }
            finally
            {
                if (buf != IntPtr.Zero) { try { Marshal.FreeHGlobal(buf); } catch { } }
                if (hToken != IntPtr.Zero) { try { CloseHandle(hToken); } catch { } }
                if (hProc != IntPtr.Zero) { try { CloseHandle(hProc); } catch { } }
            }
        }


        [DllImport("user32.dll")]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);


        [DllImport("user32.dll")]
        public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern IntPtr GetWindowLong32(IntPtr hWnd, int nIndex);


        [DllImport("user32.dll")]
        public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);


        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);


        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);


        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);


        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetProcessDPIAware();


        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr hIcon);


        [DllImport("user32.dll")]
        public static extern uint GetDpiForSystem();


        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        public const int VK_MENU = 0x12;    // Alt
        public const int VK_SHIFT = 0x10;
        public const int VK_LBUTTON = 0x01;

        /// <summary>Alt 是否按住（同时兼容左右 Alt）。</summary>
        public static bool IsAltDown()
        {
            return (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
        }

        public static bool IsLeftButtonDown()
        {
            return (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        }

        public static IntPtr GetWindowLongSafe(IntPtr hWnd, int nIndex)
        {
            return IntPtr.Size == 8 ? GetWindowLongPtr(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);
        }

        public static IntPtr SetWindowLongSafe(IntPtr hWnd, int nIndex, IntPtr value)
        {
            return IntPtr.Size == 8 ? SetWindowLongPtr(hWnd, nIndex, value) : SetWindowLong32(hWnd, nIndex, value);
        }

        /// <summary>给窗口加上「不抢焦点 / 不在任务栏 / 置顶」的扩展样式。</summary>
        public static void MakeOverlay(IntPtr hWnd, bool noActivate)
        {
            try
            {
                IntPtr ex = GetWindowLongSafe(hWnd, GWL_EXSTYLE);
                int v = ex.ToInt32();
                v |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW;
                if (noActivate) v |= WS_EX_NOACTIVATE;
                SetWindowLongSafe(hWnd, GWL_EXSTYLE, new IntPtr(v));
            }
            catch { }
        }

        /// <summary>拖拽窗口的常用写法：把鼠标消息转成「点住标题栏」。</summary>
        public static void DragWindow(IntPtr hWnd)
        {
            try
            {
                ReleaseCapture();
                SendMessage(hWnd, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
            }
            catch { }
        }

        public static Point GetCursorPosition()
        {
            POINT p;
            if (GetCursorPos(out p)) return new Point(p.X, p.Y);
            return Point.Empty;
        }

        public static void SendCtrlC()
        {
            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            keybd_event(VK_C, 0, 0, UIntPtr.Zero);
            keybd_event(VK_C, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        /// <summary>截取屏幕上的一块矩形区域。</summary>
        public static Bitmap Capture(int x, int y, int w, int h)
        {
            if (w <= 0 || h <= 0) return null;
            try
            {
                Bitmap bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                }
                return bmp;
            }
            catch { return null; }
        }
    }
}
