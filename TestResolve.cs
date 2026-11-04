// 探针：验证 desktop.ini LocalizedResourceName + SHLoadIndirectString 能把本地化显示名
// 还原成真实文件系统名。用法: TestResolve.exe <父目录> <显示名>，缺省复现 保存的图片 → Saved Pictures
// 编译: csc /t:exe /out:TestResolve.exe TestResolve.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

static class TestResolve
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOut, int cch, IntPtr ppvReserved);

    static string LocalizedName(string dir)
    {
        try
        {
            string ini = Path.Combine(dir, "desktop.ini");
            if (!File.Exists(ini)) return null;
            foreach (var line in File.ReadAllLines(ini))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                if (!key.Equals("LocalizedResourceName", StringComparison.OrdinalIgnoreCase)
                    && !key.Equals("LocalizedName", StringComparison.OrdinalIgnoreCase)) continue;
                string val = line.Substring(eq + 1).Trim().Trim('"');
                if (val.Length == 0) continue;
                var sb = new StringBuilder(520);
                if (SHLoadIndirectString(Environment.ExpandEnvironmentVariables(val), sb, sb.Capacity, IntPtr.Zero) == 0 && sb.Length > 0)
                    return sb.ToString();
            }
        }
        catch { }
        return null;
    }

    static string Resolve(string dirPath, string displayName)
    {
        foreach (var d in Directory.GetDirectories(dirPath))
        {
            if (string.Equals(Path.GetFileName(d), displayName, StringComparison.OrdinalIgnoreCase)) return d;
            string loc = LocalizedName(d);
            if (loc != null && string.Equals(loc, displayName, StringComparison.OrdinalIgnoreCase)) return d;
        }
        return null;
    }

    static int Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : @"C:\Users\Admin\Pictures";
        string name = args.Length > 1 ? args[1] : "保存的图片";
        Console.WriteLine("direct  : " + (Directory.Exists(Path.Combine(dir, name)) ? "EXISTS" : "not-found"));
        string real = Resolve(dir, name);
        Console.WriteLine("resolved: " + (real ?? "null"));
        if (real == null) return 1;
        string second = Resolve(dir, args.Length > 1 ? name : "屏幕截图");
        Console.WriteLine("resolved2: " + (second ?? "null"));
        return 0;
    }
}
