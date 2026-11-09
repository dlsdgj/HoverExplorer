using System;
using System.Diagnostics;
using System.IO;

namespace HoverExplorer
{
    // 设置面板两处「配色生成器」链接：优先打开随程序附带的单文件 HTML，
    // 本地找不到（例如 exe 被单独拷走）才回退到在线生成器地址。
    // WPF 设置窗与 WinForms 回退面板共用这一入口。
    internal static class GeneratorLinks
    {
        private const string ListHtml = "hoverExplorers-scheme-generator.html";
        private const string CardHtml = "hoverexplorer-card-palette.html";
        private const string ListUrl = "https://github.com/dlsdgj/hoverExplorers-cheme-generator";
        private const string CardUrl = "https://dlsdgj.github.io/hoverexplorer-card-palette/";

        internal static void Open(string which)
        {
            bool card = string.Equals(which, "card", StringComparison.OrdinalIgnoreCase);
            string[] names = card
                ? new[] { CardHtml }
                : new[] { ListHtml, "scheme-generator.html" };
            string url = card ? CardUrl : ListUrl;

            foreach (string dir in CandidateDirs())
            {
                foreach (string name in names)
                {
                    string path;
                    try { path = Path.Combine(dir, name); }
                    catch { continue; }
                    if (!File.Exists(path)) continue;
                    try
                    {
                        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                        return;
                    }
                    catch { }
                }
            }
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }

        // exe 同目录优先（发布时两个 HTML 与 exe 放在一起）；
        // 再向上找源码目录 / 生成器目录，覆盖开发目录直接运行的情形。
        private static string[] CandidateDirs()
        {
            string b = AppDomain.CurrentDomain.BaseDirectory;
            string p = Parent(b);
            string pp = Parent(p);
            return new[]
            {
                b,
                Path.Combine(b, "列表配色方案生成"),
                p,
                Path.Combine(p, "列表配色方案生成"),
                pp,
                Path.Combine(pp, "列表配色方案生成"),
                Environment.CurrentDirectory,
            };
        }

        private static string Parent(string dir)
        {
            try
            {
                DirectoryInfo d = Directory.GetParent(dir.TrimEnd(Path.DirectorySeparatorChar));
                return d == null ? dir : d.FullName;
            }
            catch { return dir; }
        }
    }
}
