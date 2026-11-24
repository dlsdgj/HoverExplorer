using System;
using System.Runtime.InteropServices;
using System.Text;

namespace HoverExplorer
{
    // Windows shell 原生右键菜单：通过 IContextMenu COM 接口弹出资源管理器同款菜单
    internal static class ShellContextMenu
    {
        [StructLayout(LayoutKind.Sequential)]
        struct CMINVOKECOMMANDINFOEX
        {
            public int cbSize;
            public int fMask;          // CMIC_MASK_UNICODE = 0x4000
            public IntPtr hwnd;
            public IntPtr lpVerb;      // MAKEINTRESOURCE 偏移（指针值=偏移数字）
            public IntPtr lpParameters;
            public IntPtr lpDirectory;
            public int nShow;
            public int dwHotKey;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpVerbW;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpParametersW;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpDirectoryW;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpTitleW;
            // ptInvoke 不可省：EX 版结构必须完整，否则 cbSize 比 shell 期望小 8 字节
            // → 校验失败返回 E_INVALIDARG(0x80070057)（实测踩坑）
            public POINT ptInvoke;
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214E4-0000-0000-C000-000000000046")]
        interface IContextMenu
        {
            [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
            [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
            // pszName 改 IntPtr：GCS_VERBW 需要 Unicode 缓冲，统一手动封送
            [PreserveSig] int GetCommandString(IntPtr idCmd, uint uType, IntPtr pwReserved, IntPtr pszName, uint cchMax);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214E6-0000-0000-C000-000000000046")]
        interface IShellFolder
        {
            [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, ref uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
            [PreserveSig] int EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);
            [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, [In] ref Guid riid, out IntPtr ppv);
            [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, [In] ref Guid riid, out IntPtr ppv);
            [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
            [PreserveSig] int CreateViewObject(IntPtr hwnd, [In] ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
            [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, [In] ref Guid riid, ref uint rgfReserved, out IntPtr ppv);
            [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
            [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHParseDisplayName([MarshalAs(UnmanagedType.LPWStr)] string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);
        [DllImport("shell32.dll")]
        static extern int SHBindToParent(IntPtr pidl, [In] ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);
        [DllImport("user32.dll")]
        static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll")]
        static extern bool DestroyMenu(IntPtr hMenu);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hwnd, IntPtr prcRect);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);
        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

        const uint CMF_NORMAL = 0;
        const uint TPM_RETURNCMD = 0x0100;
        const uint TPM_LEFTALIGN = 0x0000;
        const uint TPM_TOPALIGN = 0x0000;
        const uint MF_SEPARATOR = 0x0800;
        const uint MF_STRING = 0x0000;
        const uint CMD_UNPIN = 0x8000;
        const uint CMD_ADDPALETTE = 0x8001;
        const uint CMD_EXTRA = 0x8002;
        static readonly Guid IID_IContextMenu = new Guid("000214E4-0000-0000-C000-000000000046");
        static readonly Guid IID_IShellFolder = new Guid("000214E6-0000-0000-C000-000000000046");

        // 专用隐藏辅助窗口做菜单 owner：卡片是 MA_NOACTIVATE（Show 不激活 + 点击不激活），
        // SetForegroundWindow(卡片) 受前台权限限制常静默失败 → 菜单点击无效。
        // KB135788 模式：helper 窗口（普通窗口，可激活）前台化后 TrackPopupMenu 才能正常交互
        private static System.Windows.Forms.NativeWindow _menuOwner;
        private static IntPtr MenuOwnerHwnd()
        {
            if (_menuOwner == null)
            {
                _menuOwner = new System.Windows.Forms.NativeWindow();
                _menuOwner.CreateHandle(new System.Windows.Forms.CreateParams { Caption = "HE_ShellMenuOwner", X = -32000, Y = -32000, Width = 1, Height = 1 });
            }
            return _menuOwner.Handle;
        }

        public static void Show(string path, IntPtr hwndOwner, Action onUnpin, Action onAddPalette = null,
            string extraText = null, Action extraAction = null)
        {
            try
            {
                var iidSF = IID_IShellFolder;
                var iidCM = IID_IContextMenu;
                uint attr;
                IntPtr pidlFull;
                if (SHParseDisplayName(path, IntPtr.Zero, out pidlFull, 0, out attr) != 0 || pidlFull == IntPtr.Zero) return;
                IntPtr sfPtr;
                IntPtr pidlLast;
                if (SHBindToParent(pidlFull, ref iidSF, out sfPtr, out pidlLast) != 0) { Marshal.FreeCoTaskMem(pidlFull); return; }
                var sf = (IShellFolder)Marshal.GetObjectForIUnknown(sfPtr);
                var apidl = new IntPtr[] { pidlLast };
                uint reserved = 0;
                IntPtr cmPtr;
                sf.GetUIObjectOf(hwndOwner, 1, apidl, ref iidCM, ref reserved, out cmPtr);
                Marshal.Release(sfPtr);
                if (cmPtr == IntPtr.Zero) { Marshal.FreeCoTaskMem(pidlFull); return; }
                var cm = (IContextMenu)Marshal.GetObjectForIUnknown(cmPtr);
                var hMenu = CreatePopupMenu();
                int qhr = cm.QueryContextMenu(hMenu, 0, 1, 0x7FFF, CMF_NORMAL);
                Program.DebugLog("[shell] QueryContextMenu hr=0x" + qhr.ToString("X"));
                if (onUnpin != null || onAddPalette != null || (extraText != null && extraAction != null))
                {
                    AppendMenu(hMenu, MF_SEPARATOR, 0, null);
                    if (onAddPalette != null) AppendMenu(hMenu, MF_STRING, CMD_ADDPALETTE, I18n.T("card_add_palette"));
                    if (onUnpin != null) AppendMenu(hMenu, MF_STRING, CMD_UNPIN, I18n.T("unpin"));
                    if (extraText != null && extraAction != null) AppendMenu(hMenu, MF_STRING, CMD_EXTRA, extraText);
                }
                POINT pt; GetCursorPos(out pt);
                Program.ShellMenuOpen = true;
                Program.DebugLog("[shell] menu opening, flag=true");
                // helper 窗口（可激活）前台化：不直接前台化卡片（MA_NOACTIVATE 权限受限静默失败）
                IntPtr ownerH = MenuOwnerHwnd();
                bool fgOk = NativeMethods.SetForegroundWindow(ownerH);
                int cmd;
                try
                {
                    cmd = TrackPopupMenu(hMenu, TPM_LEFTALIGN | TPM_TOPALIGN | TPM_RETURNCMD, pt.X, pt.Y, 0, ownerH, IntPtr.Zero);
                    // 菜单结束发 WM_NULL：让系统正确完成菜单模态收尾（MSDN 标准做法）
                    PostMessage(ownerH, 0x0000, IntPtr.Zero, IntPtr.Zero);
                }
                finally { Program.ShellMenuOpen = false; Program.DebugLog("[shell] menu closed, flag=false"); }
                Program.DebugLog("[shell] fg=" + fgOk + " cmd=" + cmd);
                if (cmd == CMD_UNPIN) { if (onUnpin != null) onUnpin(); }
                else if (cmd == CMD_ADDPALETTE) { if (onAddPalette != null) onAddPalette(); }
                else if (cmd == CMD_EXTRA) { if (extraAction != null) extraAction(); }
                else if (cmd > 0)
                {
                    // 优先拿 Unicode 动词名（GCS_VERBW）；lpVerb 传字符串数字会被当"动词名"查表而失败
                    // （实测 hr=0x80070483）。拿不到动词名则 lpVerb 用 MAKEINTRESOURCE 偏移（指针值=偏移）
                    int offset = cmd - 1;
                    string verb = null;
                    try
                    {
                        IntPtr buf = Marshal.AllocHGlobal(512 * 2);
                        try
                        {
                            if (cm.GetCommandString((IntPtr)offset, 4 /*GCS_VERBW*/, IntPtr.Zero, buf, 512) == 0)
                                verb = Marshal.PtrToStringUni(buf);
                        }
                        finally { Marshal.FreeHGlobal(buf); }
                    }
                    catch { }
                    var info = new CMINVOKECOMMANDINFOEX
                    {
                        cbSize = Marshal.SizeOf(typeof(CMINVOKECOMMANDINFOEX)),
                        fMask = 0x4000 /*CMIC_MASK_UNICODE*/,
                        nShow = 1,
                        hwnd = ownerH,
                    };
                    if (!string.IsNullOrEmpty(verb))
                    {
                        info.lpVerbW = verb;
                        info.lpVerb = (IntPtr)offset;   // ANSI 版同步给偏移：部分实现仍读 lpVerb
                    }
                    else info.lpVerb = (IntPtr)offset;
                    int hr = cm.InvokeCommand(ref info);
                    Program.DebugLog("[shell] InvokeCommand cmd=" + cmd + " verb=" + (verb ?? "#" + offset) + " hr=0x" + hr.ToString("X"));
                }
                DestroyMenu(hMenu);
                Marshal.Release(cmPtr);
                Marshal.FreeCoTaskMem(pidlFull);
            }
            catch (Exception ex) { Program.DebugLog("[shellmenu] " + ex.Message); }
        }
    }
}