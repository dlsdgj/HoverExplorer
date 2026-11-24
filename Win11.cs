using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HoverExplorer
{
    // Win11 窗口外观：暗色标题区 + 圆角 + 描边色（dwmapi）
    // 亚克力(Mica/Acrylic)需控件全透明才能透出，当前 Label/Panel 不透明故未启用
    internal static class Win11
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);

        [DllImport("user32.dll", EntryPoint = "SetClassLong")]
        static extern uint SetClassLong32(IntPtr hWnd, int nIndex, uint dwNewLong);
        [DllImport("user32.dll", EntryPoint = "SetClassLongPtr")]
        static extern IntPtr SetClassLong64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        const int GCL_STYLE = -26;
        const int CS_DROPSHADOW = 0x00020000;

        static IntPtr SetClassLongPtrSafe(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            return IntPtr.Size == 8 ? SetClassLong64(hWnd, nIndex, dwNewLong) : (IntPtr)SetClassLong32(hWnd, nIndex, (uint)dwNewLong.ToInt32());
        }

        static void Set(IntPtr h, int attr, int v) { DwmSetWindowAttribute(h, attr, ref v, 4); }

        // Environment.OSVersion 在无 manifest 的程序里会说谎（固定报告 Win8 6.2.9200），
        // 导致 Win11 圆角判定 Build<22000 永远成立而从未生效；RtlGetVersion 不受 manifest 影响返回真实 build
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OSVERSIONINFOW
        {
            public int dwOSVersionInfoSize;
            public int dwMajorVersion;
            public int dwMinorVersion;
            public int dwBuildNumber;
            public int dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szCSDVersion;
        }

        [DllImport("ntdll.dll")]
        private static extern int RtlGetVersion(ref OSVERSIONINFOW vi);

        private static int _realBuild;

        static int Build
        {
            get
            {
                if (_realBuild == 0)
                {
                    var vi = new OSVERSIONINFOW { szCSDVersion = new string('\0', 128) };
                    vi.dwOSVersionInfoSize = Marshal.SizeOf(typeof(OSVERSIONINFOW));
                    if (RtlGetVersion(ref vi) == 0 && vi.dwBuildNumber > 0) _realBuild = vi.dwBuildNumber;
                    else _realBuild = Environment.OSVersion.Version.Build;
                }
                return _realBuild;
            }
        }

        public static void Apply(IntPtr handle, bool dark, Color? borderColor = null)
        {
            if (handle == IntPtr.Zero) return;
            SetClassLongPtrSafe(handle, GCL_STYLE, (IntPtr)CS_DROPSHADOW);
            Set(handle, 20, dark ? 1 : 0);
            ApplyCornerPref(handle);
        }

        // 分层窗口自绘阴影：清除 CS_DROPSHADOW 类样式，避免系统直角阴影与自绘圆角不协调
        public static void RemoveDropShadow(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            SetClassLongPtrSafe(handle, GCL_STYLE, IntPtr.Zero);
        }

        // 分层窗口：禁用 DWM 非客户区渲染+边框（去掉 DWM 白色描边）
        public static void RemoveNcBorder(IntPtr handle)
        {
            if (handle == IntPtr.Zero || Build < 22000) return;
            Set(handle, 2, 2);    // DWMWA_NCRENDERING_POLICY = DWMNCRP_DISABLED（原误传 1=ALLOWED）
            Set(handle, 34, -2);  // DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE (0xFFFFFFFE)
        }

        // 对话框用：只要暗色模式与圆角，不加 CS_DROPSHADOW（否则下/右会出现硬边投影）
        public static void ApplyRounded(IntPtr handle, bool dark)
        {
            if (handle == IntPtr.Zero) return;
            Set(handle, 20, dark ? 1 : 0);
            ApplyCornerPref(handle);
        }

        // 边角偏好跟随设置：直角=DONOTROUND（去掉 Win11 自动圆角带来的系统描边/阴影感）；
        // 圆角=ROUND（实测 ROUNDSMALL 在无边框工具窗上不渲染）
        public static void ApplyCornerPref(IntPtr handle)
        {
            if (handle == IntPtr.Zero || Build < 22000) return;
            Set(handle, 33, Config.WindowCorners == 1 ? 2 : 0);
        }

        // 卡片专用：无论全局边角设置如何都强制直角——DWM 裁角会切碎卡片自有的
        // 硬描边/全高色条/硬阴影，圆角模式下卡片显示问题多，一律保持直角形态
        public static void ForceSquareCorners(IntPtr handle)
        {
            if (handle == IntPtr.Zero || Build < 22000) return;
            Set(handle, 33, 0);
        }

        // ===== 圆角模式粗描边：非客户区实现 =====
        // 在客户区内画描边会被铺满整个客户区的子控件盖住（面板行/音频条都顶到 0,0），
        // 改为 WM_NCCALCSIZE 把客户区四周内缩 FramePad 像素、WM_NCPAINT 用描边色填环：
        // 子控件自动让位、描边永远在最外层，DWM 圆角裁切让描边自带圆角，直角模式零改动。
        // 用法：窗体 WndProc 最前面 if (Win11.WndNcBorder(Handle, ref m)) return;
        public static int FramePad { get { return Config.WindowCorners == 1 ? 3 : 0; } }

        [StructLayout(LayoutKind.Sequential)]
        private struct NcRect { public int L, T, R, B; }

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr h, out NcRect r);
        [DllImport("user32.dll")]
        static extern bool GetClientRect(IntPtr h, out NcRect r);
        [DllImport("user32.dll")]
        static extern IntPtr GetWindowDC(IntPtr h);
        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("user32.dll")]
        static extern IntPtr DefWindowProc(IntPtr h, int msg, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll")]
        static extern bool RedrawWindow(IntPtr h, IntPtr rc, IntPtr rgn, uint flags);
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);

        // 返回 true 表示消息已处理。border 传 null 用全局描边色（卡片传配色边框色）
        public static bool WndNcBorder(IntPtr h, ref Message m, Func<Color> border)
        {
            return WndNcBorder(h, ref m, border, null);
        }

        // topBorder 用于顶部/两侧上段的描边色：标题栏扩到边框时传标题色，底边仍用 border
        public static bool WndNcBorder(IntPtr h, ref Message m, Func<Color> border, Func<Color> topBorder)
        {
            if (Config.WindowCorners != 1 || h == IntPtr.Zero) return false;
            if (m.Msg == 0x83 && m.WParam != IntPtr.Zero)   // WM_NCCALCSIZE(wParam=TRUE)：客户区内缩
            {
                DefWindowProc(h, m.Msg, m.WParam, m.LParam);
                var r = (NcRect)Marshal.PtrToStructure(m.LParam, typeof(NcRect));
                int p = FramePad;
                r.L += p; r.T += p; r.R -= p; r.B -= p;
                Marshal.StructureToPtr(r, m.LParam, false);
                m.Result = IntPtr.Zero;
                return true;
            }
            if (m.Msg == 0x85)   // WM_NCPAINT：非客户环整圈填描边色（角部由 DWM 圆角裁掉）
            {
                DrawNcBorder(h, border, topBorder);
                m.Result = IntPtr.Zero;
                return true;
            }
            return false;
        }

        public static bool WndNcBorder(IntPtr h, ref Message m) { return WndNcBorder(h, ref m, null); }

        // 真实客户区：圆角模式 WM_NCCALCSIZE 把客户区四周内缩 FramePad，WinForms 的
        // ClientSize 并不知情（读写都差 2*FramePad，底部内容被描边裁掉），需要拿真实值核对。
        // 句柄未建或调用失败返回 Size.Empty，由调用方回退 WinForms 自己的值
        public static Size RealClient(IntPtr h)
        {
            if (h == IntPtr.Zero) return Size.Empty;
            NcRect r;
            return GetClientRect(h, out r) ? new Size(r.R - r.L, r.B - r.T) : Size.Empty;
        }

        private static void DrawNcBorder(IntPtr h, Func<Color> border, Func<Color> topBorder)
        {
            var col = Config.Border;
            if (border != null)
            {
                try { var c = border(); if (c.A > 0) col = c; } catch { }
            }
            var topCol = col;
            if (topBorder != null)
            {
                try { var c = topBorder(); if (c.A > 0) topCol = c; } catch { }
            }
            IntPtr dc = GetWindowDC(h);
            if (dc == IntPtr.Zero) return;
            try
            {
                NcRect r;
                GetWindowRect(h, out r);
                int w = r.R - r.L, hh = r.B - r.T, p = FramePad;
                using (var g = Graphics.FromHdc(dc))
                {
                    using (var b = new SolidBrush(topCol))
                        g.FillRectangle(b, 0, 0, w, p);
                    using (var b = new SolidBrush(col))
                    {
                        g.FillRectangle(b, 0, hh - p, w, p);
                        g.FillRectangle(b, 0, p, p, Math.Max(0, hh - p * 2));
                        g.FillRectangle(b, w - p, p, p, Math.Max(0, hh - p * 2));
                    }
                }
            }
            finally { ReleaseDC(h, dc); }
        }

        // 风格切换时让已存在窗口重算非客户区（FRAMECHANGED 重跑 NCCALCSIZE）并整帧重画
        public static void RefreshNc(IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0,
                0x0001 /*SWP_NOSIZE*/ | 0x0002 /*SWP_NOMOVE*/ | 0x0004 /*SWP_NOZORDER*/ |
                0x0010 /*SWP_NOACTIVATE*/ | 0x0020 /*SWP_FRAMECHANGED*/);
            RedrawWindow(h, IntPtr.Zero, IntPtr.Zero,
                0x0400 /*RDW_FRAME*/ | 0x0001 /*RDW_INVALIDATE*/ | 0x0100 /*RDW_UPDATENOW*/ | 0x0080 /*RDW_ALLCHILDREN*/);
        }

        // 控件滚动条深色化：uxtheme 非公开入口 AllowDarkModeForWindow(#133) + DarkMode_Explorer 主题
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

        [DllImport("uxtheme.dll", EntryPoint = "#133")]
        static extern bool AllowDarkModeForWindow(IntPtr hWnd, bool allow);

        public static void ApplyScrollTheme(Control c, bool dark)
        {
            if (c == null) return;
            try
            {
                var h = c.Handle;
                if (h == IntPtr.Zero) return;
                AllowDarkModeForWindow(h, dark);
                SetWindowTheme(h, dark ? "DarkMode_Explorer" : "Explorer", null);
            }
            catch { }
        }
    }
}