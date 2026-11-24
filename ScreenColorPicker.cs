using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HoverExplorer
{
    // 屏幕取色器（全局取色）
    // 旧实现用全屏 TransparencyKey 覆盖层：透明区域会穿透点击，导致只看到一个贴在输入框旁的
    // 小图标、按下拖动没有反应。现改为：
    //   1) 小尺寸放大镜窗口跟随光标（WS_EX_NOACTIVATE，不夺焦点，且排除在屏幕捕获之外，
    //      这样 CopyFromScreen 采样不会被自己挡住）；
    //   2) 低级鼠标钩子吞掉按键（避免误点下层界面），按键状态用 GetAsyncKeyState 判断：
    //      按下开始拖动 → 松开立即取色并写回色值；Esc / 右键取消。
    internal static class ScreenColorPicker
    {
        // 返回 rgba(...) 文本；取消返回 null
        public static string Pick()
        {
            using (var f = new PickerForm())
            {
                f.ShowDialog();
                return f.Result;
            }
        }

        private class PickerForm : Form
        {
            private const int WH_MOUSE_LL = 14, WH_KEYBOARD_LL = 13;
            private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_RBUTTONDOWN = 0x0204;
            private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
            private const int VK_LBUTTON = 0x01, VK_ESCAPE = 0x1B;

            [DllImport("user32.dll", SetLastError = true)]
            private static extern IntPtr SetWindowsHookEx(int idHook, Delegate lpfn, IntPtr hMod, int dwThreadId);
            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool UnhookWindowsHookEx(IntPtr hhk);
            [DllImport("user32.dll")]
            private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
            [DllImport("user32.dll")]
            private static extern short GetAsyncKeyState(int vKey);
            [DllImport("user32.dll")]
            private static extern bool SetCursorPos(int x, int y);
            [DllImport("user32.dll")]
            private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
            [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
            private static extern IntPtr GetModuleHandle(string lpModuleName);

            private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
            private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

            [StructLayout(LayoutKind.Sequential)]
            private struct KBDLLHOOKSTRUCT { public int vkCode; public int scanCode; public int flags; public int time; public IntPtr dwExtraInfo; }

            private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

            private readonly int _grid = 15;              // 采样 15x15 像素
            private readonly int _cell;                   // 每格放大后的边长（随 DPI）
            private readonly int _pad;                    // 面板内边距
            private readonly Font _monoFont;

            private readonly Timer _timer;
            private LowLevelMouseProc _mouseProc;         // 必须保留引用，防止回调被 GC 回收
            private LowLevelKeyboardProc _keyProc;
            private IntPtr _hook;
            private IntPtr _keyHook;
            private Bitmap _gridBmp;
            private Point _pt;
            private Color _color;
            private bool _pressed;
            private bool _commit;
            private bool _cancel;

            public string Result { get; private set; }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE：不抢焦点
                    cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW：不进 Alt+Tab
                    return cp;
                }
            }

            public PickerForm()
            {
                _cell = Dpi.S(8);
                _pad = Dpi.S(10);
                _monoFont = new Font("Consolas", 9f);

                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                ShowInTaskbar = false;
                TopMost = true;
                Cursor = Cursors.Cross;
                DoubleBuffered = true;
                BackColor = Color.FromArgb(32, 32, 32);
                // 不显示操作提示，尺寸只按采样网格 + HEX 行计算
                int gridW = _grid * _cell;
                Size = new Size(gridW + _pad * 2, gridW + _pad * 2 + Dpi.S(30));

                // 取色界面不显示操作提示：仅保留采样网格与 HEX 值
                _pt = Cursor.Position;
                Sample();
                PositionAt(_pt);

                _timer = new Timer { Interval = 25 };
                _timer.Tick += delegate { OnTick(); };
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                // 让放大镜不出现在屏幕捕获结果里：采样点在光标处、窗口在其外侧，二者互不干扰
                // （HOVEREXPLORER_PICKER_CAPTURE=1 可关闭该行为，便于截图排查）
                if (Environment.GetEnvironmentVariable("HOVEREXPLORER_PICKER_CAPTURE") == null)
                {
                    try { SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE); } catch { }
                }
            }

            protected override void OnShown(EventArgs e)
            {
                base.OnShown(e);
                _mouseProc = MouseProc;
                _keyProc = KeyProc;
                try { _hook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(null), 0); } catch { }
                try { _keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc, GetModuleHandle(null), 0); } catch { }
                _timer.Start();
            }

            protected override void OnFormClosed(FormClosedEventArgs e)
            {
                _timer.Stop();
                _timer.Dispose();
                if (_hook != IntPtr.Zero) { try { UnhookWindowsHookEx(_hook); } catch { } _hook = IntPtr.Zero; }
                if (_keyHook != IntPtr.Zero) { try { UnhookWindowsHookEx(_keyHook); } catch { } _keyHook = IntPtr.Zero; }
                if (_gridBmp != null) { _gridBmp.Dispose(); _gridBmp = null; }
                _monoFont.Dispose();
                base.OnFormClosed(e);
            }

            // 吞掉左键/右键，避免拖动途中误触下层界面；同时用钩子记录按下/松开状态
            // （吞掉事件后 GetAsyncKeyState 可能读不到，钩子是最可靠的来源，轮询只作兜底）
            private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
            {
                if (nCode >= 0)
                {
                    int msg = wParam.ToInt32();
                    if (msg == WM_LBUTTONDOWN) { _pressed = true; return (IntPtr)1; }
                    if (msg == WM_LBUTTONUP) { if (_pressed) _commit = true; return (IntPtr)1; }
                    if (msg == WM_RBUTTONDOWN) { _cancel = true; return (IntPtr)1; }
                }
                return CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            // Esc 取消（键盘钩子保证快速点按也不会漏掉）
            private IntPtr KeyProc(int nCode, IntPtr wParam, IntPtr lParam)
            {
                if (nCode >= 0)
                {
                    int msg = wParam.ToInt32();
                    if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                    {
                        var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                        if (k.vkCode == VK_ESCAPE) { _cancel = true; return (IntPtr)1; }
                        // WASD 微调采样点（Shift 加速 10px），方便精确取到 1px 的目标色
                        int dx = 0, dy = 0;
                        switch (k.vkCode)
                        {
                            case 0x57: dy = -1; break;   // W
                            case 0x41: dx = -1; break;   // A
                            case 0x53: dy = 1; break;    // S
                            case 0x44: dx = 1; break;    // D
                        }
                        if (dx != 0 || dy != 0)
                        {
                            int step = (GetAsyncKeyState(0x10) & 0x8000) != 0 ? 10 : 1;   // VK_SHIFT
                            Nudge(dx * step, dy * step);
                            return (IntPtr)1;
                        }
                    }
                }
                return CallNextHookEx(_keyHook, nCode, wParam, lParam);
            }

            private static bool IsDown(int vk)
            {
                return (GetAsyncKeyState(vk) & 0x8000) != 0;
            }

            private void OnTick()
            {
                if (_cancel) { Result = null; Close(); return; }

                Point p = Cursor.Position;
                if (p != _pt)
                {
                    _pt = p;
                    Sample();
                    PositionAt(p);
                    Invalidate();
                }

                // 按下开始拖动，松开即取色（松手点就是取色点）
                if (_commit)
                {
                    _commit = false;
                    _pressed = false;
                    Sample();
                    Result = "rgba(" + _color.R + "," + _color.G + "," + _color.B + ",255)";
                    Close();
                    return;
                }
                // 钩子安装失败时退回轮询按键状态
                if (_hook == IntPtr.Zero)
                {
                    if (IsDown(VK_LBUTTON)) { _pressed = true; return; }
                    if (_pressed)
                    {
                        _pressed = false;
                        Sample();
                        Result = "rgba(" + _color.R + "," + _color.G + "," + _color.B + ",255)";
                        Close();
                    }
                }
            }

            private void Nudge(int dx, int dy)
            {
                Rectangle vs = SystemInformation.VirtualScreen;
                _pt = new Point(
                    Math.Max(vs.Left, Math.Min(vs.Right - 1, _pt.X + dx)),
                    Math.Max(vs.Top, Math.Min(vs.Bottom - 1, _pt.Y + dy)));
                try { SetCursorPos(_pt.X, _pt.Y); } catch { }
                Sample();
                PositionAt(_pt);
                Invalidate();
            }

            private void PositionAt(Point p)
            {
                Rectangle vs = SystemInformation.VirtualScreen;
                int x = p.X + Dpi.S(24), y = p.Y + Dpi.S(24);
                if (x + Width > vs.Right) x = p.X - Width - Dpi.S(24);
                if (y + Height > vs.Bottom) y = p.Y - Height - Dpi.S(24);
                if (x < vs.Left) x = vs.Left;
                if (y < vs.Top) y = vs.Top;
                Location = new Point(x, y);
            }

            private void Sample()
            {
                Rectangle vs = SystemInformation.VirtualScreen;
                int half = _grid / 2;
                int cx = Math.Max(vs.Left, Math.Min(_pt.X, vs.Right - 1));
                int cy = Math.Max(vs.Top, Math.Min(_pt.Y, vs.Bottom - 1));
                int sx = Math.Max(vs.Left, Math.Min(cx - half, vs.Right - _grid));
                int sy = Math.Max(vs.Top, Math.Min(cy - half, vs.Bottom - _grid));

                if (_gridBmp == null) _gridBmp = new Bitmap(_grid, _grid, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(_gridBmp))
                    g.CopyFromScreen(sx, sy, 0, 0, new Size(_grid, _grid), CopyPixelOperation.SourceCopy);

                int gx = Math.Max(0, Math.Min(_grid - 1, cx - sx));
                int gy = Math.Max(0, Math.Min(_grid - 1, cy - sy));
                _color = _gridBmp.GetPixel(gx, gy);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.None;
                using (var b = new SolidBrush(Color.FromArgb(245, 32, 32, 32))) g.FillRectangle(b, ClientRectangle);
                using (var p = new Pen(Color.FromArgb(90, 255, 255, 255)))
                    g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);

                int gx0 = _pad, gy0 = _pad, gw = _grid * _cell;
                if (_gridBmp != null)
                {
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(_gridBmp, new Rectangle(gx0, gy0, gw, gw));
                }

                // 中心格（取色点）：黑底白框，深浅背景都看得清
                int cx = gx0 + (_grid / 2) * _cell, cy = gy0 + (_grid / 2) * _cell;
                using (var p = new Pen(Color.Black, 1)) g.DrawRectangle(p, cx - 1, cy - 1, _cell + 1, _cell + 1);
                using (var p = new Pen(Color.White, Math.Max(1, Dpi.S(2)))) g.DrawRectangle(p, cx, cy, _cell - 1, _cell - 1);

                int ty = gy0 + gw + Dpi.S(6);
                int sw = Dpi.S(16);
                using (var b = new SolidBrush(_color)) g.FillRectangle(b, gx0, ty + Dpi.S(2), sw, sw);
                using (var p = new Pen(Color.FromArgb(120, 255, 255, 255))) g.DrawRectangle(p, gx0, ty + Dpi.S(2), sw, sw);
                using (var b = new SolidBrush(Color.White))
                    g.DrawString("#" + _color.R.ToString("X2") + _color.G.ToString("X2") + _color.B.ToString("X2"),
                        _monoFont, b, gx0 + sw + Dpi.S(8), ty);

            }
        }
    }
}
