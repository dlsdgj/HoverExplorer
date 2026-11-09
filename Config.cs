using System;
using System.Collections.Generic;
using System.IO;
using System.Drawing;
using System.Web.Script.Serialization;

namespace HoverExplorer
{
    internal static class Config
    {
        // 预览类别：设置面板逐类开关用；与 PreviewProvider 的分派分支一一对应
        public enum PreviewKind { Image, Video, Audio, Document, Text, Archive, Font, Other }

        // 某类是否允许预览（关掉后该类文件不再弹预览窗）
        public static bool PreviewEnabled(PreviewKind kind)
        {
            switch (kind)
            {
                case PreviewKind.Image: return PreviewImage;
                case PreviewKind.Video: return PreviewVideo;
                case PreviewKind.Audio: return PreviewAudio;
                case PreviewKind.Document: return PreviewDocument;
                case PreviewKind.Text: return PreviewText;
                case PreviewKind.Archive: return PreviewArchive;
                case PreviewKind.Font: return PreviewFont;
                default: return PreviewOther;
            }
        }

        // 悬停抑制判定（唯一入口，Program/PopupForm 共用；Keys 参数版可脱离键盘做自检）：
        // 门控：按住任一所选修饰键才放行——只拦"从无到有"的第一级触发（桌面/资源管理器直接悬停）。
        //   一级面板已通过门控，面板内部的子面板/行预览免键操作，走 HoverSuppressedInside
        // 长按模式（gateMods 位 8）：不抑制悬停检测，由触发处用 LongPressDelayMs 替代 HoverDelay 计时
        // 修饰键实时态：必须用 GetAsyncKeyState（全局实时，独立于线程消息队列）。
        // Control.ModifierKeys(=GetKeyState) 是线程本地缓存——本进程不是前台时（悬停资源管理器的常态）
        // 键盘消息进的是 Explorer 的队列，这里读到的是上次处理消息时的旧快照，UIA 探测越忙越滞后，
        // 表现为"按住 Ctrl 门控时灵时不灵"。项目内其余按键判定（拖拽/热键/翻页）本就用 GetAsyncKeyState
        internal static System.Windows.Forms.Keys RealModifiers()
        {
            var mk = System.Windows.Forms.Keys.None;
            if ((HoverExplorer.NativeMethods.GetAsyncKeyState(0x11) & 0x8000) != 0) mk |= System.Windows.Forms.Keys.Control;
            if ((HoverExplorer.NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0) mk |= System.Windows.Forms.Keys.Shift;
            if ((HoverExplorer.NativeMethods.GetAsyncKeyState(0x12) & 0x8000) != 0) mk |= System.Windows.Forms.Keys.Alt;
            return mk;
        }
        internal static bool HoverSuppressed(bool forPreview) { return HoverSuppressed(RealModifiers(), forPreview, true, IsLeftDown()); }
        // 面板内部触发（子面板/行预览）：不做门控
        internal static bool HoverSuppressedInside(bool forPreview) { return HoverSuppressed(RealModifiers(), forPreview, false, IsLeftDown()); }
        // 鼠标左键实时按下态（长按门控用，与 RealModifiers 同源 GetAsyncKeyState）
        internal static bool IsLeftDown() { return (HoverExplorer.NativeMethods.GetAsyncKeyState(0x01) & 0x8000) != 0; }
        internal static bool HoverSuppressed(System.Windows.Forms.Keys mk, bool forPreview, bool applyGate, bool leftDown = false)
        {
            int gateMods = forPreview ? PreviewGateMods : FolderGateMods;
            bool gateOn = forPreview ? PreviewGateEnabled : FolderGateEnabled;
            bool longPress = gateOn && (gateMods & 8) != 0;
            if (longPress)
            {
                if (!applyGate) return false;     // 面板内部免键
                return !leftDown;                 // 一级触发：按住左键才放行，由触发处用 LongPressDelayMs 计时
            }
            if (applyGate && gateOn)
            {
                bool gate = ((mk & System.Windows.Forms.Keys.Shift) != 0 && (gateMods & 1) != 0)
                         || ((mk & System.Windows.Forms.Keys.Control) != 0 && (gateMods & 2) != 0)
                         || ((mk & System.Windows.Forms.Keys.Alt) != 0 && (gateMods & 4) != 0);
                if (!gate) return true;
            }
            return false;
        }
        // 长按模式判定：该功能是否启用长按门控（触发处据此用 LongPressDelayMs 替代 HoverDelay）
        internal static bool LongPressFor(bool forPreview)
        {
            if (forPreview) return PreviewGateEnabled && (PreviewGateMods & 8) != 0;
            return FolderGateEnabled && (FolderGateMods & 8) != 0;
        }

        // 修饰键掩码 → 显示文本（门控提示/设置用）
        public static string ModsLabel(int m)
        {
            var parts = new List<string>();
            if ((m & 2) != 0) parts.Add("Ctrl");
            if ((m & 1) != 0) parts.Add("Shift");
            if ((m & 4) != 0) parts.Add("Alt");
            return string.Join("+", parts.ToArray());
        }

        public static readonly HashSet<string> ImgExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp",
          // GDI+/WIC 解不了时走 ffmpeg 单帧兜底（GenImage 三级回落），专业格式全家桶
          ".jfif", ".jpe", ".exr", ".dpx", ".cin", ".j2k", ".jp2", ".jpf",
          ".pgm", ".ppm", ".pbm", ".pnm", ".tga", ".icb", ".vda", ".vst",
          ".dds", ".xbm", ".xface", ".pcx", ".sgi", ".ras", ".pic", ".qoi",
          ".hdr", ".wdp", ".hdp", ".jxr", ".avif", ".mpo" };
        public static readonly HashSet<string> IcoExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".ico", ".tiff", ".tif" };
        public static readonly HashSet<string> OfficeExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".docx", ".xlsx", ".pptx" };
        public static readonly HashSet<string> ZipExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".zip" };
        public static readonly HashSet<string> SevenZipExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".7z", ".rar" };
        public static readonly HashSet<string> FontExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".ttf", ".otf", ".ttc" };
        public static readonly HashSet<string> ExeExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".dll" };
        public static readonly HashSet<string> LnkExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".lnk" };
        public static readonly HashSet<string> EpubExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".epub" };
        public static readonly HashSet<string> AudioExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".wma", ".m4a", ".flac", ".aac", ".ogg", ".opus",
          // 音频链自带 ffmpeg 兜底（FfmpegAudioSession 优先于 MF/MCI），装了 ffmpeg 全都能播
          ".ac3", ".eac3", ".dts", ".dtshd", ".thd", ".mka", ".mpc", ".mpp",
          ".ape", ".apl", ".mac", ".wv", ".tta", ".tak", ".shn", ".ofr", ".ofs",
          ".aiff", ".aif", ".aifc", ".au", ".amr", ".gsm", ".spx", ".dsf", ".dff",
          ".m4r", ".aa", ".aax", ".oma", ".aa3", ".adx", ".aix", ".acm", ".caf",
          ".awb", ".g722", ".g729", ".g723_1", ".mlp", ".ast", ".ads", ".ss2",
          ".adp", ".dtk", ".vag", ".svag", ".xvag", ".msf", ".sf", ".ircam",
          ".pvf", ".sln", ".w64", ".voc", ".vox", ".ra", ".snd" };
        // 视频扩展名：.m4v 是 iTunes/索尼设备常用的 MP4 变体（品牌与 .mp4 完全一致，
        // 只是扩展名不同），此前未列入导致只出元数据不出画面。补齐移动端与广播常见容器。
        public static readonly HashSet<string> VideoExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".avi", ".mov", ".wmv",
          ".m4v", ".m4b", ".m4p",                       // iTunes/索尼变体
          ".3gp", ".3g2", ".3gpp",                      // 手机
          ".flv", ".f4v", ".webm", ".ogv", ".ogm",      // 流媒体容器
          ".ts", ".m2ts", ".mts", ".m2t",               // 索尼/松下摄录机
          ".mxf", ".gxf", ".wtv", ".dvr-ms", ".evo",         // 广播/机顶盒/录制容器
          ".mod", ".tod", ".amv", ".nsv", ".swf", ".gvi", ".vivo", ".ty", ".nuv", ".mxg", ".lvf",
          ".roq", ".bik", ".bk2", ".smk",                     // 游戏动画（ffmpeg 内置解码）
          ".ivf", ".obu",                                     // VP8/9 / AV1 裸流
          ".avc", ".h264", ".264", ".h265", ".265", ".hevc", ".h261",  // 裸基本流
          ".mjpg", ".mjpeg", ".mpo", ".mj2", ".y4m",          // MJPEG / JPEG2000 / YUV4MPEG
          ".vob", ".mpg", ".mpeg", ".mpe", ".m1v", ".m2v", ".mp2", ".m2p",
          ".asf", ".rm", ".rmvb", ".divx", ".xvid", ".dv", ".f4v" };
        public static readonly HashSet<string> SvgExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".svg" };
        public static int PreviewDelay = 400;
        public static int PreviewMax = 240;
        public static double HoverDelay = 0.30;
        // 文件预览触发延时跟随用户的悬停延时设置（秒 -> 毫秒）
        public static int HoverDelayMs { get { return Math.Max(50, (int)(HoverDelay * 1000)); } }
        // 视频预览单独触发延时（秒）：视频起播慢、误触成本高，默认比全局延时长；
        // 图片/文档等仍用全局 HoverDelay，音频维持面板行里既有的 100ms 提速。
        // 开关关闭或边缘触发开启时视频不加延时（防误触已由触发区负责）
        public static bool VideoPreviewDelayEnabled = true;
        public static double VideoPreviewDelay = 0.8;
        // 文件预览边缘触发：0=关(整个条目触发) 1=左缘 2=右缘 3=两侧。
        // 开着时条目中段不弹预览窗（留给改名/右键/拖拽），只有停在边缘触发区才预览
        public static int PreviewEdgeMode = 0;
        public static int PreviewEdgeWidth = 48;

        // 按类型的预览触发延时：视频走 VideoPreviewDelay（需开关开启），其余用全局 HoverDelay
        // （音频的 100ms 提速在 SchedulePreview 里叠加 min 处理）。
        // 延时开关关闭、或边缘触发开启时不加视频延时——防误触已由触发区负责，再等 0.8s 纯属双重惩罚
        public static int PreviewDelayMsFor(string full)
        {
            int d = HoverDelayMs;
            try
            {
                string ext = Path.GetExtension(full ?? "");
                if (ext.Length > 0 && VideoExts.Contains(ext) && VideoPreviewDelayEnabled && PreviewEdgeMode == 0)
                    d = Math.Max(50, (int)(VideoPreviewDelay * 1000));
            }
            catch { }
            return d;
        }
        public static int SubHoverDelay = 150;
        public static double CloseDelay = 0.35;
        // 鼠标离开后关闭列表/预览窗的统一延迟（毫秒）：面板、预览窗、历史·收藏列表共用
        public static int CloseDelayMs { get { return Math.Max(50, (int)Math.Round(CloseDelay * 1000)); } }
        // 触发区域（收藏·历史入口）悬停延时（毫秒），独立于文件夹悬停延时
        public static int EntryRectDelayMs = 300;
        // 暂停触控区驻留延时（毫秒）：驻留多久切换悬停启用/暂停，与收藏·历史触发区域延时相互独立
        public static int PauseRectDelayMs = 300;
        // 暂停触控区：鼠标驻留屏幕角落切换悬停启用/暂停（0=关 1=左上 2=右上 3=左下 4=右下）
        public static int CheckInterval = 80;
        public static int PopupOffset = 16;
        public static int SubGap = 4;
        public static int MaxItems = 400;   // 200 会在几百条的目录（如 D:\下载）截掉中文名条目
        public static int PreviewMinWidth = 240;
        public static bool PreviewOriginalSize = true;
        // 记住的图片预览窗口尺寸（右下角手柄拖拽 / Ctrl+滚轮 调整后写入）；0 = 未设置，走自动尺寸
        public static int PreviewImgW = 0;
        public static int PreviewImgH = 0;
        // 记住的文本预览窗口尺寸（右下角手柄拖拽 / Ctrl+滚轮）；0 = 未设置，按内容自动算
        public static int PreviewTextW = 0;
        public static int PreviewTextH = 0;
        // 记住的自定义面板预览窗口尺寸，视频/音频分开记（两者形态差异大，不能共用）
        // PreviewMedia* 用于 SVG 面板；0 = 未设置，走自动尺寸
        public static int PreviewMediaW = 0;
        public static int PreviewMediaH = 0;
        public static int PreviewVideoW = 0;
        public static int PreviewVideoH = 0;
        public static int PreviewAudioW = 0;
        public static int PreviewAudioH = 0;
        // 音频预览上次是否折叠（折叠态高度固定 34，展开高度另记在 PreviewAudioH）
        public static bool PreviewAudioCollapsed = true;
        // 收藏·历史列表宽度：0=按内容自适应；>0=用户拖宽后记忆
        public static int EntryListW = 0;
        public static int EntryListH = 0;

        // 窗口边角风格：0=直角（锐利卡片风）1=圆角·粗描边（DWM 圆角 + 3px 自绘描边，默认）
        public static int WindowCorners = 1;
        // 自动尺寸（含"原图大小"）允许占用的最大屏幕比例，避免大图铺满整屏
        public static double PreviewMaxRatio = 0.8;
        // 固定为"仅图标/名称触发"（已移除该开关）
        public static readonly bool NameOnlyTrigger = true;
        public static int PanelWidth = 0;
        public static int PanelHeight = 0;
        // 面板高度自动适配内容（条目多时面板长高）；用户手动拖过尺寸后转纯记忆模式
        public static bool PanelSizeAuto = true;
        public static bool ExplorerEnabled = true;   // 新装默认开启资源管理器悬停（配合下方 FolderGateEnabled=false → 模式“总是”）
        // Total Commander 悬停检测（TC 11.x LCL 界面，文件列表类名 "L"）。
        // 2026-10-22 暂停：排序对齐不可靠（ini 是退出快照、临时排序错位）、HintWindow 阴影残留
        // 未彻底解决，代码保留待后续处理。要启用：config.json 设 TcEnabled=true（排序声明用设置里的"TC 排序"）
        public static bool TcEnabled = false;
        // perch (ClipPad) 文本块 [text](路径) 悬停检测。WebView2 UIAutomation TextPattern 取鼠标下文本
        public static bool PerchEnabled = true;
        // TC 排序模式声明：-1=自动(从 TC ini 推断，运行中临时改排序会错位)；0=名称↑ 1=名称↓
        // 2=日期↑ 3=日期↓ 4=大小↑ 5=大小↓ 6=扩展名↑ 7=扩展名↓。ini 推测不准时由用户显式指定
        public static int TcSortMode = -1;
        public static bool PreviewFiles = true;
        // ===== 预览分类开关（逐类开关，不必整体禁用）=====
        // 关掉某类后该类文件悬停不再弹预览窗（CanPreview 直接判否，不占延时也不弹空窗）
        public static bool PreviewImage = true;
        public static bool PreviewVideo = true;
        // 视频预览音量（0-100）：音量条调过就记住，下次预览直接用它（静音键独立于音量）
        public static double PreviewVolume = 100;
        public static bool PreviewAudio = true;
        public static bool PreviewDocument = true;
        public static bool PreviewText = true;
        public static bool PreviewArchive = true;
        public static bool PreviewFont = true;
        public static bool PreviewOther = true;
        // 倍速刻度尺：视频、音频各自独立开关（设置面板分别挂在「视频」「音频」类型开关下）
        public static bool VideoSpeedToggle = true;
        public static bool AudioSpeedToggle = true;
        // 兼容别名：旧 config.json 的 SpeedToggle 键与旧 WinForms 设置面板读写它时，
        // 同时映射到上面两个新开关；新配置以两个独立键为准。
        public static bool SpeedToggle
        {
            get { return VideoSpeedToggle || AudioSpeedToggle; }
            set { VideoSpeedToggle = value; AudioSpeedToggle = value; }
        }
        public static bool WaveformEnabled = true;   // 音频波形图开关（解码占 CPU）
        public static bool SpectrumEnabled = false;  // 频谱显示（波形图关闭时可选）
        // ffmpeg 可执行文件路径（留空=自动探测）：用于解码 Media Foundation 解不了的
        // 高规格视频（10bit 4:2:2 / HEVC / 超高码率），以及作为解码兜底
        public static string FfmpegPath = "";
        public static bool DebugLog = false;
        public static bool HideTooltip = false;
        // 列表/预览窗口的按钮提示（tooltip），可在托盘菜单关闭
        public static bool ShowTips = true;
        public static bool HideTrayIcon = false;
        public static string TrayIconPath = "";
        public static bool TrayShowExplorer = true;
        public static bool TrayShowPreview = true;
        public static bool TrayShowOrigSize = false;
        public static bool TrayShowDebugLog = false;
        public static bool TrayShowHideTooltip = false;
        public static bool TrayShowButtonTips = true;
        public static bool TrayShowAutostart = true;
        public static bool TrayShowHideTray = true;
        public static bool TrayShowAddCard = true;
        public static int TrayMiddleAction = 1;
        public static bool HotkeyEnabled = true;
        public static int HotkeyModifiers = 0x2 | 0x1; // Ctrl+Alt
        public static int HotkeyKey = 81; // Q
        // 第二热键：剪贴板内容 → 桌面卡片
        public static bool CardHotkeyEnabled = true;
        public static int CardHotkeyModifiers = 0x2 | 0x1; // Ctrl+Alt
        public static int CardHotkeyKey = 86; // V
        // 历史/收藏文件夹入口：全局热键 + 桌面触发区域（矩形为 0 = 未设置）
        public static bool EntryHotkeyEnabled = false;
        public static int EntryHotkeyModifiers = 0x2 | 0x1; // Ctrl+Alt
        public static int EntryHotkeyKey = 69; // E
        public static int EntryRectX = 0, EntryRectY = 0, EntryRectW = 0, EntryRectH = 0;
        // 触发区域的 RGBA 标识色（ARGB）：桌面穿透色块 + 设置面板色块预览
        public static int EntryRectColor = 0x283B8EEA;   // 蓝 ≈16% 透明度
        // 暂停触控区：独立框选矩形（设置方式复制触发区域那套），屏幕绝对坐标；W/H<5=未启用
        public static int PauseRectX = 0, PauseRectY = 0, PauseRectW = 0, PauseRectH = 0;
        // 收藏文件夹（用户手动）与最近打开（自动 MRU，上限 EntryRecentMax）
        // FavoriteFolders 保留作旧格式兼容读取；新数据走 FavoriteGroups（多分组）
        public static List<string> FavoriteFolders = new List<string>();
        public static List<FavoriteGroup> FavoriteGroups = new List<FavoriteGroup>();
        public static int FavoriteCurGroup = 0;   // 当前选中分组索引
        public static List<string> RecentFolders = new List<string>();
        public static readonly int EntryRecentMax = 15;
        // 鼠标离开启用的面板/预览不自动关闭，点击其他位置才收起
        public static bool KeepOnLeave = false;
        // 行布局互换：名称靠右，大小/子项数与行内按钮靠左（左侧移动不触发悬停调度）
        public static bool RowSwapSides = false;
        // 列表底部格式筛选：关闭后 PopupForm 底部状态栏不显示格式标签（保留拖动/点击展开搜索入口）
        public static bool StatusFilter = true;
        // 互换布局的分栏分隔线位置（行内 X 像素）；0=自动（按钮组+大小列宽度），拖动后持久化
        public static int RowSplitX = 0;
        // 悬停门控：按住所选修饰键才触发，重命名/多选等操作不被弹窗干扰。
        // 文件夹列表与文件预览两套，各自独立开关与键位；门控只拦第一级触发，面板内部免键
        public static bool FolderGateEnabled = false;
        public static int FolderGateMods = 2;   // 位掩码：1=Shift 2=Ctrl 4=Alt 8=长按
        public static bool PreviewGateEnabled = false;
        public static int PreviewGateMods = 2;
        // 长按触发阈值（毫秒）：长按模式下悬停超过此时长才弹面板/预览
        public static int LongPressDelayMs = 300;
        // 视频预览默认静音（办公室友好）：悬停触发的视频先无声播放，点控制条喇叭键恢复
        // 新装默认关闭该开关（即视频预览默认带声音）
        public static bool PreviewMuted = false;
        // 文件预览窗口：标题栏下方显示信息 + 操作栏（类型/大小/行数/时间与复制操作）
        public static bool PreviewInfoBar = true;
        // 信息/操作栏固定字号（pt）；字段名沿用旧配置键，兼容已保存的 config.json
        public static float PreviewInfoBarFontMax = 10f;
        // 信息/操作栏字体族（下拉可选系统字体），默认等宽中文
        public static string PreviewInfoBarFontName = "Microsoft YaHei Mono";
        // 文件夹列表排列方式（全局）：name/size/date + 升降序，切换排序按钮时更新
        public static string SortMode = "date";
        public static bool SortDesc = true;
        public static string Language = "zh";
        public static string Theme = "light";
        // 桌面卡片配色索引（独立于 Theme：面板主题不影响卡片；卡片上滚轮可循环切换）
        public static int CardPaletteIndex = 0;
        public static int LastSettingsPage = 0;
        public static int SettingsW = 0;          // 设置窗持久化宽度（0=默认）
        public static int SettingsH = 0;          // 设置窗持久化高度（0=默认）
        public static string CardPalettesData = "";
        public static List<PinnedItem> PinnedItems = new List<PinnedItem>();
        public static readonly string FontName = "Microsoft YaHei UI";
        public static readonly float FontSize = 10f;
        // 列表面板默认字体：微软雅黑 Mono（等宽，文件名/大小对齐更整齐）；
        // ListFont() 会在该字体未安装时自动回退全局 FontName
        public static readonly string ListFontName = "Microsoft YaHei Mono";
        // ===== 列表面板分区字体：表头/标题/行文本/格式标签/数量·大小信息 =====
        // 默认回退全局 FontName/FontSize；字号默认=各区域现状（见 PopupForm）
        public static string ListHeaderFontName = ListFontName;
        public static float ListHeaderFontSize = FontSize;                       // 表头排序栏：现状 FontSize
        public static string ListTitleFontName = ListFontName;
        public static float ListTitleFontSize = FontSize;                        // 标题行文件夹名：现状 FontSize
        public static string ListItemFontName = ListFontName;
        public static float ListItemFontSize = FontSize;                         // 列表行文本：现状 FontSize
        public static string ListChipFontName = ListFontName;
        public static float ListChipFontSize = Math.Max(7f, FontSize - 1);       // 状态栏格式标签：现状 FontSize-1
        public static string ListMetaFontName = ListFontName;
        public static float ListMetaFontSize = Math.Max(6.5f, FontSize - 2);     // 大小/计数小字：现状 FontSize-2

        // 生成分区字体：保存的字体名未安装（或创建失败）时回退全局 FontName。
        // 注意：必须每次返回新实例，不能缓存共享——设置窗预览等调用方会用 using 释放，
        // 共享实例一旦被释放，后续弹出面板 set_Font 时 GetHeight 会抛"参数无效"导致列表无法显示
        private static Font ListFont(string name, float size, FontStyle style)
        {
            float sz = (size > 0 && !float.IsNaN(size)) ? size : FontSize;
            string fam = FontName;
            try
            {
                using (var ifc = new System.Drawing.Text.InstalledFontCollection())
                    foreach (var ff in ifc.Families)
                        if (string.Equals(ff.Name, name, StringComparison.OrdinalIgnoreCase)) { fam = ff.Name; break; }
            }
            catch { }
            try { return new Font(fam, sz, style); }
            catch { return new Font(FontName, sz, style); }
        }
        public static Font ListHeaderFont() { return ListFont(ListHeaderFontName, ListHeaderFontSize, FontStyle.Regular); }
        public static Font ListTitleFont() { return ListFont(ListTitleFontName, ListTitleFontSize, FontStyle.Bold); }
        public static Font ListTitleFontReg() { return ListFont(ListTitleFontName, ListTitleFontSize, FontStyle.Regular); }
        public static Font ListItemFont() { return ListFont(ListItemFontName, ListItemFontSize, FontStyle.Regular); }
        public static Font ListChipFont() { return ListFont(ListChipFontName, ListChipFontSize, FontStyle.Regular); }
        public static Font ListMetaFont() { return ListFont(ListMetaFontName, ListMetaFontSize, FontStyle.Regular); }
        public static Color BG, FG, BgHover, BgDir, FgDir, DropHighlight, BgSel, Border, PlaceholderFg, Accent, BtnHover;

        static Config()
        {
            DropHighlight = ColorTranslator.FromHtml("#2d7a3d");
            ApplyTheme();
        }

        // 系统应用深浅色（Win10/11：注册表 Personalize\AppsUseLightTheme，0=深色）
        public static bool SystemDark
        {
            get
            {
                try
                {
                    using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    {
                        var v = k == null ? null : k.GetValue("AppsUseLightTheme");
                        if (v is int) return (int)v == 0;
                    }
                }
                catch { }
                return false;
            }
        }
        // Theme 三态："light" / "dark" / "auto"（跟随系统）。全项目深色判断一律走这里
        public static bool IsDark { get { return Theme == "auto" ? SystemDark : Theme != "light"; } }

        public static void ApplyTheme()
        {
            bool dark = IsDark;
            if (dark)
            {
                BG = ColorTranslator.FromHtml("#1e1e24");
                FG = ColorTranslator.FromHtml("#ececf1");
                BgHover = ColorTranslator.FromHtml("#2c2c33");
                BgDir = ColorTranslator.FromHtml("#2a2f38");
                FgDir = ColorTranslator.FromHtml("#6aa9ff");
                BgSel = ColorTranslator.FromHtml("#6aa9ff");
                Border = ColorTranslator.FromHtml("#42424a");
                PlaceholderFg = ColorTranslator.FromHtml("#8d8d99");
                Accent = ColorTranslator.FromHtml("#6aa9ff");
                BtnHover = ColorTranslator.FromHtml("#3d3d47");
            }
            else
            {
                BG = ColorTranslator.FromHtml("#fbfbfd");
                FG = ColorTranslator.FromHtml("#1c1c22");
                BgHover = ColorTranslator.FromHtml("#efeff2");
                BgDir = ColorTranslator.FromHtml("#e8e8f0");
                FgDir = ColorTranslator.FromHtml("#0067c0");
                BgSel = ColorTranslator.FromHtml("#0067c0");
                Border = ColorTranslator.FromHtml("#e8e8ec");
                PlaceholderFg = ColorTranslator.FromHtml("#7a7a88");
                Accent = ColorTranslator.FromHtml("#0067c0");
                BtnHover = ColorTranslator.FromHtml("#dcdfe9");
            }
            ApplyListPalette();
        }

        // 列表配色 15 变量（仅浅色启用自定义；深色=主题色）。用户可粘贴 key=#hex 覆盖
        public static Color ListBg, ListZb, ListFg, ListMu, ListHv, ListSel, ListSelFg, ListBar, ListDrop, ListAc, ListAcFg, ListBd, ListSb, ListHdbg, ListBtnHv, ListCanvas;
        // 用户粘贴的原始文本（key=#hex 每行一个，# 可省略；空=暖灰斑马默认）
        public static string ListPalette = "";
        // 已保存的配色方案列表，格式：每行 name=escapedText（text 中 \n→\\n、=→\\e）；空=无方案
        public static string ListPalettesData = "";

        // 解析已保存的配色方案（与 SettingsForm 的序列化格式一致），供面板滚轮切换使用
        public static List<KeyValuePair<string, string>> PaletteSchemes()
        {
            var list = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrEmpty(ListPalettesData)) return list;
            foreach (var raw in ListPalettesData.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (string.IsNullOrEmpty(line)) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string nm = line.Substring(0, eq).Replace("\\e", "=");
                string tx = line.Substring(eq + 1).Replace("\\n", "\n").Replace("\\e", "=");
                list.Add(new KeyValuePair<string, string>(nm, tx));
            }
            return list;
        }

        public static void ApplyListPalette()
        {
            bool dark = IsDark;
            if (dark)
            {
                ListBg = BG; ListZb = BG; ListFg = FG; ListMu = PlaceholderFg;
                ListHv = BgHover; ListSel = BgSel; ListSelFg = Color.White;
                ListBar = BtnHover; ListDrop = DropHighlight;
                ListAc = Accent; ListAcFg = Color.White; ListBd = Border;
                ListSb = Border; ListHdbg = BgHover; ListBtnHv = BtnHover; ListCanvas = BG;
                return;
            }
            // 浅色默认：暖灰斑马
            ListBg = ColorTranslator.FromHtml("#f7f3ee");
            ListZb = ColorTranslator.FromHtml("#f0e9de");
            ListFg = ColorTranslator.FromHtml("#3b2f25");
            ListMu = ColorTranslator.FromHtml("#8a7664");
            ListHv = ColorTranslator.FromHtml("#ecdcc3");
            ListSel = ColorTranslator.FromHtml("#8a5a3c");
            ListSelFg = ColorTranslator.FromHtml("#ffffff");
            ListBar = ColorTranslator.FromHtml("#e6d2ab");
            ListDrop = ColorTranslator.FromHtml("#2d7a3d");
            ListAc = ColorTranslator.FromHtml("#8a5a3c");
            ListAcFg = ColorTranslator.FromHtml("#ffffff");
            ListBd = ColorTranslator.FromHtml("#d6c7b2");
            ListSb = ColorTranslator.FromHtml("#c4b399");
            ListHdbg = ColorTranslator.FromHtml("#f0e8dc");
            ListBtnHv = ColorTranslator.FromHtml("#e0d0b8");
            ListCanvas = ListBg;   // 列表画布底色默认=行背景；用户可单独设以分离行底与画布底
            if (string.IsNullOrEmpty(ListPalette)) return;
            bool sawCanvas = false, sawHdbg = false;
            foreach (var line in ListPalette.Split('\n'))
            {
                var t = line.Trim().TrimEnd('\\');   // 容错：AI 输出可能每行尾带反斜线/代码块标记
                if (string.IsNullOrEmpty(t) || t.StartsWith("//") || t.StartsWith("```")) continue;
                int eq = t.IndexOf('=');
                if (eq <= 0) continue;
                string k = t.Substring(0, eq).Trim().ToLowerInvariant();
                string v = t.Substring(eq + 1).Trim();
                if (v.StartsWith("#")) v = v.Substring(1);
                // 正则提取 6/8 位十六进制，忽略任何尾随/前导垃圾（反斜线、分号、Markdown 残留等）
                var m = System.Text.RegularExpressions.Regex.Match(v, "[0-9a-fA-F]{6,8}");
                if (!m.Success) continue;
                Color c;
                try { c = ColorTranslator.FromHtml("#" + m.Value); } catch { continue; }
                switch (k)
                {
                    case "bg": ListBg = c; break;
                    case "zb": ListZb = c; break;
                    case "fg": ListFg = c; break;
                    case "mu": ListMu = c; break;
                    case "hv": ListHv = c; break;
                    case "sel": ListSel = c; break;
                    case "selfg": ListSelFg = c; break;
                    case "bar": ListBar = c; break;
                    case "drop": ListDrop = c; break;
                    case "ac": ListAc = c; break;
                    case "acfg": ListAcFg = c; break;
                    case "bd": ListBd = c; break;
                    case "sb": ListSb = c; break;
                    case "hdbg": ListHdbg = c; sawHdbg = true; break;
                    case "btnhv": ListBtnHv = c; break;
                    case "canvas": ListCanvas = c; sawCanvas = true; break;
                }
            }
            // 方案未单独定义画布/表头底时跟随方案背景：否则深色方案下这两处仍是
            // 浅色默认值（滚动条让位区/行区露底、顶栏标题与搜索框残留浅色）
            if (!sawCanvas) ListCanvas = ListBg;
            if (!sawHdbg) ListHdbg = ListBg;
        }

        // 解析配色文本为 15 色字典A字典（基于当前 ListXxx + text 覆盖，不写全局；色块预览用）
        public static Dictionary<string, Color> ParsePalette(string text)
        {
            var map = new Dictionary<string, Color>
            {
                {"bg",ListBg},{"zb",ListZb},{"fg",ListFg},{"mu",ListMu},{"hv",ListHv},
                {"sel",ListSel},{"selfg",ListSelFg},{"bar",ListBar},{"drop",ListDrop},{"ac",ListAc},
                {"acfg",ListAcFg},{"bd",ListBd},{"sb",ListSb},{"hdbg",ListHdbg},{"btnhv",ListBtnHv},{"canvas",ListCanvas},
            };
            if (string.IsNullOrEmpty(text)) return map;
            foreach (var line in text.Split('\n'))
            {
                var t = line.Trim().TrimEnd('\\');
                if (string.IsNullOrEmpty(t) || t.StartsWith("//") || t.StartsWith("```")) continue;
                int eq = t.IndexOf('=');
                if (eq <= 0) continue;
                string k = t.Substring(0, eq).Trim().ToLowerInvariant();
                string v = t.Substring(eq + 1).Trim();
                if (v.StartsWith("#")) v = v.Substring(1);
                var m = System.Text.RegularExpressions.Regex.Match(v, "[0-9a-fA-F]{6,8}");
                if (!m.Success) continue;
                Color c;
                try { c = ColorTranslator.FromHtml("#" + m.Value); } catch { continue; }
                if (map.ContainsKey(k)) map[k] = c;
            }
            return map;
        }
        public static int TextPreviewLines = 10;
        public static readonly HashSet<string> TextExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".markdown", ".py", ".js", ".ts", ".json", ".log",
          ".ini", ".cfg", ".yaml", ".yml", ".xml", ".html", ".css",
          ".ahk", ".bat", ".cmd", ".ps1", ".csv", ".srt", ".toml", ".vue" };
        public static Dictionary<string, string> SortLabels = new Dictionary<string, string>
        { {"name","名称"}, {"size","大小"}, {"date","日期"} };

        public static void RefreshSortLabels()
        {
            SortLabels["name"] = I18n.T("sort_name");
            SortLabels["size"] = I18n.T("sort_size");
            SortLabels["date"] = I18n.T("sort_date");
        }

        public static void Load()
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            var p = Path.Combine(dir, "config.json");
            if (!File.Exists(p)) return;
            try
            {
                var text = File.ReadAllText(p);
                var jss = new JavaScriptSerializer();
                var data = jss.Deserialize<Dictionary<string, object>>(text);
                foreach (var kv in data)
                {
                    string k = kv.Key;
                    var v = kv.Value;
                    switch (k)
                    {
                        case "PreviewDelay": PreviewDelay = Convert.ToInt32(v); break;
                        case "PreviewMax": PreviewMax = Convert.ToInt32(v); break;
                        case "HoverDelay": HoverDelay = Convert.ToDouble(v); break;
                        case "SubHoverDelay": SubHoverDelay = Convert.ToInt32(v); break;
                        case "CloseDelay": CloseDelay = Convert.ToDouble(v); break;
                        case "CheckInterval": CheckInterval = Convert.ToInt32(v); break;
                        case "PopupOffset": PopupOffset = Convert.ToInt32(v); break;
                        case "SubGap": SubGap = Convert.ToInt32(v); break;
                        case "MaxItems": MaxItems = Convert.ToInt32(v); break;
                        case "PreviewMinWidth": PreviewMinWidth = Convert.ToInt32(v); break;
                        case "PreviewOriginalSize": PreviewOriginalSize = Convert.ToBoolean(v); break;
                        case "PreviewImgW": PreviewImgW = Convert.ToInt32(v); break;
                        case "PreviewImgH": PreviewImgH = Convert.ToInt32(v); break;
                        case "PreviewTextW": PreviewTextW = Convert.ToInt32(v); break;
                        case "PreviewTextH": PreviewTextH = Convert.ToInt32(v); break;
                        case "PreviewMediaW": PreviewMediaW = Convert.ToInt32(v); break;
                        case "PreviewMediaH": PreviewMediaH = Convert.ToInt32(v); break;
                        case "PreviewVideoW": PreviewVideoW = Convert.ToInt32(v); break;
                        case "PreviewVideoH": PreviewVideoH = Convert.ToInt32(v); break;
                        case "PreviewAudioW": PreviewAudioW = Convert.ToInt32(v); break;
                        case "PreviewAudioH": PreviewAudioH = Convert.ToInt32(v); break;
                        case "PreviewAudioCollapsed": PreviewAudioCollapsed = Convert.ToBoolean(v); break;
                        case "EntryListW": EntryListW = Convert.ToInt32(v); break;
                        case "EntryListH": EntryListH = Convert.ToInt32(v); break;

                        case "WindowCorners": WindowCorners = Convert.ToInt32(v); break;
                        case "EntryRectDelayMs": EntryRectDelayMs = Convert.ToInt32(v); break;
                        case "PauseRectDelayMs": PauseRectDelayMs = Convert.ToInt32(v); break;
                        case "PreviewMaxRatio": PreviewMaxRatio = Convert.ToDouble(v); break;
                        case "PanelWidth": PanelWidth = Convert.ToInt32(v); break;
                        case "PanelHeight": PanelHeight = Convert.ToInt32(v); break;
                        case "PanelSizeAuto": PanelSizeAuto = Convert.ToBoolean(v); break;
                        case "TextPreviewLines": TextPreviewLines = Convert.ToInt32(v); break;
                        case "ExplorerEnabled": ExplorerEnabled = Convert.ToBoolean(v); break;
                        case "TcEnabled": TcEnabled = Convert.ToBoolean(v); break;
                        case "PerchEnabled": PerchEnabled = Convert.ToBoolean(v); break;
                        case "TcSortMode": TcSortMode = Convert.ToInt32(v); break;
                        case "PreviewFiles": PreviewFiles = Convert.ToBoolean(v); break;
                        case "PreviewImage": PreviewImage = Convert.ToBoolean(v); break;
                        case "PreviewVideo": PreviewVideo = Convert.ToBoolean(v); break;
                        case "VideoPreviewDelay": VideoPreviewDelay = Convert.ToDouble(v); break;
                        case "VideoPreviewDelayEnabled": VideoPreviewDelayEnabled = Convert.ToBoolean(v); break;
                        case "PreviewVolume": PreviewVolume = Convert.ToDouble(v); break;
                        case "PreviewEdgeMode": PreviewEdgeMode = Convert.ToInt32(v); break;
                        case "PreviewEdgeWidth": PreviewEdgeWidth = Convert.ToInt32(v); break;
                        case "PreviewAudio": PreviewAudio = Convert.ToBoolean(v); break;
                        case "PreviewDocument": PreviewDocument = Convert.ToBoolean(v); break;
                        case "PreviewText": PreviewText = Convert.ToBoolean(v); break;
                        case "PreviewArchive": PreviewArchive = Convert.ToBoolean(v); break;
                        case "PreviewFont": PreviewFont = Convert.ToBoolean(v); break;
                        case "PreviewOther": PreviewOther = Convert.ToBoolean(v); break;
                        // 旧版单开关迁移：新键不存在时才用旧值初始化，避免覆盖用户分别设置的视频/音频倍速
                        case "SpeedToggle":
                            {
                                bool legacy = Convert.ToBoolean(v);
                                if (!data.ContainsKey("VideoSpeedToggle")) VideoSpeedToggle = legacy;
                                if (!data.ContainsKey("AudioSpeedToggle")) AudioSpeedToggle = legacy;
                                break;
                            }
                        case "VideoSpeedToggle": VideoSpeedToggle = Convert.ToBoolean(v); break;
                        case "AudioSpeedToggle": AudioSpeedToggle = Convert.ToBoolean(v); break;
                        case "WaveformEnabled": WaveformEnabled = Convert.ToBoolean(v); break;
                        case "SpectrumEnabled": SpectrumEnabled = Convert.ToBoolean(v); break;
                        case "FfmpegPath": FfmpegPath = Convert.ToString(v); break;
                        case "DebugLog": DebugLog = Convert.ToBoolean(v); break;
                        case "HideTooltip": HideTooltip = Convert.ToBoolean(v); break;
                        case "ShowTips": ShowTips = Convert.ToBoolean(v); break;
                        case "HideTrayIcon": HideTrayIcon = Convert.ToBoolean(v); break;
                        case "TrayIconPath": TrayIconPath = Convert.ToString(v); break;
                        case "TrayShowExplorer": TrayShowExplorer = Convert.ToBoolean(v); break;
                        case "TrayShowPreview": TrayShowPreview = Convert.ToBoolean(v); break;
                        case "TrayShowOrigSize": TrayShowOrigSize = Convert.ToBoolean(v); break;
                        case "TrayShowDebugLog": TrayShowDebugLog = Convert.ToBoolean(v); break;
                        case "TrayShowHideTooltip": TrayShowHideTooltip = Convert.ToBoolean(v); break;
                        case "TrayShowButtonTips": TrayShowButtonTips = Convert.ToBoolean(v); break;
                        case "TrayShowAutostart": TrayShowAutostart = Convert.ToBoolean(v); break;
                        case "TrayShowHideTray": TrayShowHideTray = Convert.ToBoolean(v); break;
                        case "TrayShowAddCard": TrayShowAddCard = Convert.ToBoolean(v); break;
                        case "TrayMiddleAction": TrayMiddleAction = Convert.ToInt32(v); break;
                        case "HotkeyEnabled": HotkeyEnabled = Convert.ToBoolean(v); break;
                        case "HotkeyModifiers": HotkeyModifiers = Convert.ToInt32(v); break;
                        case "HotkeyKey": HotkeyKey = Convert.ToInt32(v); break;
                        case "Language": Language = Convert.ToString(v); break;
                        case "Theme": Theme = Convert.ToString(v); break;
                        case "CardPaletteIndex": CardPaletteIndex = Convert.ToInt32(v); break;
                        case "LastSettingsPage": LastSettingsPage = Convert.ToInt32(v); break;
                        case "SettingsW": SettingsW = Convert.ToInt32(v); break;
                        case "SettingsH": SettingsH = Convert.ToInt32(v); break;
                        case "PinnedItems":
                            PinnedItems = new List<PinnedItem>();
                            foreach (var item in (System.Collections.ArrayList)v)
                            {
                                var d = (Dictionary<string, object>)item;
                                var pi = new PinnedItem { Path = Convert.ToString(d["Path"]), X = Convert.ToInt32(d["X"]), Y = Convert.ToInt32(d["Y"]) };
                                if (d.ContainsKey("Kind")) pi.Kind = Convert.ToString(d["Kind"]);
                                if (string.IsNullOrEmpty(pi.Kind)) pi.Kind = "file";
                                if (d.ContainsKey("Title")) pi.Title = Convert.ToString(d["Title"]);
                                if (d.ContainsKey("Palette")) pi.Palette = Convert.ToInt32(d["Palette"]);
                                PinnedItems.Add(pi);
                            }
                            break;
                        case "CardHotkeyEnabled": CardHotkeyEnabled = Convert.ToBoolean(v); break;
                        case "CardHotkeyModifiers": CardHotkeyModifiers = Convert.ToInt32(v); break;
                        case "CardHotkeyKey": CardHotkeyKey = Convert.ToInt32(v); break;
                        case "EntryHotkeyEnabled": EntryHotkeyEnabled = Convert.ToBoolean(v); break;
                        case "EntryHotkeyModifiers": EntryHotkeyModifiers = Convert.ToInt32(v); break;
                        case "EntryHotkeyKey": EntryHotkeyKey = Convert.ToInt32(v); break;
                        case "EntryRectX": EntryRectX = Convert.ToInt32(v); break;
                        case "EntryRectY": EntryRectY = Convert.ToInt32(v); break;
                        case "EntryRectW": EntryRectW = Convert.ToInt32(v); break;
                        case "EntryRectH": EntryRectH = Convert.ToInt32(v); break;
                        case "PauseRectX": PauseRectX = Convert.ToInt32(v); break;
                        case "PauseRectY": PauseRectY = Convert.ToInt32(v); break;
                        case "PauseRectW": PauseRectW = Convert.ToInt32(v); break;
                        case "PauseRectH": PauseRectH = Convert.ToInt32(v); break;
                        case "EntryRectColor": EntryRectColor = Convert.ToInt32(v); break;
                        case "FavoriteFolders":
                            FavoriteFolders = new List<string>();
                            foreach (var f in (System.Collections.ArrayList)v) FavoriteFolders.Add(Convert.ToString(f));
                            break;
                        case "FavoriteCurGroup": FavoriteCurGroup = Convert.ToInt32(v); break;
                        case "FavoriteGroups":
                            FavoriteGroups = new List<FavoriteGroup>();
                            foreach (var g in (System.Collections.ArrayList)v)
                            {
                                var gd = g as Dictionary<string, object>;
                                if (gd == null) continue;
                                var grp = new FavoriteGroup();
                                if (gd.ContainsKey("Name")) grp.Name = Convert.ToString(gd["Name"]);
                                if (gd.ContainsKey("Color")) grp.Color = Convert.ToString(gd["Color"]);
                                if (gd.ContainsKey("Items"))
                                {
                                    var al = gd["Items"] as System.Collections.ArrayList;
                                    if (al != null)
                                        foreach (var it in al) grp.Items.Add(Convert.ToString(it));
                                }
                                FavoriteGroups.Add(grp);
                            }
                            break;
                        case "RecentFolders":
                            RecentFolders = new List<string>();
                            foreach (var f in (System.Collections.ArrayList)v) RecentFolders.Add(Convert.ToString(f));
                            break;
                        case "KeepOnLeave": KeepOnLeave = Convert.ToBoolean(v); break;
                        case "RowSwapSides": RowSwapSides = Convert.ToBoolean(v); break;
                        case "StatusFilter": StatusFilter = Convert.ToBoolean(v); break;
                        case "RowSplitX": RowSplitX = Convert.ToInt32(v); break;
                        case "FolderGateEnabled": FolderGateEnabled = Convert.ToBoolean(v); break;
                        case "FolderGateMods": FolderGateMods = Convert.ToInt32(v); break;
                        case "PreviewGateEnabled": PreviewGateEnabled = Convert.ToBoolean(v); break;
                        case "PreviewGateMods": PreviewGateMods = Convert.ToInt32(v); break;
                        case "LongPressDelayMs": LongPressDelayMs = Convert.ToInt32(v); break;
                        case "PreviewMuted": PreviewMuted = Convert.ToBoolean(v); break;
                        case "PreviewInfoBar": PreviewInfoBar = Convert.ToBoolean(v); break;
                        case "PreviewInfoBarFontMax": PreviewInfoBarFontMax = (float)Convert.ToDouble(v); break;
                        case "PreviewInfoBarFontName":
                            {
                                string s = Convert.ToString(v);
                                if (!string.IsNullOrEmpty(s)) PreviewInfoBarFontName = s;
                                break;
                            }
                        case "ListPalette": ListPalette = Convert.ToString(v); break;
                        case "ListPalettesData": ListPalettesData = Convert.ToString(v); break;
                        case "CardPalettesData": CardPalettesData = Convert.ToString(v); break;
                        case "SortMode": SortMode = Convert.ToString(v); break;
                        case "SortDesc": SortDesc = Convert.ToBoolean(v); break;
                        case "ListHeaderFontName": ListHeaderFontName = Convert.ToString(v); break;
                        case "ListHeaderFontSize": ListHeaderFontSize = Convert.ToSingle(v); break;
                        case "ListTitleFontName": ListTitleFontName = Convert.ToString(v); break;
                        case "ListTitleFontSize": ListTitleFontSize = Convert.ToSingle(v); break;
                        case "ListItemFontName": ListItemFontName = Convert.ToString(v); break;
                        case "ListItemFontSize": ListItemFontSize = Convert.ToSingle(v); break;
                        case "ListChipFontName": ListChipFontName = Convert.ToString(v); break;
                        case "ListChipFontSize": ListChipFontSize = Convert.ToSingle(v); break;
                        case "ListMetaFontName": ListMetaFontName = Convert.ToString(v); break;
                        case "ListMetaFontSize": ListMetaFontSize = Convert.ToSingle(v); break;
                    }
                }
                // 旧格式迁移：FavoriteFolders(扁平) → FavoriteGroups(多分组) 首组
                if (FavoriteGroups.Count == 0)
                {
                    var first = new FavoriteGroup(I18n.T("fav_default_name"), "#4a7ab5");
                    first.Items = new List<string>(FavoriteFolders);
                    FavoriteGroups.Add(first);
                }
                // 兜底：至少一个分组
                if (FavoriteGroups.Count == 0)
                    FavoriteGroups.Add(new FavoriteGroup(I18n.T("fav_default_name"), "#4a7ab5"));
                if (FavoriteCurGroup < -1 || FavoriteCurGroup >= FavoriteGroups.Count) FavoriteCurGroup = 0;
                I18n.Lang = Language;
                RefreshSortLabels();
                ApplyTheme();
                CardPalettes.Load();
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                var dir = AppDomain.CurrentDomain.BaseDirectory;
                var p = Path.Combine(dir, "config.json");
                var data = new Dictionary<string, object>();
                if (File.Exists(p))
                {
                    var text = File.ReadAllText(p);
                    var jss = new JavaScriptSerializer();
                    var existing = jss.Deserialize<Dictionary<string, object>>(text);
                    if (existing != null) data = existing;
                }
                data["ExplorerEnabled"] = ExplorerEnabled;
                data["TcEnabled"] = TcEnabled;
                data["PerchEnabled"] = PerchEnabled;
                data["TcSortMode"] = TcSortMode;
                data["PreviewFiles"] = PreviewFiles;
                data["PreviewImage"] = PreviewImage;
                data["PreviewVideo"] = PreviewVideo;
                data["VideoPreviewDelay"] = VideoPreviewDelay;
                data["VideoPreviewDelayEnabled"] = VideoPreviewDelayEnabled;
                data["PreviewVolume"] = PreviewVolume;
                data["PreviewEdgeMode"] = PreviewEdgeMode;
                data["PreviewEdgeWidth"] = PreviewEdgeWidth;
                data["PreviewAudio"] = PreviewAudio;
                data["PreviewDocument"] = PreviewDocument;
                data["PreviewText"] = PreviewText;
                data["PreviewArchive"] = PreviewArchive;
                data["PreviewFont"] = PreviewFont;
                data["PreviewOther"] = PreviewOther;
                data["SpeedToggle"] = SpeedToggle;   // 保留旧键，供旧版本/旧面板回退读取
                data["VideoSpeedToggle"] = VideoSpeedToggle;
                data["AudioSpeedToggle"] = AudioSpeedToggle;
                data["WaveformEnabled"] = WaveformEnabled;
                data["SpectrumEnabled"] = SpectrumEnabled;
                data["FfmpegPath"] = FfmpegPath;
                data["DebugLog"] = DebugLog;
                data["HideTooltip"] = HideTooltip;
                data["ShowTips"] = ShowTips;
                data["HideTrayIcon"] = HideTrayIcon;
                data["TrayIconPath"] = TrayIconPath;
                data["TrayShowExplorer"] = TrayShowExplorer;
                data["TrayShowPreview"] = TrayShowPreview;
                data["TrayShowOrigSize"] = TrayShowOrigSize;
                data["TrayShowDebugLog"] = TrayShowDebugLog;
                data["TrayShowHideTooltip"] = TrayShowHideTooltip;
                data["TrayShowButtonTips"] = TrayShowButtonTips;
                data["TrayShowAutostart"] = TrayShowAutostart;
                data["TrayShowHideTray"] = TrayShowHideTray;
                data["TrayShowAddCard"] = TrayShowAddCard;
                data["TrayMiddleAction"] = TrayMiddleAction;
                data["HotkeyEnabled"] = HotkeyEnabled;
                data["HotkeyModifiers"] = HotkeyModifiers;
                data["HotkeyKey"] = HotkeyKey;
                data["CardHotkeyEnabled"] = CardHotkeyEnabled;
                data["CardHotkeyModifiers"] = CardHotkeyModifiers;
                data["CardHotkeyKey"] = CardHotkeyKey;
                data["EntryHotkeyEnabled"] = EntryHotkeyEnabled;
                data["EntryHotkeyModifiers"] = EntryHotkeyModifiers;
                data["EntryHotkeyKey"] = EntryHotkeyKey;
                data["EntryRectX"] = EntryRectX;
                data["EntryRectY"] = EntryRectY;
                data["EntryRectW"] = EntryRectW;
                data["EntryRectH"] = EntryRectH;
                data["PauseRectX"] = PauseRectX;
                data["PauseRectY"] = PauseRectY;
                data["PauseRectW"] = PauseRectW;
                data["PauseRectH"] = PauseRectH;
                data["EntryRectColor"] = EntryRectColor;
                data["FavoriteFolders"] = FavoriteFolders;
                data["FavoriteCurGroup"] = FavoriteCurGroup;
                {
                    var gl = new List<Dictionary<string, object>>();
                    foreach (var g in FavoriteGroups)
                        gl.Add(new Dictionary<string, object> { { "Name", g.Name }, { "Color", g.Color }, { "Items", g.Items } });
                    data["FavoriteGroups"] = gl;
                }
                data["RecentFolders"] = RecentFolders;
                data["KeepOnLeave"] = KeepOnLeave;
                data["RowSwapSides"] = RowSwapSides;
                data["StatusFilter"] = StatusFilter;
                data["RowSplitX"] = RowSplitX;
                data["FolderGateEnabled"] = FolderGateEnabled;
                data["FolderGateMods"] = FolderGateMods;
                data["PreviewGateEnabled"] = PreviewGateEnabled;
                data["PreviewGateMods"] = PreviewGateMods;
                data["LongPressDelayMs"] = LongPressDelayMs;
                data["PreviewMuted"] = PreviewMuted;
                data["PreviewInfoBar"] = PreviewInfoBar;
                data["PreviewInfoBarFontMax"] = PreviewInfoBarFontMax;
                data["PreviewInfoBarFontName"] = PreviewInfoBarFontName;
                data["ListPalette"] = ListPalette;
                data["ListPalettesData"] = ListPalettesData;
                data["SortMode"] = SortMode;
                data["SortDesc"] = SortDesc;
                data["ListHeaderFontName"] = ListHeaderFontName;
                data["ListHeaderFontSize"] = ListHeaderFontSize;
                data["ListTitleFontName"] = ListTitleFontName;
                data["ListTitleFontSize"] = ListTitleFontSize;
                data["ListItemFontName"] = ListItemFontName;
                data["ListItemFontSize"] = ListItemFontSize;
                data["ListChipFontName"] = ListChipFontName;
                data["ListChipFontSize"] = ListChipFontSize;
                data["ListMetaFontName"] = ListMetaFontName;
                data["ListMetaFontSize"] = ListMetaFontSize;
                data["Language"] = Language;
                data["Theme"] = Theme;
                data["CardPaletteIndex"] = CardPaletteIndex;
                data["LastSettingsPage"] = LastSettingsPage;
                data["SettingsW"] = SettingsW;
                data["SettingsH"] = SettingsH;
                data["CardPalettesData"] = CardPalettesData;
                data["HoverDelay"] = HoverDelay;
                data["CloseDelay"] = CloseDelay;
                data["PreviewMinWidth"] = PreviewMinWidth;
                data["PreviewOriginalSize"] = PreviewOriginalSize;
                data["PreviewImgW"] = PreviewImgW;
                data["PreviewImgH"] = PreviewImgH;
                data["PreviewTextW"] = PreviewTextW;
                data["PreviewTextH"] = PreviewTextH;
                data["PreviewMediaW"] = PreviewMediaW;
                data["PreviewMediaH"] = PreviewMediaH;
                data["PreviewVideoW"] = PreviewVideoW;
                data["PreviewVideoH"] = PreviewVideoH;
                data["PreviewAudioW"] = PreviewAudioW;
                data["PreviewAudioH"] = PreviewAudioH;
                data["PreviewAudioCollapsed"] = PreviewAudioCollapsed;
                data["EntryListW"] = EntryListW;
                data["EntryListH"] = EntryListH;

                data["WindowCorners"] = WindowCorners;
                data["EntryRectDelayMs"] = EntryRectDelayMs;
                data["PauseRectDelayMs"] = PauseRectDelayMs;
                data["PreviewMaxRatio"] = PreviewMaxRatio;
                data["PanelWidth"] = PanelWidth;
                data["PanelHeight"] = PanelHeight;
                data["PanelSizeAuto"] = PanelSizeAuto;
                data["PinnedItems"] = PinnedItems;
                var jss2 = new JavaScriptSerializer();
                File.WriteAllText(p, jss2.Serialize(data));
            }
            catch { }
        }

        public static string FmtSize(long n)
        {
            double d = n;
            string[] units = { "B", "KB", "MB", "GB" };
            for (int i = 0; i < units.Length; i++)
            {
                if (d < 1024 || i == 3)
                    return i == 0 ? string.Format("{0:0} {1}", d, units[i]) : string.Format("{0:0.0} {1}", d, units[i]);
                d /= 1024;
            }
            return string.Format("{0:0.0} GB", d);
        }
    }

    internal class PinnedItem
    {
        public string Kind = "file";   // file / url / text
        public string Path = "";       // 文件路径 / URL / 文本内容
        public string Title = "";      // 可选显示标题（url/text）
        public int X;
        public int Y;
        public int Palette = -1;       // 该卡片自己的配色索引；-1 = 跟随默认（Config.CardPaletteIndex）
    }

    // 收藏分组：名称 + 色点 + 条目列表（原型 A 分组标签）
    internal class FavoriteGroup
    {
        public string Name = "";
        public string Color = "#4a7ab5";
        public List<string> Items = new List<string>();
        public FavoriteGroup() { }
        public FavoriteGroup(string n, string c) { Name = n; Color = c; }
    }
}