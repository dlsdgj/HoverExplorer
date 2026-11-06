using System;
using System.Collections.Generic;
using System.Drawing;

namespace HoverExplorer
{
    // 桌面卡片·标签带配色方案（可编辑可导入）。
    // 与面板主题（Config.Theme）完全独立：卡片配色由 Config.CardPaletteIndex 单独记忆，
    // 鼠标在卡片上滚轮即循环切换（Program.CardWheelFilter）。
    // 可调项：卡片背景/文字色/徽标背景/徽标文字色/左侧色条(色+宽)/字体(名+大小)。
    internal sealed class CardPalette
    {
        public string Name;
        public Color Bg;          // 卡片底色
        public Color Fg;          // 主文字
        public Color BadgeBg;     // 徽标背景
        public Color BadgeFg;     // 徽标文字
        public Color StripColor;  // 左侧色条
        public int StripWidth;    // 色条宽度（0 = 不画）
        public string FontName;   // 字体名称
        public float FontSize;    // 字体大小
        public Color Hover;       // 悬停底色（由 Bg 明暗自动推导）
        public Color Sub;         // 副标题文字色（Fg 与 Bg 的中间调）
        public int Radius;        // 圆角半径（0=直角）

        public CardPalette() { }

        // 默认高对比黑白黄
        public static CardPalette Default()
        {
            var p = new CardPalette();
            p.Name = "高对比黑白黄";
            p.Bg = Color.Black;
            p.Fg = Color.White;

            p.BadgeBg = Color.FromArgb(0xff, 0xe6, 0x00);
            p.BadgeFg = Color.Black;
            p.StripColor = Color.White;
            p.StripWidth = 1;
            p.FontName = "微软雅黑";
            p.FontSize = 9f;
            p.Radius = 0;
            p._fnDerive();
            return p;
        }

        // 从 key=value 文本解析（未写的 key 保留默认值）
        public static CardPalette Parse(string text, string name)
        {
            var p = Default();
            p.Name = string.IsNullOrEmpty(name) ? "自定义" : name;
            if (string.IsNullOrEmpty(text)) return p;
            foreach (var line in text.Split('\n'))
            {
                var t = line.Trim().TrimEnd('\\');
                if (string.IsNullOrEmpty(t) || t.StartsWith("//") || t.StartsWith("```")) continue;
                int eq = t.IndexOf('=');
                if (eq <= 0) continue;
                string k = t.Substring(0, eq).Trim().ToLowerInvariant();
                string v = t.Substring(eq + 1).Trim();
                if (v.StartsWith("#")) v = v.Substring(1);
                switch (k)
                {
                    case "bg": p.Bg = Hx(v); break;
                    case "fg": p.Fg = Hx(v); break;

                    case "badge_bg": p.BadgeBg = Hx(v); break;
                    case "badge_fg": p.BadgeFg = Hx(v); break;
                    case "strip": p.StripColor = Hx(v); break;
                    case "strip_w": int sw; if (int.TryParse(v, out sw)) p.StripWidth = sw; break;
                    case "font": if (!string.IsNullOrEmpty(v)) p.FontName = v; break;
                    case "font_size": float fs; if (float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out fs) && fs > 0) p.FontSize = fs; break;
                    case "radius": int rd; if (int.TryParse(v, out rd)) p.Radius = rd; break;
                }
            }
            p._fnDerive();
            return p;
        }

        private void _fnDerive()
        {
            bool dark = Lum(Bg) < 128;
            Hover = Mix(Bg, dark ? Color.White : Color.Black, dark ? 0.10 : 0.07);
            Sub = Mix(Fg, Bg, 0.45);
        }

        // 序列化为 key=value 文本（颜色用 rgba 格式）
        public string Serialize()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("bg=").Append(ToRgba(Bg)).Append('\n');
            sb.Append("fg=").Append(ToRgba(Fg)).Append('\n');

