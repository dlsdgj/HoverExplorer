using System;
using System.Windows.Forms;
using HoverExplorer;

// HoverSuppressed 自检：脱离真实键盘，直接喂 Keys 状态断言双门控/Alt 抑制的判定表。
// 门控拆两套：文件夹面板（HoverSuppressed(mk,false)）/ 文件预览（HoverSuppressed(mk,true)）。
// 编译：csc /out:check.exe Config.cs I18n.cs CardPalette.cs TestGate.cs
class TestGate
{
    static int fail = 0;
    static void Check(string name, bool got, bool want)
    {
        if (got != want) { Console.WriteLine("[FAIL] " + name + " got=" + got + " want=" + want); fail++; }
        else Console.WriteLine("[ok] " + name);
    }
    static bool Folder(Keys mk) { return Config.HoverSuppressed(mk, false, true); }   // 第一级（门控生效）
    static bool Preview(Keys mk) { return Config.HoverSuppressed(mk, true, true); }
    [STAThread]
    static int Main(string[] args)
    {
        // config 往返冒烟（smoke.ps1 调用）：BaseDirectory 下放置生成的 config.json，
        // 加载→断言新键→保存→确认落盘含全部新键且无废弃键（抓 Save/Load 键名不一致）
        if (args.Length > 0 && args[0] == "config")
        {
            Config.Load();
            Check("cfg.Theme=auto", Config.Theme == "auto", true);
            Check("cfg.ExplorerEnabled", Config.ExplorerEnabled, true);
            Check("cfg.FolderGateMods=2", Config.FolderGateMods == 2, true);
            Check("cfg.PreviewGate=Shift", Config.PreviewGateEnabled && Config.PreviewGateMods == 1, true);
            Check("cfg.PreviewMuted", Config.PreviewMuted, true);
            Config.Save();
            var saved = System.IO.File.ReadAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json"));
            Check("save.新键落盘", saved.Contains("\"FolderGateMods\"") && saved.Contains("\"PreviewMuted\"")
                && saved.Contains("\"PreviewGateMods\""), true);
            Check("save.废弃键未写入", !saved.Contains("HoverGateEnabled"), true);
            Console.WriteLine(fail == 0 ? "CONFIG 全部通过" : ("CONFIG 失败 " + fail));
            return fail;
        }
        // 默认：门控全关，Alt 抑制开
        Check("默认-无键-面板放行", Folder(Keys.None), false);
        Check("默认-无键-预览放行", Preview(Keys.None), false);
        Check("默认-按Alt-面板抑制", Folder(Keys.Alt), true);
        Check("默认-按Alt-预览抑制", Preview(Keys.Alt), true);
        Check("默认-按Ctrl-放行", Folder(Keys.Control), false);

        // 面板门控开（默认 Ctrl）：预览不受影响
        Config.FolderGateEnabled = true; Config.FolderGateMods = 2;
        Check("面板门控Ctrl-无键-面板抑制", Folder(Keys.None), true);
        Check("面板门控Ctrl-按Ctrl-面板放行", Folder(Keys.Control), false);
        Check("面板门控Ctrl-无键-预览不受累", Preview(Keys.None), false);
        Check("面板门控Ctrl-按Shift-面板抑制", Folder(Keys.Shift), true);

        // 预览门控开（Shift）：面板照常，预览按 Shift 放行
        Config.PreviewGateEnabled = true; Config.PreviewGateMods = 1;
        Check("预览门控Shift-按Shift-预览放行", Preview(Keys.Shift), false);
        Check("预览门控Shift-按Ctrl-预览抑制", Preview(Keys.Control), true);
        Check("预览门控Shift-按Ctrl-面板放行", Folder(Keys.Control), false);

        // 双门控：按 Ctrl → 面板放行 + 预览抑制（各拦各的）
        Check("双门控-按Ctrl-面板放行预览抑制", Folder(Keys.Control), false);
        Check("双门控-按Ctrl-预览抑制", Preview(Keys.Control), true);

        // 面板门控键含 Alt：按 Alt 时豁免 Alt 抑制；预览门控不含 Alt → 预览仍被 Alt 抑制
        Config.FolderGateMods = 2 | 4;
        Check("面板门控含Alt-按Alt-面板放行(豁免抑制)", Folder(Keys.Alt), false);
        Check("面板门控含Alt-按Alt-预览仍抑制", Preview(Keys.Alt), true);

        // 预览门控键也含 Alt：两边都放行
        Config.PreviewGateMods = 4;
        Check("预览门控含Alt-按Alt-预览放行", Preview(Keys.Alt), false);

        // 门控 Ctrl（不含 Alt）+ 按 Ctrl+Alt：门控放行，Alt 抑制仍生效
        Config.FolderGateMods = 2; Config.PreviewGateEnabled = false;
        Check("面板门控Ctrl-Ctrl+Alt-面板抑制", Folder(Keys.Control | Keys.Alt), true);

        // Alt 抑制关 + 面板门控：未按键抑制，按 Ctrl 放行
        Config.AltSuppressHover = false;
        Check("抑制关-无键-面板抑制", Folder(Keys.None), true);
        Check("抑制关-按Ctrl-面板放行", Folder(Keys.Control), false);

        // 全关：任何键态都放行
        Config.FolderGateEnabled = false;
        Check("全关-按Alt-放行", Folder(Keys.Alt), false);
        Check("全关-无键-放行", Folder(Keys.None), false);

        // ModsLabel
        Check("ModsLabel", Config.ModsLabel(2 | 1) == "Ctrl+Shift" && Config.ModsLabel(4) == "Alt", true);


        // 门控只拦第一级：面板内部（Inside）不看门控，Alt 临时禁用仍生效
        Config.FolderGateEnabled = true; Config.FolderGateMods = 2;
        Config.PreviewGateEnabled = true; Config.PreviewGateMods = 1;
        Config.AltSuppressHover = true;
        Check("面板内-无键-子面板放行", Config.HoverSuppressed(Keys.None, false, false), false);
        Check("面板内-无键-行预览放行", Config.HoverSuppressed(Keys.None, true, false), false);
        Check("面板内-按Alt-仍抑制", Config.HoverSuppressed(Keys.Alt, false, false), true);
        Check("第一级-无键-面板仍拦", Config.HoverSuppressed(Keys.None, false, true), true);
        Check("第一级-按Ctrl-面板放行", Config.HoverSuppressed(Keys.Control, false, true), false);

        Console.WriteLine(fail == 0 ? "全部通过" : ("失败 " + fail));
        return fail;
    }
}
