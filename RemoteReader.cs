using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace HoverExplorer
{
    internal sealed class RemoteReader : IDisposable
    {
        private readonly IntPtr _hwnd;
        private readonly IntPtr _proc;

        public uint Pid { get; private set; }

        public RemoteReader(IntPtr hwnd)
        {
            _hwnd = hwnd;
            uint pid;
            NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
            Pid = pid;
            _proc = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_VM_OPERATION | NativeMethods.PROCESS_VM_READ | NativeMethods.PROCESS_VM_WRITE,
                false, pid);
            if (_proc == IntPtr.Zero)
                throw new InvalidOperationException("OpenProcess failed, pid=" + pid);
        }

        public void Dispose()
        {
            if (_proc != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(_proc);
            }
        }

        private IntPtr Alloc(uint size)
        {
            return NativeMethods.VirtualAllocEx(_proc, IntPtr.Zero, size, NativeMethods.MEM_COMMIT, NativeMethods.PAGE_READWRITE);
        }

        private void Write(IntPtr remote, byte[] data, uint size)
        {
            var gc = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                uint written;
                NativeMethods.WriteProcessMemory(_proc, remote, gc.AddrOfPinnedObject(), size, out written);
            }
            finally
            {
                gc.Free();
            }
        }

        private void Read(IntPtr remote, byte[] buf, uint size)
        {
            var gc = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                uint read;
                NativeMethods.ReadProcessMemory(_proc, remote, gc.AddrOfPinnedObject(), size, out read);
            }
            finally
            {
                gc.Free();
            }
        }

        public int HitTest(int screenX, int screenY)
        {
            var pt = new NativeMethods.POINT { X = screenX, Y = screenY };
            if (!NativeMethods.ScreenToClient(_hwnd, ref pt))
                return -1;

            int size = Marshal.SizeOf(typeof(NativeMethods.LVHITTESTINFO));
            IntPtr remote = Alloc((uint)size);
            if (remote == IntPtr.Zero)
                return -1;
            try
            {
                var local = new NativeMethods.LVHITTESTINFO { x = pt.X, y = pt.Y, flags = 0, iItem = -1, iSubItem = 0 };
                var bytes = StructureToBytes(local);
                Write(remote, bytes, (uint)size);
                NativeMethods.SendMsg(_hwnd, (uint)NativeMethods.LVM_SUBITEMHITTEST, IntPtr.Zero, remote);
                var outBuf = new byte[size];
                Read(remote, outBuf, (uint)size);
                var outInfo = BytesToStructure<NativeMethods.LVHITTESTINFO>(outBuf);
                if ((outInfo.flags & (NativeMethods.LVHT_ONITEMICON | NativeMethods.LVHT_ONITEMLABEL)) != 0)
                    return outInfo.iItem;
                return -1;
            }
            finally
            {
                NativeMethods.VirtualFreeEx(_proc, remote, 0, NativeMethods.MEM_RELEASE);
            }
        }

        public string GetItemText(int itemIndex, int bufSize = 520)
        {
            int sizeText = bufSize * 2;
            IntPtr remoteText = Alloc((uint)sizeText);
            int sizeItem = Marshal.SizeOf(typeof(NativeMethods.LVITEMW));
            IntPtr remoteItem = Alloc((uint)sizeItem);
            if (remoteText == IntPtr.Zero || remoteItem == IntPtr.Zero)
                return "";
            try
            {
                var local = new NativeMethods.LVITEMW
                {
                    mask = NativeMethods.LVIF_TEXT,
                    iItem = itemIndex,
                    iSubItem = 0,
                    pszText = remoteText,
                    cchTextMax = bufSize
                };
                Write(remoteItem, StructureToBytes(local), (uint)sizeItem);
                NativeMethods.SendMsg(_hwnd, (uint)NativeMethods.LVM_GETITEMTEXTW, (IntPtr)itemIndex, remoteItem);
                var textBuf = new byte[sizeText];
                Read(remoteText, textBuf, (uint)sizeText);
                return Encoding.Unicode.GetString(textBuf).TrimEnd('\0');
            }
            finally
            {
                if (remoteText != IntPtr.Zero)
                    NativeMethods.VirtualFreeEx(_proc, remoteText, 0, NativeMethods.MEM_RELEASE);
                if (remoteItem != IntPtr.Zero)
                    NativeMethods.VirtualFreeEx(_proc, remoteItem, 0, NativeMethods.MEM_RELEASE);
            }
        }

        public int GetItemCount()
        {
            return (int)(long)NativeMethods.SendMsg(_hwnd, (uint)NativeMethods.LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
        }

        public Rectangle GetItemRect(int itemIndex)
        {
            return GetItemRect(itemIndex, 1);   // 默认 LVIR_ICON：图标图形矩形（高亮描边用）
        }

        // portion=LVM_GETITEMRECT 的取值：0=LVIR_BOUNDS(整个条目含图标+名称) 1=LVIR_ICON(仅图标图形)
        public Rectangle GetItemRect(int itemIndex, int portion)
        {
            int size = Marshal.SizeOf(typeof(NativeMethods.RECT));
            IntPtr remote = Alloc((uint)size);
            if (remote == IntPtr.Zero) return Rectangle.Empty;
            try
            {
                var local = new NativeMethods.RECT { Left = portion };
                Write(remote, StructureToBytes(local), (uint)size);
                NativeMethods.SendMsg(_hwnd, (uint)NativeMethods.LVM_GETITEMRECT, (IntPtr)itemIndex, remote);
                var outBuf = new byte[size];
                Read(remote, outBuf, (uint)size);
                var rc = BytesToStructure<NativeMethods.RECT>(outBuf);
                var tl = new NativeMethods.POINT { X = rc.Left, Y = rc.Top };
                var br = new NativeMethods.POINT { X = rc.Right, Y = rc.Bottom };
                NativeMethods.ClientToScreen(_hwnd, ref tl);
                NativeMethods.ClientToScreen(_hwnd, ref br);
                return Rectangle.FromLTRB(tl.X, tl.Y, br.X, br.Y);
            }
            finally
            {
                NativeMethods.VirtualFreeEx(_proc, remote, 0, NativeMethods.MEM_RELEASE);
            }
        }

        private static byte[] StructureToBytes(object obj)
        {
            int size = Marshal.SizeOf(obj);
            var buf = new byte[size];
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(obj, ptr, false);
                Marshal.Copy(ptr, buf, 0, size);
                return buf;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        private static T BytesToStructure<T>(byte[] buf)
        {
            var ptr = Marshal.AllocHGlobal(buf.Length);
            try
            {
                Marshal.Copy(buf, 0, ptr, buf.Length);
                return (T)Marshal.PtrToStructure(ptr, typeof(T));
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
    }
}