            sb.Append("badge_bg=").Append(ToRgba(BadgeBg)).Append('\n');
            sb.Append("badge_fg=").Append(ToRgba(BadgeFg)).Append('\n');
            sb.Append("strip=").Append(ToRgba(StripColor)).Append('\n');
            sb.Append("strip_w=").Append(StripWidth).Append('\n');
            sb.Append("font=").Append(FontName).Append('\n');
            sb.Append("font_size=").Append(FontSize).Append('\n');
            sb.Append("radius=").Append(Radius);
            return sb.ToString();
        }

        public static string ToHex(Color c)
        {
            if (c.A < 255) return c.A.ToString("x2") + c.R.ToString("x2") + c.G.ToString("x2") + c.B.ToString("x2");
            return c.R.ToString("x2") + c.G.ToString("x2") + c.B.ToString("x2");
        }

        public static string ToRgba(Color c) { return "rgba(" + c.R + "," + c.G + "," + c.B + "," + c.A + ")"; }

        public static Color Hx(string s)
        {
            if (string.IsNullOrEmpty(s) || s == "transparent") return Color.Transparent;
            s = s.Trim();
            // rgba(R,G,B,A) 或 rgb(R,G,B) 格式
            int paren = s.IndexOf('(');
            if (paren > 0 && (s.StartsWith("rgba", StringComparison.OrdinalIgnoreCase) || s.StartsWith("rgb", StringComparison.OrdinalIgnoreCase)))
            {
                var inner = s.Substring(paren + 1).TrimEnd(')', ' ');
                var parts = inner.Split(',');
                if (parts.Length >= 3)
                {
                    int r, g, b, a = 255;
                    if (int.TryParse(parts[0].Trim(), out r) && int.TryParse(parts[1].Trim(), out g) && int.TryParse(parts[2].Trim(), out b))
                    {
                        if (parts.Length >= 4)
                        {
                            float fa;
                            if (float.TryParse(parts[3].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out fa))
                                a = fa <= 1f ? (int)Math.Round(fa * 255) : (int)fa;
                        }
                        return Color.FromArgb(a, r, g, b);
                    }
                }
                return Color.Transparent;
            }
            s = s.TrimStart('#');
            try
            {
                if (s.Length == 6) s = "FF" + s;
                if (s.Length == 8) return Color.FromArgb(unchecked((int)uint.Parse(s, System.Globalization.NumberStyles.HexNumber)));
                return ColorTranslator.FromHtml("#" + s);
            }
            catch { return Color.Transparent; }
        }

        public static int Lum(Color c) { return (int)(0.299 * c.R + 0.587 * c.G + 0.114 * c.B); }

        public static Color Opaque(Color c) { return Color.FromArgb(255, c.R, c.G, c.B); }

        public static Color Mix(Color a, Color b, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }
    }

    internal static class CardPalettes
    {
        public static List<CardPalette> All = new List<CardPalette>();

        // 出厂预设方案（仅新安装用户没有任何方案时注入；老用户的自建方案不受影响）。
        // 格式：首行名称，其余 key=value（与配色编辑器/导入文本同一套语法）
        private static readonly string[] Presets = new string[]
        {
            "清爽浅蓝\nbg=rgba(248,249,250,245)\nfg=rgba(32,34,40,255)\nbadge_bg=rgba(65,113,204,237)\nbadge_fg=rgba(255,255,255,255)\nstrip=rgba(65,113,204,255)\nstrip_w=2\nfont=Microsoft YaHei UI\nfont_size=9.5\nradius=6",
            "极简白\nbg=rgba(255,255,255,248)\nfg=rgba(28,28,34,255)\nbadge_bg=rgba(28,28,34,255)\nbadge_fg=rgba(255,255,255,255)\nstrip=rgba(28,28,34,255)\nstrip_w=3\nfont=Microsoft YaHei UI\nfont_size=9.5\nradius=6",
            "墨绿纸感\nbg=rgba(246,241,231,230)\nfg=rgba(38,36,33,255)\nbadge_bg=rgba(153,57,43,235)\nbadge_fg=rgba(255,255,255,255)\nstrip=rgba(153,57,43,255)\nstrip_w=3\nfont=Microsoft YaHei UI\nfont_size=9\nradius=6",
            "樱花粉白\nbg=rgba(250,184,235,235)\nfg=rgba(37,34,37,255)\nbadge_bg=rgba(255,255,255,230)\nbadge_fg=rgba(28,23,27,255)\nstrip=rgba(233,32,140,255)\nstrip_w=3\nfont=Microsoft YaHei UI\nfont_size=10\nradius=8",
            "深空蓝玻璃\nbg=rgba(21,23,29,240)\nfg=rgba(232,233,237,255)\nbadge_bg=rgba(67,72,87,240)\nbadge_fg=rgba(241,242,243,255)\nstrip=rgba(67,221,252,255)\nstrip_w=4\nfont=Segoe UI\nfont_size=10\nradius=8",
            "青蓝深海\nbg=rgba(10,44,55,225)\nfg=rgba(232,235,237,255)\nbadge_bg=rgba(58,203,251,220)\nbadge_fg=rgba(15,15,15,255)\nstrip=rgba(58,203,251,255)\nstrip_w=4\nfont=Consolas\nfont_size=10\nradius=8",
            "森林绿\nbg=rgba(27,50,41,235)\nfg=rgba(234,235,235,255)\nbadge_bg=rgba(87,210,165,252)\nbadge_fg=rgba(15,15,15,255)\nstrip=rgba(87,210,165,255)\nstrip_w=3\nfont=Segoe UI\nfont_size=11\nradius=6",
            "暖橙琥珀\nbg=rgba(47,23,14,237)\nfg=rgba(236,234,233,255)\nbadge_bg=rgba(240,99,51,252)\nbadge_fg=rgba(255,255,255,255)\nstrip=rgba(240,99,51,255)\nstrip_w=5\nfont=Microsoft YaHei UI\nfont_size=10\nradius=6",
        };

        // 从 Config.CardPalettesData 加载（格式：每行 name=escapedText，\n→\\n、=→\\e）
        public static void Load()
        {
            All.Clear();
            if (!string.IsNullOrEmpty(Config.CardPalettesData))
            {
                foreach (var line in Config.CardPalettesData.Split('\n'))
                {
                    var t = line.Trim();
                    if (string.IsNullOrEmpty(t)) continue;
                    int eq = t.IndexOf('=');
                    if (eq <= 0) continue;
                    string name = t.Substring(0, eq).Trim().Replace("\\e", "=");
                    string escaped = t.Substring(eq + 1);
                    string text = escaped.Replace("\\n", "\n").Replace("\\e", "=");
                    All.Add(CardPalette.Parse(text, name));
                }
            }
            if (All.Count == 0)
            {
                // 新安装用户：注入默认高对比 + 一组出厂预设，保证开箱即有方案可用
                All.Add(CardPalette.Default());
                foreach (var t in Presets)
                {
                    int nl = t.IndexOf('\n');
                    if (nl <= 0) continue;
                    All.Add(CardPalette.Parse(t.Substring(nl + 1), t.Substring(0, nl)));
                }
            }
            if (Config.CardPaletteIndex < 0 || Config.CardPaletteIndex >= All.Count)
                Config.CardPaletteIndex = 0;
        }

        // 保存到 Config.CardPalettesData
        public static void Save()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var p in All)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(p.Name.Replace("=", "\\e")).Append('=').Append(p.Serialize().Replace("\n", "\\n"));
            }
            Config.CardPalettesData = sb.ToString();
        }

        public static CardPalette Get(int index)
        {
            if (All.Count == 0) All.Add(CardPalette.Default());   // 兜底：任何时刻不返回 null，卡片永远打得开
            if (index < 0 || index >= All.Count) index = 0;
            return All[index];
        }

        public static CardPalette Current { get { return Get(Config.CardPaletteIndex); } }
    }
}
