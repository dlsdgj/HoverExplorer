using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;

namespace HoverExplorer
{
    // 参考原型方案 A（紧凑手风琴）重排：
    // 分组标题行 = 状态点 + 标题 + 摘要 chip + 主开关 + 箭头；单开手风琴 + 全部展开/收起；
    // 内容区独立滚动，确定/取消固定底栏。「默认排序」独立成组（非 TC 设置项）。
    internal class SettingsForm : Form
    {
        private const int PadX = 24;
        private const int RowH = 46;
        private const int HeadAreaH = 62;
        private const int DonateH = 160;   // 赞赏区（固定底栏上方）：二维码150 + 边距
        private const int ScrollBarW = 10; // SlimScrollBar 占宽

        private readonly List<AccGroup> _groups = new List<AccGroup>();
        private AccGroup _hoverGroup;
        private AccGroup _cardGroup;
        private AccGroup _entryGroup;
        private AccGroup _sortGroup;
        private AccGroup _tcGroup;
        private AccGroup _pvTypeGroup;
        private ViewportPanel _viewport;
        private FlowLayoutPanel _scrollPanel;
        private SlimScrollBar _scrollBar;
        private Button _expandBtn;
        private Label _closeBtn;
        private Label _verLbl;
        private LinkLabel _updateLbl;
        private static string _latestVer; // null=未检测, ""=失败, "x.x.x"=最新版

        private ToggleSwitch _hoverToggle;
        private ToggleSwitch _cardToggle;
        private ToggleSwitch _keepToggle;
        private ToggleSwitch _rowSwapToggle;
        private ToggleSwitch _altSuppressToggle;
        private DarkCombo _modeExplorer;  // 悬停浏览模式：0 关闭 1 总是 2 按住Ctrl 3 按住Shift 4 按住Alt
        private Label _altGateHint;       // 门控键=Alt 时的互斥说明（动态显隐）
        private bool _internalSync;       // 托盘→设置回写时抑制 SelectedIndexChanged 的反向应用
        private DarkCombo _modePreview;   // 文件预览模式（同上）
        private ToggleSwitch _pvMuteToggle;         // 视频预览默认静音
        private ToggleSwitch _entryToggle;
        // 预览类型分类开关（图片/视频/音频/文档/文本/压缩包/字体/其他）
        private ToggleSwitch _pvImageToggle;
        private ToggleSwitch _pvVideoToggle;
        private ToggleSwitch _pvAudioToggle;
        private ToggleSwitch _pvDocToggle;
        private ToggleSwitch _pvTextToggle;
        private ToggleSwitch _pvArchiveToggle;
        private ToggleSwitch _pvFontToggle;
        private ToggleSwitch _pvOtherToggle;
        private ToggleSwitch _pvSpeedToggle;
        private TextBox _ffmpegPathBox;
        private Label _ffmpegStatusLbl;
        private HotkeyPill _hoverPill;
        private HotkeyPill _cardPill;
        private HotkeyPill _entryPill;
        private DelayStepper _delayStepper;
        private MsStepper _closeDelayStepper;
        private MsStepper _entryDelayStepper;

        private DarkCombo _cornerCombo;
        private DarkCombo _tcSortCombo;
        private DarkCombo _sortCombo;
        private Label _areaLbl;
        private Button _areaPickBtn;
        private Button _pausePickBtn;
        private Button _pauseClearBtn;
        private Label _pauseRectLbl;
        private Button _areaClearBtn;
        private Panel _areaColorBlock;
        private int _entryColor;
        private static readonly int[] _areaColors = {
            0x283B8EEA, 0x2852A883, 0x28F0B429, 0x289B5DE5,
            0x28E5567A, 0x2814B8A6, 0x28C98A2B, 0x28888888,
        };
        private Rectangle _entryRect;

        private int _hoverMods;
        private int _hoverKey;
        private int _cardMods;
        private int _cardKey;
        private int _entryMods;
        private int _entryKey;
        private bool _hoverRecording;
        private bool _cardRecording;
        private bool _entryRecording;

        private static LinkLabel MakeLink(string text, int x, int y, string url)
        {
            var link = new LinkLabel
            {
                Text = text,
                Location = new Point(x, y),
                AutoSize = true,
                Font = new Font(Config.FontName, 8.5f),
                Tag = "sub",
                LinkArea = new LinkArea(0, text.Length)
            };
            link.Click += (s, e) => OpenUrl(url);
            return link;
        }

        private static void OpenUrl(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }

        public SettingsForm()
        {
            Text = I18n.T("settings");
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            Width = 500;
            Height = Math.Min(720, Screen.FromPoint(Cursor.Position).WorkingArea.Height - 60);
            ShowInTaskbar = false;
            DoubleBuffered = true;
            KeyPreview = true;

            _hoverMods = Config.HotkeyModifiers;
            _hoverKey = Config.HotkeyKey;
            _cardMods = Config.CardHotkeyModifiers;
            _cardKey = Config.CardHotkeyKey;
            _entryMods = Config.EntryHotkeyModifiers;
            _entryKey = Config.EntryHotkeyKey;
            _entryRect = Config.EntryRectW >= 5 ? new Rectangle(Config.EntryRectX, Config.EntryRectY, Config.EntryRectW, Config.EntryRectH) : Rectangle.Empty;
            _entryColor = Array.IndexOf(_areaColors, Config.EntryRectColor) >= 0 ? Config.EntryRectColor : _areaColors[0];

            // ---- 头部（固定）：标题 + 副标题 + 全部展开/收起 + 关闭 ----
            // 标题栏底色由 OnPaint 绘制（与组标题 BgHover 同色）
            var titleLbl = new Label
            {
                Text = I18n.T("settings"),
                Location = new Point(PadX, 12),
                AutoSize = true,
                Font = new Font(Config.FontName, 13f, FontStyle.Bold),
                BackColor = Config.BgHover
            };
            titleLbl.MouseDown += (s, e) => _fnDrag(e);
            Controls.Add(titleLbl);

            var subLbl = new Label
            {
                Text = I18n.T("settings_subtitle"),
                Location = new Point(PadX, 40),
                AutoSize = true,
                ForeColor = Config.PlaceholderFg,
                Font = new Font(Config.FontName, 8.5f),
                Tag = "sub",
                BackColor = Config.BgHover
            };
            subLbl.MouseDown += (s, e) => _fnDrag(e);
            Controls.Add(subLbl);

            _closeBtn = new Label
            {
                Text = "×",
                Size = new Size(28, 24),
                Location = new Point(Width - 32, 8),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font(Config.FontName, 11),
                Cursor = Cursors.Hand
            };
            _closeBtn.Click += (s, e) => Close();
            _closeBtn.MouseEnter += (s, e) => _closeBtn.BackColor = Config.BgHover;
            _closeBtn.MouseLeave += (s, e) => _closeBtn.BackColor = Config.BG;
            Controls.Add(_closeBtn);

            _expandBtn = new Button
            {
                Text = I18n.T("settings_expand"),
                FlatStyle = FlatStyle.Flat,
                BackColor = Config.BG,
                ForeColor = Config.FG,
                Font = new Font(Config.FontName, 8.5f),
                AutoSize = true,
                Height = 24,
                Cursor = Cursors.Hand,
                Tag = "plain"
            };
            _expandBtn.FlatAppearance.BorderSize = 1;
            _expandBtn.FlatAppearance.MouseOverBackColor = Config.BgHover;
            _expandBtn.Click += (s, e) =>
            {
                bool anyClosed = false;
                foreach (var g in _groups) if (!g.Open) { anyClosed = true; break; }
                foreach (var g in _groups) g.Open = anyClosed;
                _fnSyncScroll();
                _fnUpdateExpandBtn();
            };
            _fnPlaceExpandBtn();
            Controls.Add(_expandBtn);

            // ---- 内容滚动区（SlimScrollBar 自绘：无轨道 4px 滑块、悬停 8px；FlowLayoutPanel 垂直堆叠，组高度变化时下方自动下移）----
            // 结构：viewport（固定可视区，裁切溢出）→ _scrollPanel（内容层，Y 偏移滚动）+ _scrollBar（Dock Right）
            int viewH = Height - HeadAreaH;   // 赞助区已并入"关于"折叠组随滚动，不再预留固定高度；底部无按钮栏
            _viewport = new ViewportPanel
            {
                Location = new Point(0, HeadAreaH),
                Size = new Size(Width, viewH),
                BackColor = Config.BG
            };
            _scrollPanel = new FlowLayoutPanel
            {
                Location = new Point(0, 0),
                Size = new Size(Width - ScrollBarW, viewH),
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = false,
                Padding = new Padding(PadX, 8, 0, 0),
                BackColor = Config.BG
            };
            // FlowLayoutPanel.DoubleBuffered 受保护：反射开启，滚动移动时整列子控件一次合成绘制，消除卡顿闪烁
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(_scrollPanel, true, null);
            bool dark0 = Config.IsDark;
            _scrollBar = new SlimScrollBar
            {
                Style = SlimScrollBar.BarStyle.Thin,
                ThumbColor = dark0 ? ColorTranslator.FromHtml("#4a5260") : ColorTranslator.FromHtml("#b8bec8"),
                ThumbHotColor = dark0 ? ColorTranslator.FromHtml("#6a7484") : ColorTranslator.FromHtml("#8a919b"),
            };
            _scrollBar.Scroll += (s, e) => _scrollPanel.Location = new Point(0, -_scrollBar.Value);
            _viewport.Controls.Add(_scrollPanel);
            _viewport.Controls.Add(_scrollBar);
            Controls.Add(_viewport);

            // ==== 组 1：悬停预览 ====
            _hoverGroup = _fnNewGroup(I18n.T("group_hover"), false);
            _hoverToggle = new ToggleSwitch { On = Config.HotkeyEnabled };
            _hoverToggle.Toggled += (s, e) => { _fnUpdateHoverState(); _fnApplySettings(true, false); };
            _hoverGroup.SetToggle(_hoverToggle);
            _hoverGroup.AddChip(HotkeyDisplay(Config.HotkeyEnabled, _hoverMods, _hoverKey));
            _hoverGroup.AddChip(string.Format("{0:0.00} {1}", Config.HoverDelay, I18n.T("delay_unit")));
            _hoverGroup.AddChip(Config.CloseDelayMs + " ms");

            int y = 8;
            _hoverPill = new HotkeyPill();
            _hoverPill.SetHotkey(_hoverMods, _hoverKey);
            _hoverPill.RecordClick += (s, e) => _fnStartHoverCapture();
            y = _hoverGroup.AddRow(y, I18n.T("hotkey_row"), I18n.T("hotkey_desc"), _hoverPill);

            // 暂停触控区：独立框选矩形（设置方式复制触发区域那套：框选按钮+清除+矩形标签+桌面标识块）。
            // 鼠标驻留该区域 0.3s 切换悬停启用/暂停；未框选时仅剩快捷键 Ctrl+Alt+Q
            int pauseRowY = y;
            y = _hoverGroup.AddRow(y, I18n.T("pause_zone_row"), null, null);
            _pauseRectLbl = new Label
            {
                Text = I18n.T("area_none"),
                Location = new Point(AccGroup.InL, AccGroup.HeadH + pauseRowY + 19),
                AutoSize = true,
                ForeColor = Config.PlaceholderFg,
                Font = new Font(Config.FontName, 8f),
                Tag = "sub"
            };
            _hoverGroup.Controls.Add(_pauseRectLbl);
            _pausePickBtn = new Button
            {
                Text = I18n.T("area_pick"),
                FlatStyle = FlatStyle.Flat,
                BackColor = Config.BG,
                ForeColor = Config.FG,
                Font = new Font(Config.FontName, 8.5f),
                Size = new Size(64, 24),
                Cursor = Cursors.Hand
            };
            _pausePickBtn.FlatAppearance.BorderColor = Config.Border;
            _pausePickBtn.FlatAppearance.MouseOverBackColor = Config.BgHover;
            _pausePickBtn.Click += (s, e) => _fnPickPauseZone();
            _pauseClearBtn = new Button
            {
                Text = I18n.T("area_clear"),
                FlatStyle = FlatStyle.Flat,
                BackColor = Config.BG,
                ForeColor = Config.FG,
                Font = new Font(Config.FontName, 8.5f),
                Size = new Size(60, 24),
                Cursor = Cursors.Hand
            };
            _pauseClearBtn.FlatAppearance.BorderColor = Config.Border;
            _pauseClearBtn.FlatAppearance.MouseOverBackColor = Config.BgHover;
            _pauseClearBtn.Click += (s, e) =>
            {
                Config.PauseRectW = 0; Config.PauseRectH = 0;
                _fnUpdatePauseLbl();
                Program.UpdatePauseIndicator();
                _fnApplySettings(false, false);
            };
            int pauseBtnY = AccGroup.HeadH + pauseRowY;
            _pausePickBtn.Location = new Point(_hoverGroup.Width - AccGroup.InR - _pausePickBtn.Width, pauseBtnY);
            _pauseClearBtn.Location = new Point(_pausePickBtn.Left - _pauseClearBtn.Width - 8, pauseBtnY);
            _hoverGroup.Controls.Add(_pausePickBtn);
            _hoverGroup.Controls.Add(_pauseClearBtn);
            _hoverGroup.ExpandRow(pauseRowY, Math.Max(RowH, 19 + _pauseRectLbl.PreferredHeight + 8));
            _fnUpdatePauseLbl();

            _delayStepper = new DelayStepper { Value = Config.HoverDelay };
            _delayStepper.ValueChanged += (s, e) => { _fnRefreshChips(); _fnApplySettings(false, false); };
            y = _hoverGroup.AddRow(y, I18n.T("delay_row"), I18n.T("hover_delay_desc"), _delayStepper);

            _closeDelayStepper = new MsStepper { Value = Config.CloseDelayMs };
            _closeDelayStepper.ValueChanged += (s, e) => { _fnRefreshChips(); _fnApplySettings(false, false); };
            y = _hoverGroup.AddRow(y, I18n.T("close_delay_row"), I18n.T("close_delay_desc"), _closeDelayStepper);

            // 触发区域（收藏·历史入口）独立的悬停延时，不随文件夹悬停延时联动
            _entryDelayStepper = new MsStepper { Value = Config.EntryRectDelayMs };
            _entryDelayStepper.ValueChanged += (s, e) => _fnApplySettings(false, false);
            y = _hoverGroup.AddRow(y, I18n.T("entry_delay_row"), I18n.T("entry_delay_desc"), _entryDelayStepper);

            _keepToggle = new ToggleSwitch { On = Config.KeepOnLeave };
            _keepToggle.Toggled += (s, e) => _fnApplySettings(false, false);
            y = _hoverGroup.AddRow(y, I18n.T("keep_open"), I18n.T("keep_open_desc"), _keepToggle);

            _rowSwapToggle = new ToggleSwitch { On = Config.RowSwapSides };
            // 行布局互换影响已开面板的按钮位置 → 应用时刷新存活面板
            _rowSwapToggle.Toggled += (s, e) => _fnApplySettings(false, true);
            y = _hoverGroup.AddRow(y, I18n.T("row_swap"), I18n.T("row_swap_desc"), _rowSwapToggle);

            _altSuppressToggle = new ToggleSwitch { On = Config.AltSuppressHover };
            _altSuppressToggle.Toggled += (s, e) => _fnApplySettings(false, false);
            y = _hoverGroup.AddRow(y, I18n.T("alt_suppress"), I18n.T("alt_suppress_desc"), _altSuppressToggle);

            // 悬停浏览（桌面/资源管理器）与文件预览：各一个下拉搞定——关闭 / 总是 / 按住某键才触发。
            // 即改即生效，ApplySettingsLive 会把托盘勾选态同步
            _modeExplorer = _fnMakeModeCombo(ModeFromConfig(Config.ExplorerEnabled, Config.FolderGateEnabled, Config.FolderGateMods));
            _modeExplorer.SelectedIndexChanged += (s, e) => { _fnUpdateAltGateHint(); _fnApplySettings(false, false); };
            y = _hoverGroup.AddRow(y, I18n.T("explorer_hover"), I18n.T("mode_desc"), _modeExplorer);

            _modePreview = _fnMakeModeCombo(ModeFromConfig(Config.PreviewFiles, Config.PreviewGateEnabled, Config.PreviewGateMods));
            _modePreview.SelectedIndexChanged += (s, e) => { _fnUpdateAltGateHint(); _fnApplySettings(false, false); };
            y = _hoverGroup.AddRow(y, I18n.T("file_preview"), I18n.T("mode_desc"), _modePreview);

            // Alt 互斥提示行：任一模式选了"按住 Alt"就亮出说明（门控键=Alt 时按 Alt 是放行不是禁用）。
            // 空间常驻（RowH），避免 AccGroup 只增不减的行高机制导致布局跳动
            _altGateHint = new Label
            {
                Location = new Point(AccGroup.InL, AccGroup.HeadH + y),
                AutoSize = true,
                Font = new Font(Config.FontName, 8f),
                Visible = false,
                Tag = "paint"
            };
            _hoverGroup.Controls.Add(_altGateHint);
            _hoverGroup.ExpandRow(y, RowH);
            y += RowH;
            _fnUpdateAltGateHint();   // 打开设置窗时若已选"按住 Alt"，提示立即就位

            // 窗口边角：直角（卡片风默认）/ 圆角·粗描边（Win11）；切换即时重刷存活窗口
            _cornerCombo = new DarkCombo { Font = new Font(Config.FontName, 8.5f), Width = 150, Height = 24 };
            _cornerCombo.SetItems(new object[] { I18n.T("corner_square"), I18n.T("corner_round") },
                Config.WindowCorners == 1 ? 1 : 0);
            _cornerCombo.SelectedIndex = Config.WindowCorners == 1 ? 1 : 0;
            _cornerCombo.SelectedIndexChanged += (s, e) =>
            {
                Config.WindowCorners = _cornerCombo.SelectedIndex;
                _fnApplySettings(false, false);
                Program.RefreshWindowCorners();
                Win11.RefreshNc(Handle);   // 设置窗口自己也跟着切换描边风格
            };
            y = _hoverGroup.AddRow(y, I18n.T("corner_row"), I18n.T("corner_desc"), _cornerCombo);


            _hoverGroup.FinishRows();


            // ==== 组：预览类型（逐类开关，的"按类型预览"设置）====
            _pvTypeGroup = _fnNewGroup(I18n.T("group_preview_types"), false);
            _pvTypeGroup.AddHeadLink("@成熟的剪辑猿", "https://b23.tv/hVdEGrA");
            _pvTypeGroup.AddHeadLink("GitHub", "https://github.com/akahoz94");
            y = 8;
            _pvSpeedToggle = new ToggleSwitch { On = Config.SpeedToggle };
            _pvSpeedToggle.Toggled += (s, e) => { _fnApplySettings(false, false); };
            y = _pvTypeGroup.AddRow(y, I18n.T("pv_type_speed"), null, _pvSpeedToggle);
            // 视频预览默认静音（办公室友好）：悬停触发的视频先无声，点控制条喇叭键恢复
            _pvMuteToggle = new ToggleSwitch { On = Config.PreviewMuted };
            _pvMuteToggle.Toggled += (s, e) => _fnApplySettings(false, false);
            y = _pvTypeGroup.AddRow(y, I18n.T("mute_video"), I18n.T("mute_video_desc"), _pvMuteToggle);
            y = _pvTypeGroup.AddRow(y, I18n.T("pv_types_desc"), null, null);

            _pvImageToggle = new ToggleSwitch { On = Config.PreviewImage };
            _pvImageToggle.Toggled += (s, e) => { _fnApplySettings(false, false); };
            y = _pvTypeGroup.AddRow(y, I18n.T("pv_type_image"), null, _pvImageToggle);

            _pvVideoToggle = new ToggleSwitch { On = Config.PreviewVideo };
            _pvVideoToggle.Toggled += (s, e) => { _fnApplySettings(false, false); };
            y = _pvTypeGroup.AddRow(y, I18n.T("pv_type_video"), null, _pvVideoToggle);

            _pvAudioToggle = new ToggleSwitch { On = Config.PreviewAudio };
            _pvAudioToggle.Toggled += (s, e) => { _fnApplySettings(false, false); };
            y = _pvTypeGroup.AddRow(y, I18n.T("pv_type_audio"), null, _pvAudioToggle);

            _pvDocToggle = new ToggleSwitch { On = Config.PreviewDocument };
            _pvDocToggle.Toggled += (s, e) => { _fnApplySettings(false, false); };
            y = _pvTypeGroup.AddRow(y, I18n.T("pv_type_document"), null, _pvDocToggle);

            _pvTextToggle = new ToggleSwitch { On = Config.PreviewText };
            _pvTextToggle.Toggled += (s, e) => { _fnApplySettings(false, false); };
            y = _pvTypeGroup.AddRow(y, I18n.T("pv_type_text"), null, _pvTextToggle);

            _pvArchiveToggle = new ToggleSwitch { On = Config.PreviewArchive };
            _pvArchiveToggle.Toggled += (s, e) => { _fnApplySettings(false, false); };
            y = _pvTypeGroup.AddRow(y, I18n.T("pv_type_archive"), null, _pvArchiveToggle);

            _pvFontToggle = new ToggleSwitch { On = Config.PreviewFont };
            _pvFontToggle.Toggled += (s, e) => { _fnApplySettings(false, false); };
            y = _pvTypeGroup.AddRow(y, I18n.T("pv_type_font"), null, _pvFontToggle);

            _pvOtherToggle = new ToggleSwitch { On = Config.PreviewOther };
            _pvOtherToggle.Toggled += (s, e) => { _fnApplySettings(false, false); };
            y = _pvTypeGroup.AddRow(y, I18n.T("pv_type_other"), null, _pvOtherToggle);

            // ffmpeg 路径（留空=自动探测）：用于解码系统解码器不支持的高规格视频
            _ffmpegPathBox = new TextBox
            {
                Text = Config.FfmpegPath,
                Font = new Font("Consolas", 8.5f),
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Config.BG,
                ForeColor = Config.FG,
            };
            _ffmpegPathBox.Size = new Size(150, 22);
            var ffAutoBtn = new Button
            {
                Text = I18n.T("ffmpeg_auto"),
                FlatStyle = FlatStyle.Flat,
                BackColor = Config.BG,
                ForeColor = Config.FG,
                Font = new Font(Config.FontName, 8.5f),
                Size = new Size(48, 22),
                Cursor = Cursors.Hand,
            };
            ffAutoBtn.FlatAppearance.BorderColor = Config.Border;
            ffAutoBtn.Click += (s, e) => { _ffmpegPathBox.Text = ""; _fnApplySettings(false, false); };
            var ffRow = new Panel { Size = new Size(206, 24), BackColor = Config.BG };
            _ffmpegPathBox.Location = new Point(0, 1);
            ffAutoBtn.Location = new Point(156, 0);
            ffRow.Controls.Add(_ffmpegPathBox);
            ffRow.Controls.Add(ffAutoBtn);
            y = _pvTypeGroup.AddRow(y, I18n.T("ffmpeg_path_row"), I18n.T("ffmpeg_path_desc"), ffRow);

            // 路径失焦即生效
            _ffmpegPathBox.LostFocus += (s, e) => { _fnApplySettings(false, false); };
            _ffmpegPathBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { _ffmpegPathBox.Text = _ffmpegPathBox.Text.Trim(); _fnApplySettings(false, false); } };

            // ffmpeg 探测状态：让用户知道兜底解码到底能不能用
            int ffStatusY = y;
            _ffmpegStatusLbl = new Label
            {
                Location = new Point(AccGroup.InL, AccGroup.HeadH + ffStatusY + 2),
                AutoSize = true,
                ForeColor = Config.PlaceholderFg,
                Font = new Font(Config.FontName, 8f),
                Tag = "sub"
            };
            _pvTypeGroup.Controls.Add(_ffmpegStatusLbl);
            _pvTypeGroup.ExpandRow(ffStatusY, 20);
            y = ffStatusY + 20;
            _fnUpdateFfmpegStatus();

            _pvTypeGroup.FinishRows();


            // ==== 组：列表配色 ====
            var palGroup = _fnNewGroup(I18n.T("group_palette"), false);
            y = 8;
            y = palGroup.AddRow(y, I18n.T("list_palette"), I18n.T("list_palette_desc"), null);
            int palW = palGroup.Width - 24;
            Func<string, Button> mkBtn = delegate(string text)
            {
                var b = new Button { Text = text, Font = new Font(Config.FontName, 9f), FlatStyle = FlatStyle.Flat, BackColor = Config.BG, ForeColor = Config.FG, Height = 26, Visible = false };
                b.FlatAppearance.BorderColor = Config.Border;
                return b;
            };
            const string preset = "bg=f7f3ee\nzb=f0e9de\nfg=3b2f25\nmu=8a7664\nhv=ecdcc3\nsel=8a5a3c\nselfg=ffffff\nbar=e6d2ab\ndrop=2d7a3d\nac=8a5a3c\nacfg=ffffff\nbd=d6c7b2\nsb=c4b399\nhdbg=f0e8dc\nbtnhv=e0d0b8\ncanvas=f7f3ee";
            var promptText = I18n.T("palette_prompt_text");
            string[] palKeys = { "bg","zb","fg","mu","hv","sel","selfg","bar","drop","ac","acfg","bd","sb","hdbg","btnhv","canvas" };
            string[] palLabels = Array.ConvertAll(palKeys, k => I18n.T("pal_lbl_" + k));

            // 行 1：方案下拉 + 删除 + 另存为（选中/改即应用，无应用按钮）；行 2：暖灰斑马 + 清空（拆行避免右侧溢出截断）
            int row1Y = AccGroup.HeadH + y;
            var schemeLbl = new Label { Text = I18n.T("palette_scheme") + ":", Font = new Font(Config.FontName, 9f), ForeColor = Config.FG, Location = new Point(12, row1Y + 4), AutoSize = true, Visible = false };
            palGroup.Controls.Add(schemeLbl);
            var schemeCombo = new DarkCombo { Font = new Font(Config.FontName, 9f), Location = new Point(12 + schemeLbl.PreferredWidth + 8, row1Y), Width = 130, Visible = false };
            palGroup.Controls.Add(schemeCombo);
            var btnDelScheme = mkBtn(I18n.T("del_scheme"));
            var btnSaveAs = mkBtn(I18n.T("save_as"));
            var btnPreset = mkBtn(I18n.T("warm_zebra"));
            var btnClear = mkBtn(I18n.T("clear"));
            using (var bg = Graphics.FromHwnd(IntPtr.Zero)) { using (var f9 = new Font(Config.FontName, 9f)) { foreach (var b in new[]{btnDelScheme,btnSaveAs,btnPreset,btnClear}) b.Width = (int)bg.MeasureString(b.Text, f9).Width + 24; } }
            btnDelScheme.Location = new Point(schemeCombo.Right + 8, row1Y);
            btnSaveAs.Location = new Point(btnDelScheme.Right + 8, row1Y);
            palGroup.Controls.Add(btnDelScheme); palGroup.Controls.Add(btnSaveAs);
            int rowPresetY = row1Y + 32;
            btnPreset.Location = new Point(12, rowPresetY);
            btnClear.Location = new Point(btnPreset.Right + 8, rowPresetY);
            palGroup.Controls.Add(btnPreset); palGroup.Controls.Add(btnClear);

            // 行 3：16 行可视化编辑器（中文标签 + 色块 + hex），色块点击弹 ColorDialog，改即应用
            int row2Y = rowPresetY + 32;
            int editorH = palKeys.Length * 24;
            var editorPanel = new Panel { Location = new Point(12, row2Y), Size = new Size(palW, editorH), BackColor = Config.BG, Visible = false };
            palGroup.Controls.Add(editorPanel);
            var hexBoxes = new TextBox[palKeys.Length];
            var colorBlocks = new Panel[palKeys.Length];
            Action applyNow = null;
            // 测量标签最大宽度，色块/hex 留足间距，避免长语言文字覆盖色块
            int lblMaxW = 0;
            using (var lg = Graphics.FromHwnd(IntPtr.Zero)) using (var lf = new Font(Config.FontName, 9f))
                foreach (var t in palLabels) lblMaxW = Math.Max(lblMaxW, (int)lg.MeasureString(t, lf).Width + 4);
            int blockX = Math.Max(92, lblMaxW + 12);
            int hexX = blockX + 30;
            for (int i = 0; i < palKeys.Length; i++)
            {
                int ry = i * 24;
                var lbl = new Label { Text = palLabels[i], Font = new Font(Config.FontName, 9f), ForeColor = Config.FG, Location = new Point(0, ry + 3), AutoSize = true };
                var block = new Panel { Location = new Point(blockX, ry + 1), Size = new Size(24, 20), BorderStyle = BorderStyle.FixedSingle, Cursor = Cursors.Hand };
                var hex = new TextBox { Location = new Point(hexX, ry), Size = new Size(100, 22), Font = new Font("Consolas", 9f), BorderStyle = BorderStyle.FixedSingle };
                editorPanel.Controls.Add(lbl); editorPanel.Controls.Add(block); editorPanel.Controls.Add(hex);
                hexBoxes[i] = hex; colorBlocks[i] = block;
                int idx = i;
                block.Click += (s, e) => { using (var cd = new ColorDialog()) { if (cd.ShowDialog() == DialogResult.OK) { colorBlocks[idx].BackColor = cd.Color; hexBoxes[idx].Text = ColorTranslator.ToHtml(cd.Color).TrimStart('#'); if (applyNow != null) applyNow(); } } };
            }
            Action<string> fillEditor = delegate(string text)
            {
                var map = Config.ParsePalette(text);
                for (int i = 0; i < palKeys.Length; i++) { Color c; map.TryGetValue(palKeys[i], out c); colorBlocks[i].BackColor = c; hexBoxes[i].Text = ColorTranslator.ToHtml(c).TrimStart('#'); }
            };
            applyNow = delegate()
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < palKeys.Length; i++) sb.Append(palKeys[i]).Append('=').Append(hexBoxes[i].Text).Append('\n');
                if (sb.Length > 0) sb.Length--;
                Config.ListPalette = sb.ToString(); Config.ApplyListPalette(); Config.Save(); Program.RefreshAllPanels();
            };
            for (int i = 0; i < palKeys.Length; i++) { int idx = i; hexBoxes[i].Leave += (s, e) => { try { colorBlocks[idx].BackColor = ColorTranslator.FromHtml("#" + hexBoxes[idx].Text); } catch { } applyNow(); }; }
            fillEditor(Config.ListPalette);

            // 行 4：AI 提示词（标签 + 复制按钮 + 粘贴导入按钮）
            int row3Y = row2Y + editorH + 8;
            var promptLbl = new Label { Text = I18n.T("palette_prompt"), Font = new Font(Config.FontName, 9f, FontStyle.Bold), ForeColor = Config.FG, Location = new Point(12, row3Y + 4), AutoSize = true, Visible = false };
            palGroup.Controls.Add(promptLbl);
            var btnCopyPrompt = mkBtn(I18n.T("copy_prompt"));
            var btnPasteImport = mkBtn(I18n.T("paste_import"));
            using (var bg2 = Graphics.FromHwnd(IntPtr.Zero)) { using (var f9b = new Font(Config.FontName, 9f)) { foreach (var b in new[]{btnCopyPrompt,btnPasteImport}) b.Width = (int)bg2.MeasureString(b.Text, f9b).Width + 24; } }
            btnCopyPrompt.Location = new Point(promptLbl.Right + 12, row3Y);
            btnPasteImport.Location = new Point(btnCopyPrompt.Right + 8, row3Y);
            palGroup.Controls.Add(btnCopyPrompt); palGroup.Controls.Add(btnPasteImport);

            // 方案列表数据解析/序列化（格式：每行 name=escapedText，text 中 \n→\\n、=→\\e）
            Func<string, List<KeyValuePair<string,string>>> parsePalettes = delegate(string data)
            {
                var list = new List<KeyValuePair<string,string>>();
                if (string.IsNullOrEmpty(data)) return list;
                foreach (var line in data.Split('\n'))
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string nm = line.Substring(0, eq).Replace("\\e", "=");
                    string tx = line.Substring(eq + 1).Replace("\\n", "\n").Replace("\\e", "=");
                    list.Add(new KeyValuePair<string,string>(nm, tx));
                }
                return list;
            };
            Action<List<KeyValuePair<string,string>>> reloadCombo = delegate(List<KeyValuePair<string,string>> schemes)
            {
                schemeCombo.Items.Clear();
                foreach (var kv in schemes) schemeCombo.Items.Add(kv.Key);
            };
            var schemes0 = parsePalettes(Config.ListPalettesData);
            reloadCombo(schemes0);

            schemeCombo.SelectedIndexChanged += (s, e) => { if (schemeCombo.SelectedIndex >= 0) { fillEditor(schemes0[schemeCombo.SelectedIndex].Value); applyNow(); } };
            btnPreset.Click += (s, e) => { fillEditor(preset); applyNow(); };
            btnClear.Click += (s, e) => { Config.ListPalette = ""; Config.ApplyListPalette(); Config.Save(); fillEditor(""); Program.RefreshAllPanels(); };
            btnDelScheme.Click += (s, e) =>
            {
                if (schemeCombo.SelectedIndex < 0) return;
                schemes0.RemoveAt(schemeCombo.SelectedIndex);
                var sb = new System.Text.StringBuilder();
                foreach (var kv in schemes0) sb.Append(kv.Key.Replace("=","\\e")).Append('=').Append(kv.Value.Replace("\n","\\n").Replace("=","\\e")).Append('\n');
                Config.ListPalettesData = sb.ToString(); Config.Save(); reloadCombo(schemes0);
            };
            btnSaveAs.Click += (s, e) =>
            {
                applyNow();
                string nm = PromptName(I18n.T("scheme_name"), I18n.T("scheme_name"));
                if (string.IsNullOrEmpty(nm)) return;
                schemes0.Add(new KeyValuePair<string,string>(nm, Config.ListPalette));
                var sb = new System.Text.StringBuilder();
                foreach (var kv in schemes0) sb.Append(kv.Key.Replace("=","\\e")).Append('=').Append(kv.Value.Replace("\n","\\n").Replace("=","\\e")).Append('\n');
                Config.ListPalettesData = sb.ToString(); Config.Save(); reloadCombo(schemes0); schemeCombo.SelectedIndex = schemeCombo.Items.Count - 1;
            };
            btnCopyPrompt.Click += (s, e) => { try { Clipboard.SetText(promptText); } catch { } };
            // 粘贴导入：读剪贴板 key=hex 行，校验至少命中一个已知 key 后填入编辑器并即时应用（未写的 key 保留原值，由 ParsePalette 兜底）
            btnPasteImport.Click += (s, e) =>
            {
                string clip = null;
                try { clip = Clipboard.GetText(); } catch { }
                int hit = 0;
                if (!string.IsNullOrEmpty(clip))
                {
                    foreach (var line in clip.Split('\n'))
                    {
                        var t = line.Trim();
                        int eq = t.IndexOf('='); if (eq <= 0) continue;
                        string k = t.Substring(0, eq).Trim().ToLowerInvariant();
                        string v = t.Substring(eq + 1).Trim();
                        if (v.StartsWith("#")) v = v.Substring(1);
                        if (Array.IndexOf(palKeys, k) >= 0 && System.Text.RegularExpressions.Regex.IsMatch(v, "[0-9a-fA-F]{6,8}")) hit++;
                    }
                }
                if (hit == 0) { MessageBox.Show(this, I18n.T("paste_invalid"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                fillEditor(clip); applyNow();
                // 自动另存为方案（不弹命名框）：导入时间命名，重名追加序号
                string baseName = I18n.T("imported_prefix") + " " + DateTime.Now.ToString("MM-dd HH:mm");
                string nm = baseName; int dup = 2;
                while (schemes0.Exists(kv => kv.Key == nm)) nm = baseName + " (" + (dup++) + ")";
                schemes0.Add(new KeyValuePair<string, string>(nm, Config.ListPalette));
                var sbSave = new System.Text.StringBuilder();
                foreach (var kv in schemes0) sbSave.Append(kv.Key.Replace("=", "\\e")).Append('=').Append(kv.Value.Replace("\n", "\\n").Replace("=", "\\e")).Append('\n');
                Config.ListPalettesData = sbSave.ToString(); Config.Save(); reloadCombo(schemes0);
                schemeCombo.SelectedIndex = schemeCombo.Items.Count - 1;
            };
            palGroup.ExpandRow(row3Y - AccGroup.HeadH + 34, 0);
            palGroup.FinishRows();


            // ==== 组 2：桌面卡片 ====
            _cardGroup = _fnNewGroup(I18n.T("group_card"), false);
            _cardToggle = new ToggleSwitch { On = Config.CardHotkeyEnabled };
            _cardToggle.Toggled += (s, e) => { _fnUpdateCardState(); _fnApplySettings(true, false); };
            _cardGroup.SetToggle(_cardToggle);
            _cardGroup.AddChip(HotkeyDisplay(Config.CardHotkeyEnabled, _cardMods, _cardKey));

            y = 8;
            _cardPill = new HotkeyPill();
            _cardPill.SetHotkey(_cardMods, _cardKey);
            _cardPill.RecordClick += (s, e) => _fnStartCardCapture();
            y = _cardGroup.AddRow(y, I18n.T("hotkey_row"), I18n.T("card_hotkey_desc"), _cardPill);

            // ---- 卡片配色编辑器（方案下拉 + 色块编辑 + 字体 + 导入，参考列表配色）----
            string[] cpKeys = { "bg", "fg", "badge_bg", "badge_fg", "strip" };
            string[] cpLabels = Array.ConvertAll(cpKeys, k => I18n.T("cp_lbl_" + k));
            int cpRow1Y = AccGroup.HeadH + y;
            var cpSchemeLbl = new Label { Text = I18n.T("palette_scheme") + ":", Font = new Font(Config.FontName, 9f), ForeColor = Config.FG, Location = new Point(12, cpRow1Y + 4), AutoSize = true, Visible = false };
            _cardGroup.Controls.Add(cpSchemeLbl);
            var cpSchemeCombo = new DarkCombo { Font = new Font(Config.FontName, 9f), Location = new Point(12 + cpSchemeLbl.PreferredWidth + 8, cpRow1Y), Width = 130, Visible = false };
            _cardGroup.Controls.Add(cpSchemeCombo);
            Func<string, Button> cpBtn = delegate(string text) { var b = new Button { Text = text, Font = new Font(Config.FontName, 9f), FlatStyle = FlatStyle.Flat, BackColor = Config.BG, ForeColor = Config.FG, Height = 26, Visible = false }; b.FlatAppearance.BorderColor = Config.Border; return b; };
            Action cpApply = null;
            var cpBtnDel = cpBtn(I18n.T("del_scheme")); var cpBtnSaveAs = cpBtn(I18n.T("save_as"));
            var cpBtnImport = cpBtn(I18n.T("paste_import"));
            var cpBtnUpdate = cpBtn(I18n.T("update_scheme"));
            cpBtnDel.Location = new Point(cpSchemeCombo.Right + 8, cpRow1Y); cpBtnSaveAs.Location = new Point(cpBtnDel.Right + 8, cpRow1Y);
            _cardGroup.Controls.Add(cpBtnDel); _cardGroup.Controls.Add(cpBtnSaveAs);
            int cpRow2Y = cpRow1Y + 32;
            var cpEditor = new Panel { Location = new Point(12, cpRow2Y), Size = new Size(_cardGroup.Width - 24, cpKeys.Length * 24 + 4 * 24 + 14), BackColor = Config.BG, Visible = false };
            _cardGroup.Controls.Add(cpEditor);
            var cpHex = new TextBox[cpKeys.Length]; var cpBlk = new Panel[cpKeys.Length];
            for (int i = 0; i < cpKeys.Length; i++)
            {
                int ry = i * 24;
                cpEditor.Controls.Add(new Label { Text = cpLabels[i], Font = new Font(Config.FontName, 9f), ForeColor = Config.FG, Location = new Point(0, ry + 3), AutoSize = true });
                var blk = new Panel { Location = new Point(92, ry + 1), Size = new Size(24, 20), BorderStyle = BorderStyle.FixedSingle, Cursor = Cursors.Hand };
                var hx = new TextBox { Location = new Point(122, ry), Size = new Size(184, 22), Font = new Font("Consolas", 9f), BorderStyle = BorderStyle.FixedSingle };
                cpEditor.Controls.Add(blk); cpEditor.Controls.Add(hx); cpHex[i] = hx; cpBlk[i] = blk;
                int idx = i;
                blk.Paint += (sender, pe) => { var p = (Panel)sender; var g = pe.Graphics; using (var bw = new SolidBrush(Color.White)) using (var bg = new SolidBrush(Color.FromArgb(0xCC, 0xCC, 0xCC))) for (int bx = 0; bx < p.Width; bx += 5) for (int by = 0; by < p.Height; by += 5) g.FillRectangle(((bx / 5 + by / 5) & 1) == 0 ? bw : bg, bx, by, 5, 5); using (var b = new SolidBrush(p.BackColor)) g.FillRectangle(b, p.ClientRectangle); };
                blk.Click += (s, e) => { using (var cd = new ColorDialog()) { cd.Color = Color.FromArgb(255, cpBlk[idx].BackColor); if (cd.ShowDialog() == DialogResult.OK) { var old = cpBlk[idx].BackColor; var nc = Color.FromArgb(old.A, cd.Color.R, cd.Color.G, cd.Color.B); cpBlk[idx].BackColor = nc; cpHex[idx].Text = CardPalette.ToRgba(nc); cpBtnUpdate.Visible = true; } } };
                blk.MouseDown += (s, e) => { if (e.Button == MouseButtons.Right) { using (var af = new Form { Text = "Alpha 0-255", Width = 200, Height = 110, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false }) { var nud = new NumericUpDown { Location = new Point(20, 15), Width = 80, Minimum = 0, Maximum = 255, Value = cpBlk[idx].BackColor.A }; var ok = new Button { Text = "OK", Location = new Point(110, 13), Width = 60, Height = 26, DialogResult = DialogResult.OK }; af.Controls.Add(nud); af.Controls.Add(ok); if (af.ShowDialog(this) == DialogResult.OK) { var old = cpBlk[idx].BackColor; var nc = Color.FromArgb((int)nud.Value, old.R, old.G, old.B); cpBlk[idx].BackColor = nc; cpHex[idx].Text = CardPalette.ToRgba(nc); cpBtnUpdate.Visible = true; } } } };
            }
            // 色条宽 + 字体 + 字号 + 圆角（纵向排列，与上方颜色行对齐）
            int cpExtraY = cpKeys.Length * 24 + 2;
            const int cpAttrX = 92;
            cpEditor.Controls.Add(new Label { Text = I18n.T("cp_strip_w"), Font = new Font(Config.FontName, 9f), ForeColor = Config.FG, Location = new Point(0, cpExtraY + 3), AutoSize = true });
            var cpStripW = new NumericUpDown { Location = new Point(cpAttrX, cpExtraY), Size = new Size(46, 22), Minimum = 0, Maximum = 10, Font = new Font(Config.FontName, 9f) };
            cpEditor.Controls.Add(cpStripW);
            int cpFontY = cpExtraY + 24;
            cpEditor.Controls.Add(new Label { Text = I18n.T("cp_font"), Font = new Font(Config.FontName, 9f), ForeColor = Config.FG, Location = new Point(0, cpFontY + 3), AutoSize = true });
            var cpFontCombo = new DarkCombo { Location = new Point(cpAttrX, cpFontY), Size = new Size(200, 24), Font = new Font(Config.FontName, 9f) };
            using (var ifc = new InstalledFontCollection())
                foreach (var ff in ifc.Families)
                    cpFontCombo.Items.Add(ff.Name);
            cpEditor.Controls.Add(cpFontCombo);
            int cpSizeY = cpFontY + 24;
            cpEditor.Controls.Add(new Label { Text = I18n.T("cp_font_size"), Font = new Font(Config.FontName, 9f), ForeColor = Config.FG, Location = new Point(0, cpSizeY + 3), AutoSize = true });
            var cpFontSize = new NumericUpDown { Location = new Point(cpAttrX, cpSizeY), Size = new Size(46, 22), Minimum = 6, Maximum = 24, Font = new Font(Config.FontName, 9f) };
            cpEditor.Controls.Add(cpFontSize);
            int cpRadY = cpSizeY + 24;
            cpEditor.Controls.Add(new Label { Text = I18n.T("cp_radius"), Font = new Font(Config.FontName, 9f), ForeColor = Config.FG, Location = new Point(0, cpRadY + 3), AutoSize = true });
            var cpRadius = new NumericUpDown { Location = new Point(cpAttrX, cpRadY), Size = new Size(46, 22), Minimum = 0, Maximum = 12, Font = new Font(Config.FontName, 9f) };
            cpEditor.Controls.Add(cpRadius);
            // AI 提示词 + 复制 + 粘贴导入
            int cpPromptY = cpRow2Y + cpEditor.Height + 4;
            var cpPromptLbl = new Label { Text = I18n.T("palette_prompt"), Font = new Font(Config.FontName, 9f, FontStyle.Bold), ForeColor = Config.FG, Location = new Point(12, cpPromptY + 4), AutoSize = true, Visible = false };
            _cardGroup.Controls.Add(cpPromptLbl);
            var cpBtnCopyPrompt = cpBtn(I18n.T("copy_prompt"));
            cpBtnCopyPrompt.Location = new Point(cpPromptLbl.Right + 8, cpPromptY);
            _cardGroup.Controls.Add(cpBtnCopyPrompt);
            cpBtnImport.Location = new Point(cpBtnCopyPrompt.Right + 8, cpPromptY);
            _cardGroup.Controls.Add(cpBtnImport);
            cpBtnUpdate.Location = new Point(cpBtnImport.Right + 8, cpPromptY);
            cpBtnUpdate.Visible = false;
            _cardGroup.Controls.Add(cpBtnUpdate);
            var cpPromptText = I18n.T("card_palette_prompt_text");
            cpBtnCopyPrompt.Click += (s, e) => { try { Clipboard.SetText(cpPromptText); } catch { } };
            // 填充编辑器
            Action<CardPalette> cpFill = delegate(CardPalette p) {
                if (p == null) return;
                var vals = new Dictionary<string, Color> { {"bg",p.Bg},{"fg",p.Fg},{"badge_bg",p.BadgeBg},{"badge_fg",p.BadgeFg},{"strip",p.StripColor} };
                for (int i = 0; i < cpKeys.Length; i++) { Color c; vals.TryGetValue(cpKeys[i], out c); cpBlk[i].BackColor = c; cpHex[i].Text = CardPalette.ToRgba(c); }
                cpStripW.Value = Math.Max(0, Math.Min(10, p.StripWidth));
                if (cpFontCombo.Items.Contains(p.FontName)) cpFontCombo.SelectedItem = p.FontName; else { cpFontCombo.Items.Add(p.FontName); cpFontCombo.SelectedItem = p.FontName; }
                cpFontSize.Value = (decimal)Math.Max(6, Math.Min(24, p.FontSize));
                cpRadius.Value = Math.Max(0, Math.Min(12, p.Radius));
                cpBtnUpdate.Visible = false;
            };
            // 应用编辑器值到当前方案 + 保存 + 刷新卡片

            cpApply = delegate() {
                int si = Config.CardPaletteIndex;
                if (si < 0 || si >= CardPalettes.All.Count) return;
                var p = CardPalettes.All[si];
                p.Bg = cpBlk[0].BackColor; p.Fg = cpBlk[1].BackColor;
                p.BadgeBg = cpBlk[2].BackColor; p.BadgeFg = cpBlk[3].BackColor; p.StripColor = cpBlk[4].BackColor;
                p.StripWidth = (int)cpStripW.Value; p.FontName = (string)cpFontCombo.SelectedItem ?? "微软雅黑"; p.FontSize = (float)cpFontSize.Value; p.Radius = (int)cpRadius.Value;
                bool dark = CardPalette.Lum(p.Bg) < 128; p.Hover = CardPalette.Mix(p.Bg, dark ? Color.White : Color.Black, dark ? 0.10 : 0.07); p.Sub = CardPalette.Mix(p.Fg, p.Bg, 0.45);
                CardPalettes.Save(); Config.Save();
            };
            for (int i = 0; i < cpKeys.Length; i++) { int idx = i; cpHex[i].Leave += (s, e) => { var txt = cpHex[idx].Text.Trim(); Color nc; if (txt.StartsWith("rgba", StringComparison.OrdinalIgnoreCase) || txt.StartsWith("rgb", StringComparison.OrdinalIgnoreCase)) nc = CardPalette.Hx(txt); else { var h = txt.TrimStart('#'); if (System.Text.RegularExpressions.Regex.IsMatch(h, "^[0-9a-fA-F]{6}$|^[0-9a-fA-F]{8}$")) nc = CardPalette.Hx(h); else return; } cpBlk[idx].BackColor = nc; cpBtnUpdate.Visible = true; }; }
            cpStripW.ValueChanged += (s, e) => cpBtnUpdate.Visible = true;
            cpFontCombo.SelectedIndexChanged += (s, e) => cpBtnUpdate.Visible = true;
            cpFontSize.ValueChanged += (s, e) => cpBtnUpdate.Visible = true;
            cpRadius.ValueChanged += (s, e) => cpBtnUpdate.Visible = true;
            // 方案下拉
            Action cpRefreshSchemes = delegate() {
                int cur = Config.CardPaletteIndex;
                cpSchemeCombo.Items.Clear();
                foreach (var p in CardPalettes.All) cpSchemeCombo.Items.Add(p.Name);
                if (cur >= 0 && cur < cpSchemeCombo.Items.Count) cpSchemeCombo.SelectedIndex = cur;
            };
            cpSchemeCombo.SelectedIndexChanged += (s, e) => { if (cpSchemeCombo.SelectedIndex >= 0) { Config.CardPaletteIndex = cpSchemeCombo.SelectedIndex; cpFill(CardPalettes.Current); Config.Save(); } };
            cpBtnSaveAs.Click += (s, e) => {
                var nm = PromptName(I18n.T("scheme_name"), I18n.T("save_as"));
                if (string.IsNullOrEmpty(nm)) return;
                var src = CardPalettes.Current; if (src == null) return;
                var np = new CardPalette { Name = nm, Bg = src.Bg, Fg = src.Fg, BadgeBg = src.BadgeBg, BadgeFg = src.BadgeFg, StripColor = src.StripColor, StripWidth = src.StripWidth, FontName = src.FontName, FontSize = src.FontSize };
                np.Hover = src.Hover; np.Sub = src.Sub;
                CardPalettes.All.Add(np); Config.CardPaletteIndex = CardPalettes.All.Count - 1;
                CardPalettes.Save(); Config.Save(); cpRefreshSchemes(); cpFill(np);
            };
            cpBtnDel.Click += (s, e) => {
                if (CardPalettes.All.Count <= 1) return;
                int si = Config.CardPaletteIndex;
                CardPalettes.All.RemoveAt(si); Config.CardPaletteIndex = Math.Max(0, si - 1);
                CardPalettes.Save(); Config.Save(); cpRefreshSchemes(); cpFill(CardPalettes.Current);
            };
            cpBtnImport.Click += (s, e) => {
                string clip = null; try { clip = Clipboard.GetText(); } catch { }
                if (string.IsNullOrEmpty(clip)) { MessageBox.Show(this, I18n.T("paste_invalid"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                // 自动保存为新方案（不覆盖当前方案，不弹命名框）：导入时间命名，重名追加序号
                var parsed = CardPalette.Parse(clip, null);
                string baseName = I18n.T("imported_prefix") + " " + DateTime.Now.ToString("MM-dd HH:mm");
                string nm = baseName; int dup = 2;
                while (CardPalettes.All.Exists(p => p.Name == nm)) nm = baseName + " (" + (dup++) + ")";
                parsed.Name = nm; parsed.Radius = Math.Max(0, Math.Min(12, parsed.Radius));
                CardPalettes.All.Add(parsed); Config.CardPaletteIndex = CardPalettes.All.Count - 1;
                CardPalettes.Save(); Config.Save();
                cpRefreshSchemes(); cpFill(parsed); Program.RefreshAllCards();
            };
            cpBtnUpdate.Click += (s, e) => { cpApply(); Program.RefreshAllCards(); cpBtnUpdate.Visible = false; };
            cpRefreshSchemes(); cpFill(CardPalettes.Current);
            _cardGroup.ExpandRow(cpBtnImport.Bottom - AccGroup.HeadH + 4, 0);
            _cardGroup.FinishRows();


            // ==== 组 3：历史·收藏文件夹入口 ====
            _entryGroup = _fnNewGroup(I18n.T("group_entry"), false);
            _entryToggle = new ToggleSwitch { On = Config.EntryHotkeyEnabled };
            _entryToggle.Toggled += (s, e) => { _fnUpdateEntryState(); _fnApplySettings(true, false); };
            _entryGroup.SetToggle(_entryToggle);
            _entryGroup.AddChip(HotkeyDisplay(Config.EntryHotkeyEnabled, _entryMods, _entryKey));
            _entryGroup.AddChip("");   // 区域尺寸 chip（_fnUpdateAreaLbl 填充）

            y = 8;
            _entryPill = new HotkeyPill();
            _entryPill.SetHotkey(_entryMods, _entryKey);
            _entryPill.RecordClick += (s, e) => _fnStartEntryCapture();
            y = _entryGroup.AddRow(y, I18n.T("entry_hotkey"), null, _entryPill);

            // 触发区域行：色块 + 清除 + 框选（手动定位，desc 动态）
            int areaRowY = y;
            y = _entryGroup.AddRow(y, I18n.T("entry_area"), null, null);
            _areaLbl = new Label
            {
                Text = I18n.T("area_none"),
                Location = new Point(AccGroup.InL, AccGroup.HeadH + areaRowY + 19),
                AutoSize = true,
                MaximumSize = new Size(Math.Max(160, _entryGroup.Width - AccGroup.InL - 200), 0),
                ForeColor = Config.PlaceholderFg,
                Font = new Font(Config.FontName, 8f),
                Tag = "sub",
                Visible = false
            };
            _entryGroup.Controls.Add(_areaLbl);

            _areaPickBtn = new Button
            {
                Text = I18n.T("area_pick"),
                FlatStyle = FlatStyle.Flat,
                BackColor = Config.Accent,
                ForeColor = Color.White,
                Font = new Font(Config.FontName, 8.5f),
                Size = new Size(84, 26),
                Cursor = Cursors.Hand,
                Tag = "accent"
            };
            _areaPickBtn.FlatAppearance.BorderSize = 0;
            _areaPickBtn.FlatAppearance.MouseOverBackColor = Config.Accent;
            _areaPickBtn.Click += (s, e) => _fnPickArea();

            _areaClearBtn = new Button
            {
                Text = I18n.T("area_clear"),
                FlatStyle = FlatStyle.Flat,
                BackColor = Config.BG,
                ForeColor = Config.FG,
                Font = new Font(Config.FontName, 8.5f),
                Size = new Size(60, 26),
                Cursor = Cursors.Hand,
                Tag = "plain"
            };
            _areaClearBtn.FlatAppearance.BorderSize = 1;
            _areaClearBtn.FlatAppearance.MouseOverBackColor = Config.BgHover;
            _areaClearBtn.Click += (s, e) => { _entryRect = Rectangle.Empty; _fnUpdateAreaLbl(); };

            // RGBA 色块：棋盘底 + 半透明色（同桌面色块观感），点击循环切换预设色
            _areaColorBlock = new Panel
            {
                Size = new Size(26, 26),
                BackColor = Config.BG,
                Cursor = Cursors.Hand,
                Tag = "paint"
            };
            _areaColorBlock.Paint += (s, e) =>
            {
                var g = e.Graphics;
                using (var bw = new SolidBrush(Color.White)) g.FillRectangle(bw, 0, 0, 26, 26);
                using (var bg = new SolidBrush(Color.FromArgb(222, 222, 222)))
                    for (int yy = 0; yy < 26; yy += 8)
                        for (int xx = ((yy / 8) % 2) * 8; xx < 26; xx += 16)
                            g.FillRectangle(bg, xx, yy, Math.Min(8, 26 - xx), Math.Min(8, 26 - yy));
                using (var bc = new SolidBrush(Color.FromArgb(_entryColor))) g.FillRectangle(bc, 0, 0, 26, 26);
                using (var p = new Pen(Config.Border)) g.DrawRectangle(p, 0, 0, 25, 25);
            };
            _areaColorBlock.Click += (s, e) =>
            {
                int idx = Array.IndexOf(_areaColors, _entryColor);
                _entryColor = _areaColors[(idx + 1) % _areaColors.Length];
                _areaColorBlock.Invalidate();
                _fnUpdateAreaLbl();   // 换色也实时刷新桌面色块
            };
            Tips.Set(_areaColorBlock, I18n.T("area_color"));

            int areaBtnY = AccGroup.HeadH + areaRowY;
            _areaPickBtn.Location = new Point(_entryGroup.Width - AccGroup.InR - _areaPickBtn.Width, areaBtnY);
            _areaClearBtn.Location = new Point(_areaPickBtn.Left - _areaClearBtn.Width - 8, areaBtnY);
            _areaColorBlock.Location = new Point(_areaClearBtn.Left - _areaColorBlock.Width - 8, areaBtnY);
            _entryGroup.Controls.Add(_areaPickBtn);
            _entryGroup.Controls.Add(_areaClearBtn);
            _entryGroup.Controls.Add(_areaColorBlock);
            _entryGroup.ExpandRow(areaRowY, Math.Max(RowH, 19 + _areaLbl.PreferredHeight + 8));

            _entryGroup.FinishRows();


            // ==== 组 4：排序（默认排序，独立于 TC）====
            _sortGroup = _fnNewGroup(I18n.T("group_sort"), false);
            _sortGroup.AddChip("");

            _sortCombo = new DarkCombo { Font = new Font(Config.FontName, 8.5f), Width = 170, Height = 24 };
            _sortCombo.Items.AddRange(new object[] {
                I18n.T("sort_name") + " ↑", I18n.T("sort_name") + " ↓",
                I18n.T("sort_size") + " ↑", I18n.T("sort_size") + " ↓",
                I18n.T("sort_date") + " ↑", I18n.T("sort_date") + " ↓",
            });
            string[] sortModes = { "name", "size", "date" };
            int sortIdx = Array.IndexOf(sortModes, Config.SortMode);
            if (sortIdx < 0) sortIdx = 0;
            _sortCombo.SelectedIndex = sortIdx * 2 + (Config.SortDesc ? 1 : 0);
            _sortCombo.SelectedIndexChanged += (s, e) => { _fnRefreshChips(); _fnApplySettings(false, true); };

            y = 8;
            y = _sortGroup.AddRow(y, I18n.T("sort_default"), I18n.T("sort_default_desc"), _sortCombo);
            _sortGroup.FinishRows();


            // ==== 组 5：Total Commander 支持（暂停开发）====
            _tcGroup = _fnNewGroup(I18n.T("group_tc"), false);
            _tcGroup.AddChip(I18n.T("tag_paused"), true);

            _tcSortCombo = new DarkCombo { Font = new Font(Config.FontName, 8.5f), Enabled = false, Width = 170, Height = 24 };
            _tcSortCombo.Items.AddRange(new object[] { "自动 (读 TC ini)", "名称 ↑", "名称 ↓", "日期 ↑", "日期 ↓", "大小 ↑", "大小 ↓", "扩展名 ↑", "扩展名 ↓" });
            _tcSortCombo.SelectedIndex = Config.TcSortMode + 1;

            y = 8;
            y = _tcGroup.AddRow(y, I18n.T("tc_sort_row"), I18n.T("tc_sort_disabled"), _tcSortCombo);
            _tcGroup.FinishRows();

            // ---- "关于"折叠组（赞助/版本，随内容滚动）----
            EventHandler copyWx = (s, e) =>
            {
                try { Clipboard.SetText("jtugqivi"); MessageBox.Show(I18n.T("donated"), I18n.T("donate"), MessageBoxButtons.OK, MessageBoxIcon.Information); } catch { }
            };
            var aboutGroup = _fnNewGroup(I18n.T("group_about"), true);
            aboutGroup.FreeformLayout = true;   // 自由排版说明文本，几何审计按行式规则扫会误报
            const int qrSize = 150;
            Image qrImg = null;
            try { var s = typeof(SettingsForm).Assembly.GetManifestResourceStream("HoverExplorer.donate_qr.png"); if (s != null) qrImg = Image.FromStream(s); } catch { }
            int aboutY = 8;
            int aboutBase = AccGroup.HeadH + aboutY;
            if (qrImg != null)
            {
                var qr = new PictureBox { Location = new Point(PadX, aboutBase), Size = new Size(qrSize, qrSize), SizeMode = PictureBoxSizeMode.Zoom, Image = qrImg, Cursor = Cursors.Hand, Visible = false };
                qr.Click += copyWx;
                aboutGroup.Controls.Add(qr);
            }
            int linkX = PadX + qrSize + 16;
            var donateLbl = new LinkLabel { Text = "vx:jtugqivi", Location = new Point(linkX, aboutBase + 4), AutoSize = true, Font = new Font(Config.FontName, 8.5f), Tag = "sub", Visible = false };
            donateLbl.Click += copyWx;
            aboutGroup.Controls.Add(donateLbl);
            var link1 = MakeLink("GitHub Releases", linkX, aboutBase + 28, "https://github.com/dlsdgj/HoverExplorer/releases"); link1.Visible = false; aboutGroup.Controls.Add(link1);
            var link2 = MakeLink("百度网盘 (pwd: hbf8)", linkX, aboutBase + 52, "https://pan.baidu.com/s/12rp6LTe7Y8oSErvP8wTLgA?pwd=hbf8"); link2.Visible = false; aboutGroup.Controls.Add(link2);
            var linkCardGen = MakeLink(I18n.T("card_gen_link"), linkX, aboutBase + 76, "https://dlsdgj.github.io/hoverexplorer-card-palette/"); linkCardGen.Visible = false; aboutGroup.Controls.Add(linkCardGen);
            var linkListGen = MakeLink(I18n.T("list_gen_link"), linkX, aboutBase + 100, "https://dlsdgj.github.io/hoverExplorers-cheme-generator/"); linkListGen.Visible = false; aboutGroup.Controls.Add(linkListGen);
            _verLbl = new Label { Text = "v" + Program.Version, AutoSize = true, ForeColor = Config.PlaceholderFg, Font = new Font(Config.FontName, 8f), Tag = "sub", Visible = false };
            _verLbl.Location = new Point(aboutGroup.Width - PadX - 18 - _verLbl.PreferredWidth, aboutBase + qrSize - _verLbl.PreferredHeight);
            aboutGroup.Controls.Add(_verLbl);
            // 更新检测：异步查 GitHub latest release，新版则在版本号左侧显示红点标识，点击打开 Releases
            _updateLbl = new LinkLabel { Text = "", AutoSize = true, Font = new Font(Config.FontName, 8f, FontStyle.Bold), Tag = "sub", Visible = false, LinkColor = Color.FromArgb(0xE0, 0x31, 0x31), ActiveLinkColor = Color.FromArgb(0xE0, 0x31, 0x31) };
            _updateLbl.LinkClicked += (s, e) => { try { System.Diagnostics.Process.Start("https://github.com/dlsdgj/HoverExplorer/releases"); } catch { } };
            aboutGroup.Controls.Add(_updateLbl);
            _fnCheckUpdate(latest =>
            {
                if (IsDisposed || _updateLbl == null || _verLbl == null) return;
                if (_fnNewer(latest))
                {
                    _updateLbl.Text = "● v" + latest + " ↗";
                    _updateLbl.Location = new Point(_verLbl.Left - 10 - _updateLbl.PreferredWidth, _verLbl.Top);
                }
            });

            // MP3 预览慢的科普说明 + 修复工具入口（拖拽转换容器型假 MP3）
            int mp3Y = aboutBase + qrSize + 10;
            var mp3Title = new Label { Text = I18n.T("mp3_slow_title"), Location = new Point(PadX, mp3Y), AutoSize = true, Font = new Font(Config.FontName, 8.5f, FontStyle.Bold), Tag = "sub", Visible = false };
            aboutGroup.Controls.Add(mp3Title);
            var mp3Body = new Label
            {
                Text = I18n.T("mp3_slow_body"),
                Location = new Point(PadX, mp3Y + 20),
                AutoSize = false,
                Size = new Size(aboutGroup.Width - PadX * 2, 60),
                ForeColor = Config.PlaceholderFg,
                Font = new Font(Config.FontName, 8f),
                Tag = "sub",
                Visible = false
            };
            mp3Body.Height = Math.Max(48, (int)Math.Ceiling((double)TextRenderer.MeasureText(mp3Body.Text, mp3Body.Font, mp3Body.Size, TextFormatFlags.WordBreak).Height / 12.0) * 12 + 4);
            aboutGroup.Controls.Add(mp3Body);
            var fixerLink = MakeLink(I18n.T("mp3_fixer_open"), PadX, mp3Body.Bottom + 6, null);
            fixerLink.Click += (s, e) =>
            {
                var exe = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "Mp3Fixer.exe");
                if (System.IO.File.Exists(exe)) { try { System.Diagnostics.Process.Start(exe); return; } catch { } }
                // 程序目录里没有 Mp3Fixer.exe：引导到 GitHub Releases / 百度网盘（关于区下方即链接）
                var r = System.Windows.Forms.MessageBox.Show(this,
                    "未找到 Mp3Fixer.exe。\n\n可到 GitHub Releases 页面或百度网盘（链接见下方）下载后，放到本程序同目录。\n\n现在打开 GitHub Releases 页面？",
                    I18n.T("mp3_fixer_open"), MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (r == DialogResult.Yes)
                {
                    try { System.Diagnostics.Process.Start("https://github.com/dlsdgj/HoverExplorer/releases"); } catch { }
                }
            };
            aboutGroup.Controls.Add(fixerLink);
            // 为什么要下载 ffmpeg（与 MP3 段同款"标题+正文+链接"结构）+ 一键下载（Mp3Fixer 同款方案）。
            // 落点=程序目录（不可写退 %LOCALAPPDATA%\HoverExplorer），下完探测立即生效。
            // 状态提示与链接同行（右侧），不占行高、不推动下方绝对定位的内容
            int ffdlY = fixerLink.Bottom + 14;
            var ffDlTitle = new Label { Text = I18n.T("ffmpeg_dl_title"), Location = new Point(PadX, ffdlY), AutoSize = true, Font = new Font(Config.FontName, 8.5f, FontStyle.Bold), Tag = "sub", Visible = false };
            aboutGroup.Controls.Add(ffDlTitle);
            var ffDlBody = new Label
            {
                Text = I18n.T("ffmpeg_dl_body"),
                Location = new Point(PadX, ffdlY + 20),
                AutoSize = false,
                Size = new Size(aboutGroup.Width - PadX * 2, 60),
                ForeColor = Config.PlaceholderFg,
                Font = new Font(Config.FontName, 8f),
                Tag = "sub",
                Visible = false
            };
            ffDlBody.Height = Math.Max(48, (int)Math.Ceiling((double)TextRenderer.MeasureText(ffDlBody.Text, ffDlBody.Font, ffDlBody.Size, TextFormatFlags.WordBreak).Height / 12.0) * 12 + 4);
            aboutGroup.Controls.Add(ffDlBody);
            var ffDlLink = MakeLink(I18n.T("ffmpeg_download"), PadX, ffDlBody.Bottom + 6, null);
            aboutGroup.Controls.Add(ffDlLink);
            var ffDlStatus = new Label
            {
                Location = new Point(ffDlLink.Right + 10, ffDlLink.Top + 2),
                AutoSize = true,
                ForeColor = Config.PlaceholderFg,
                Font = new Font(Config.FontName, 8f),
                Tag = "sub",
                Visible = false
            };
            aboutGroup.Controls.Add(ffDlStatus);
            ffDlLink.Click += (s, e) =>
            {
                if (FfmpegDownload.Busy) return;
                if (FfmpegTools.FindFfmpeg() != null)
                {
                    var r = System.Windows.Forms.MessageBox.Show(this, I18n.T("ffmpeg_download_confirm"),
                        I18n.T("ffmpeg_download"), MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (r != DialogResult.Yes) return;
                }
                ffDlLink.Enabled = false;
                ffDlStatus.Visible = true;
                ffDlStatus.Text = I18n.T("ffmpeg_downloading", "0%");
                Action<string> status = txt =>
                {
                    try { BeginInvoke((MethodInvoker)delegate { ffDlStatus.Text = I18n.T("ffmpeg_downloading", txt); }); } catch { }
                };
                FfmpegDownload.Download(status, (ok, msg) =>
                {
                    try { BeginInvoke((MethodInvoker)delegate
                    {
                        ffDlLink.Enabled = true;
                        ffDlStatus.Text = ok ? I18n.T("ffmpeg_download_done", msg) : I18n.T("ffmpeg_download_fail", msg);
                        if (ok) _fnUpdateFfmpegStatus();   // 不动用户显式路径：留空时 FindFfmpeg 自动发现新落点
                    }); } catch { }
                });
            };
            // Windows Defender 误报说明
            int wdY = ffDlLink.Bottom + 14;
            var wdTitle = new Label { Text = I18n.T("wd_notice_title"), Location = new Point(PadX, wdY), AutoSize = true, Font = new Font(Config.FontName, 8.5f, FontStyle.Bold), Tag = "sub", Visible = false };
            aboutGroup.Controls.Add(wdTitle);
            var wdBody = new Label
            {
                Text = I18n.T("wd_notice_body"),
                Location = new Point(PadX, wdY + 20),
                AutoSize = false,
                Size = new Size(aboutGroup.Width - PadX * 2, 80),
                ForeColor = Config.PlaceholderFg,
                Font = new Font(Config.FontName, 8f),
                Tag = "sub",
                Visible = false
            };
            wdBody.Height = Math.Max(60, (int)Math.Ceiling((double)TextRenderer.MeasureText(wdBody.Text, wdBody.Font, wdBody.Size, TextFormatFlags.WordBreak).Height / 12.0) * 12 + 4);
            aboutGroup.Controls.Add(wdBody);
            // 隐藏能力说明
            int hfY = wdBody.Bottom + 18;
            var hfTitle = new Label { Text = I18n.T("hidden_features_title"), Location = new Point(PadX, hfY), AutoSize = true, Font = new Font(Config.FontName, 8.5f, FontStyle.Bold), Tag = "sub", Visible = false };
            aboutGroup.Controls.Add(hfTitle);
            var hfBody = new Label
            {
                Text = I18n.T("hidden_features_body"),
                Location = new Point(PadX, hfY + 20),
                AutoSize = false,
                Size = new Size(aboutGroup.Width - PadX * 2, 80),
                ForeColor = Config.PlaceholderFg,
                Font = new Font(Config.FontName, 8f),
                Tag = "sub",
                Visible = false
            };
            hfBody.Height = Math.Max(60, (int)Math.Ceiling((double)TextRenderer.MeasureText(hfBody.Text, hfBody.Font, hfBody.Size, TextFormatFlags.WordBreak).Height / 12.0) * 12 + 4);
            aboutGroup.Controls.Add(hfBody);
            var obsidianLink = MakeLink(I18n.T("hidden_features_obsidian_link"), PadX, hfBody.Bottom + 8, "https://github.com/dlsdgj/hover-link-preview");
            obsidianLink.Visible = false;
            aboutGroup.Controls.Add(obsidianLink);
            var perchLink = MakeLink(I18n.T("hidden_features_perch_link"), PadX, obsidianLink.Bottom + 6, "https://github.com/dlsdgj/perch");
            perchLink.Visible = false;
            aboutGroup.Controls.Add(perchLink);
            aboutGroup.ExpandRow(perchLink.Bottom + 20 - AccGroup.HeadH, 0);
            aboutGroup.FinishRows();
            if (aboutGroup.Open) { aboutGroup.Open = false; aboutGroup.Open = true; }

            _fnUpdateAreaLbl();
            _fnRefreshChips();
            _fnUpdateExpandBtn();
            _fnSyncScroll();
            ApplyThemeColors();
        }


        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _fnSyncScroll();   // 布局稳定后再校一次内容高度（构造期 GetPreferredSize 可能偏小）
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            // 标题栏底色（与组标题同色，拉开与内容区层次）；子控件绘制在其上
            using (var b = new SolidBrush(Config.BgHover))
                e.Graphics.FillRectangle(b, 0, 0, Width, HeadAreaH);
            using (var p = new Pen(Config.Border, 1))
                if (Config.WindowCorners != 1) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            // 圆角模式的粗描边由非客户区绘制（见 WndNcBorder），客户区不画
        }

        protected override void WndProc(ref Message m)
        {
            if (Win11.WndNcBorder(Handle, ref m)) return;
            base.WndProc(ref m);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _fnDrag(e);
        }

        private void _fnDrag(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && e.Y <= HeadAreaH)
            {
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(Handle, 0xA1, 0x2, 0);
            }
        }

        // ================= 手风琴 =================

        // 简易输入框（配色方案命名用）
        private static string PromptName(string title, string defaultName)
        {
            using (var f = new Form { Text = title, Width = 340, Height = 150, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, BackColor = Config.BG })
            {
                var tb = new TextBox { Text = defaultName, Location = new Point(12, 12), Width = 300, Font = new Font(Config.FontName, 9f), BackColor = Config.BG, ForeColor = Config.FG };
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(236, 44), Width = 76, Height = 26, Font = new Font(Config.FontName, 9f), BackColor = Config.BG, ForeColor = Config.FG, FlatStyle = FlatStyle.Flat };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(154, 44), Width = 76, Height = 26, Font = new Font(Config.FontName, 9f), BackColor = Config.BG, ForeColor = Config.FG, FlatStyle = FlatStyle.Flat };
                f.Controls.Add(tb); f.Controls.Add(ok); f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                return f.ShowDialog() == DialogResult.OK ? tb.Text.Trim() : null;
            }
        }

        private AccGroup _fnNewGroup(string title, bool open)
        {
            int contentW = Width - PadX * 2 - ScrollBarW;   // 右侧预留自绘滚动条
            var g = new AccGroup(contentW, title, open);
            g.Margin = new Padding(0, 0, 0, 10);
            _scrollPanel.Controls.Add(g);
            _groups.Add(g);
            g.HeadClicked += (s, e) => _fnToggleGroup(g);
            return g;
        }

        // 手风琴单开：点已展开组 = 收起；展开一组 = 收起其余，并滚入视野
        private void _fnToggleGroup(AccGroup g)
        {
            if (g.Open) g.Open = false;
            else
            {
                foreach (var o in _groups) if (o != g) o.Open = false;
                g.Open = true;
                _fnScrollIntoView(g);
            }
            _fnSyncScroll();
            _fnUpdateExpandBtn();
        }

        // GitHub latest release 版本检测（静态缓存，多次开设置只查一次）
        private static void _fnCheckUpdate(Action<string> done)
        {
            if (_latestVer != null) { done(_latestVer); return; }
            try
            {
                var wc = new System.Net.WebClient();
                wc.Headers.Add("user-agent", "HoverExplorer/" + Program.Version);
                wc.DownloadStringCompleted += (s, e) =>
                {
                    string res = "";
                    try
                    {
                        if (e.Error == null && e.Result != null)
                        {
                            var m = System.Text.RegularExpressions.Regex.Match(e.Result.ToString(), @"""tag_name""\s*:\s*""v?([\d.]+)""");
                            if (m.Success) res = m.Groups[1].Value;
                        }
                    }
                    catch { }
                    _latestVer = res;
                    try { done(res); } catch { }
                };
                wc.DownloadStringAsync(new Uri("https://api.github.com/repos/dlsdgj/HoverExplorer/releases/latest"));
            }
            catch { _latestVer = ""; try { done(""); } catch { } }
        }

        private static bool _fnNewer(string latest)
        {
            if (string.IsNullOrEmpty(latest)) return false;
            try
            {
                var a = Program.Version.Split('.');
                var b = latest.Split('.');
                for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
                {
                    int ai = i < a.Length ? int.Parse(a[i]) : 0;
                    int bi = i < b.Length ? int.Parse(b[i]) : 0;
                    if (bi > ai) return true;
                    if (bi < ai) return false;
                }
            }
            catch { }
            return false;
        }

        // ================= 自绘滚动 =================

        // 组高度/视口变化后同步滚动范围（内容层高度补齐，避免 FlowLayoutPanel 裁掉溢出子控件）
        // 注意：GetPreferredSize(Size(w,0)) 高度约束 0 会返回 0（上版滑块不显示根因），改为直接累加子控件 bounds
        private void _fnSyncScroll()
        {
            if (_viewport == null || _scrollBar == null) return;
            _scrollPanel.PerformLayout();   // 组高度变化后先重排（FlowLayoutPanel 布局默认挂起，直接读 bounds 是旧值）
            int contentH = _scrollPanel.Padding.Top;
            foreach (Control c in _scrollPanel.Controls)
                contentH = Math.Max(contentH, c.Bottom + c.Margin.Bottom);
            int viewH = _viewport.Height;
            _scrollPanel.Size = new Size(_viewport.Width - ScrollBarW, Math.Max(contentH, viewH));
            _scrollBar.Maximum = Math.Max(1, contentH);
            _scrollBar.LargeChange = Math.Max(1, viewH);
            if (_scrollBar.Value > contentH - viewH) _scrollBar.Value = Math.Max(0, contentH - viewH);
        }

        // 展开的组滚入视野（自绘滚动替代 ScrollControlIntoView）
        private void _fnScrollIntoView(AccGroup g)
        {
            int yInView = g.Location.Y - _scrollBar.Value;
            if (yInView < 0) _scrollBar.Value = g.Location.Y;
            else if (yInView + g.Height > _viewport.Height)
                _scrollBar.Value = Math.Max(0, g.Location.Y + g.Height - _viewport.Height);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_scrollBar == null || !_scrollBar.Visible) return;
            var loc = _viewport.PointToClient(Cursor.Position);
            if (loc.X < 0 || loc.X >= _viewport.Width || loc.Y < 0 || loc.Y >= _viewport.Height) return;
            _scrollBar.Value -= e.Delta / 120 * 40;
        }

        private void _fnUpdateExpandBtn()
        {
            if (_expandBtn == null) return;
            bool anyClosed = false;
            foreach (var g in _groups) if (!g.Open) { anyClosed = true; break; }
            _expandBtn.Text = anyClosed ? I18n.T("settings_expand") : I18n.T("settings_collapse");
            _fnPlaceExpandBtn();
        }

        private void _fnPlaceExpandBtn()
        {
            _expandBtn.Location = new Point(_closeBtn.Left - 12 - _expandBtn.Width, 19);
        }

        // ================= 摘要 chip =================

        private void _fnRefreshChips()
        {
            if (_hoverGroup == null || _cardGroup == null || _entryGroup == null || _sortGroup == null) return;
            if (_hoverPill == null || _cardPill == null || _entryPill == null || _delayStepper == null || _closeDelayStepper == null || _sortCombo == null) return;
            _hoverGroup.SetChip(0, HotkeyDisplay(_hoverToggle.On, _hoverMods, _hoverKey));
            _hoverGroup.SetChip(1, string.Format("{0:0.00} {1}", _delayStepper.Value, I18n.T("delay_unit")));
            _hoverGroup.SetChip(2, _closeDelayStepper.Value + " ms");
            _cardGroup.SetChip(0, HotkeyDisplay(_cardToggle.On, _cardMods, _cardKey));
            _entryGroup.SetChip(0, HotkeyDisplay(_entryToggle.On, _entryMods, _entryKey));
            _sortGroup.SetChip(0, _sortCombo.SelectedIndex >= 0 ? _sortCombo.SelectedItem.ToString() : "");
        }

        // ================= 快捷键录制 =================

        private void _fnStartHoverCapture()
        {
            _fnStopCapture();
            _hoverRecording = true;
            _hoverPill.Recording = true;
        }

        private void _fnStartCardCapture()
        {
            _fnStopCapture();
            _cardRecording = true;
            _cardPill.Recording = true;
        }

        private void _fnStartEntryCapture()
        {
            _fnStopCapture();
            _entryRecording = true;
            _entryPill.Recording = true;
        }

        private void _fnStopCapture()
        {
            if (_hoverRecording)
            {
                _hoverRecording = false;
                _hoverPill.Recording = false;
                _fnPlacePill(_hoverPill, _hoverGroup);
            }
            if (_cardRecording)
            {
                _cardRecording = false;
                _cardPill.Recording = false;
                _fnPlacePill(_cardPill, _cardGroup);
            }
            if (_entryRecording)
            {
                _entryRecording = false;
                _entryPill.Recording = false;
                _fnPlacePill(_entryPill, _entryGroup);
            }
            _fnRefreshChips();
        }

        private void _fnPlacePill(HotkeyPill pill, AccGroup g)
        {
            if (pill == null || g == null) return;
            pill.Location = new Point(g.Width - AccGroup.InR - pill.Width, pill.Location.Y);
        }

        private void _fnUpdatePauseLbl()
        {
            if (_pauseRectLbl == null) return;
            _pauseRectLbl.Text = Config.PauseRectW >= 20
                ? string.Format(I18n.T("area_fmt"), Config.PauseRectW, Config.PauseRectH, Config.PauseRectX, Config.PauseRectY)
                : I18n.T("area_none");
        }

        private void _fnPickPauseZone()
        {
            // 暂停触控区框选（复制触发区域的框选流程）：确定后写入矩形并刷新标识块
            using (var pick = new AreaPickForm(delegate(Rectangle r)
            {
                // 最小 5×5：选定后桌面红框+坐标即时可见可校验
                if (r.Width < 5 || r.Height < 5) return;
                Config.PauseRectX = r.X; Config.PauseRectY = r.Y;
                Config.PauseRectW = r.Width; Config.PauseRectH = r.Height;
                _fnUpdatePauseLbl();
                Program.UpdatePauseIndicator();
                _fnApplySettings(false, false);
            }))
            {
                pick.ShowDialog(this);
            }
        }

        private void _fnPickArea()
        {
            // 全屏遮罩框选（Esc 取消）；确定后写 _entryRect 并即时预览显示
            using (var pick = new AreaPickForm(delegate(Rectangle r) { _entryRect = r; _fnUpdateAreaLbl(); }))
            {
                pick.ShowDialog(this);
            }
        }

        // ffmpeg 探测状态：显示实际用到的可执行文件；找不到时给出提示（兜底解码不可用）
        private void _fnUpdateFfmpegStatus()
        {
            if (_ffmpegStatusLbl == null) return;
            string exe = FfmpegTools.FindFfmpeg();
            _ffmpegStatusLbl.Text = string.IsNullOrEmpty(exe)
                ? I18n.T("ffmpeg_status_missing")
                : I18n.T("ffmpeg_found", exe);
        }

        private void _fnUpdateAreaLbl()
        {
            if (_areaLbl == null) return;
            _areaLbl.Text = _entryRect.Width > 0
                ? string.Format(I18n.T("area_fmt"), _entryRect.Width, _entryRect.Height, _entryRect.X, _entryRect.Y)
                : I18n.T("area_none");
            if (_entryGroup != null)
                _entryGroup.SetChip(1, _entryRect.Width > 0 ? string.Format("{0}×{1}", _entryRect.Width, _entryRect.Height) : "");
            // 即改即生效：桌面色块先按当前值立即刷新，随后整批写入 Config 并保存
            Program.UpdateEntryIndicator(_entryRect, _entryColor);
            _fnApplySettings(false, false);
        }

        // 即改即生效：把控件当前值整批写入 Config 并提交（无确定/取消按钮，改动实时生效）
        private void _fnApplySettings(bool hotkeys, bool panels)
        {
            if (_internalSync) return;   // 托盘→设置回写触发的下拉事件：Config 已是最新，不再整批应用

            Config.HotkeyEnabled = _hoverToggle.On;
            Config.HotkeyModifiers = _hoverMods;
            Config.HotkeyKey = _hoverKey;
            Config.HoverDelay = _delayStepper.Value;
            Config.CloseDelay = _closeDelayStepper.Value / 1000.0;
            Config.EntryRectDelayMs = _entryDelayStepper.Value;
            Config.CardHotkeyEnabled = _cardToggle.On;
            Config.CardHotkeyModifiers = _cardMods;
            Config.CardHotkeyKey = _cardKey;
            Config.KeepOnLeave = _keepToggle.On;
            Config.RowSwapSides = _rowSwapToggle.On;
            Config.AltSuppressHover = _altSuppressToggle.On;
            if (_modeExplorer != null) ApplyMode(true, _modeExplorer.SelectedIndex);
            if (_modePreview != null) ApplyMode(false, _modePreview.SelectedIndex);
            int sortSel = _sortCombo.SelectedIndex >= 0 ? _sortCombo.SelectedIndex : 0;
            Config.SortMode = sortSel / 2 == 1 ? "size" : (sortSel / 2 == 2 ? "date" : "name");
            Config.SortDesc = (sortSel % 2 == 1);
            Config.EntryHotkeyEnabled = _entryToggle.On;
            Config.EntryHotkeyModifiers = _entryMods;
            Config.EntryHotkeyKey = _entryKey;
            Config.EntryRectX = _entryRect.X;
            Config.EntryRectY = _entryRect.Y;
            Config.EntryRectW = _entryRect.Width;
            Config.EntryRectH = _entryRect.Height;
            Config.EntryRectColor = _entryColor;
            // 预览类型分类开关（关掉的类别不再弹预览窗）
            if (_pvImageToggle != null) Config.PreviewImage = _pvImageToggle.On;
            if (_pvVideoToggle != null) Config.PreviewVideo = _pvVideoToggle.On;
            if (_pvAudioToggle != null) Config.PreviewAudio = _pvAudioToggle.On;
            if (_pvDocToggle != null) Config.PreviewDocument = _pvDocToggle.On;
            if (_pvTextToggle != null) Config.PreviewText = _pvTextToggle.On;
            if (_pvArchiveToggle != null) Config.PreviewArchive = _pvArchiveToggle.On;
            if (_pvFontToggle != null) Config.PreviewFont = _pvFontToggle.On;
            if (_pvOtherToggle != null) Config.PreviewOther = _pvOtherToggle.On;
            if (_pvSpeedToggle != null) Config.SpeedToggle = _pvSpeedToggle.On;
            if (_ffmpegPathBox != null)
            {
                string fp = (_ffmpegPathBox.Text ?? "").Trim();
                if (fp != Config.FfmpegPath)
                {
                    Config.FfmpegPath = fp;
                    FfmpegTools.ResetCache();   // 路径变了要重新探测 ffmpeg
                    _fnUpdateFfmpegStatus();
                }
            }
            Program.ApplySettingsLive(hotkeys, panels);
        }

        // 任一模式下拉选了"按住 Alt" → 亮出互斥说明
        private void _fnUpdateAltGateHint()
        {
            if (_altGateHint == null) return;
            // 三态提示：①某功能选了"按住 Alt" → 互斥说明；②两功能都不再"总是"触发 →
            // "按住 Alt 禁用"已无用武之地（保留功能只提示，不改行为）；③隐藏。
            // ①优先于②（更具体、更易误解的场景先解释）
            int ex = _modeExplorer != null ? _modeExplorer.SelectedIndex : 1;
            int pv = _modePreview != null ? _modePreview.SelectedIndex : 1;
            string text = null;
            if (ex == 4 || pv == 4) text = I18n.T("alt_gate_hint");
            else if (ex != 1 && pv != 1) text = I18n.T("alt_suppress_redundant");
            _altGateHint.Visible = text != null;
            if (text == null) return;
            _altGateHint.Text = text;
            _altGateHint.ForeColor = Config.IsDark ? ColorTranslator.FromHtml("#e5b84f") : ColorTranslator.FromHtml("#a8720a");
        }

        // 模式下拉（悬停浏览/文件预览共用）：0 关闭 1 总是 2 按住Ctrl 3 按住Shift 4 按住Alt
        private DarkCombo _fnMakeModeCombo(int mode)
        {
            var cb = new DarkCombo { Font = new Font(Config.FontName, 8.5f), Width = 130, Height = 24 };
            cb.SetItems(new object[] { I18n.T("mode_off"), I18n.T("mode_always"), I18n.T("mode_ctrl"), I18n.T("mode_shift"), I18n.T("mode_alt") },
                Math.Max(0, Math.Min(4, mode)));
            return cb;
        }



        // Config 三个底层字段（启用/门控开/门控键）→ 模式档位；组合键设置已简化掉，归入 Ctrl 档
        private static int ModeFromConfig(bool enabled, bool gateOn, int mods)
        {
            if (!enabled) return 0;
            if (!gateOn) return 1;
            if (mods == 1) return 3;
            if (mods == 4) return 4;
            return 2;
        }

        // 托盘勾选改动 Config 后回叫：模式下拉回读 Config，设置窗开着也不出现两处说法不一
        internal void SyncFromTray()
        {
            if (_modeExplorer == null || _modePreview == null) return;
            _internalSync = true;
            try
            {
                _modeExplorer.SelectedIndex = ModeFromConfig(Config.ExplorerEnabled, Config.FolderGateEnabled, Config.FolderGateMods);
                _modePreview.SelectedIndex = ModeFromConfig(Config.PreviewFiles, Config.PreviewGateEnabled, Config.PreviewGateMods);
            }
            finally { _internalSync = false; }
            _fnUpdateAltGateHint();
        }

        // 模式档位 → 底层字段。门控键含 Alt 时与"按住 Alt 禁用"互补：按 Alt 是放行而非禁用
        private static void ApplyMode(bool explorer, int mode)
        {
            bool en = mode > 0;
            bool gate = mode >= 2;
            int mods = mode == 3 ? 1 : mode == 4 ? 4 : 2;
            if (explorer) { Config.ExplorerEnabled = en; Config.FolderGateEnabled = gate; Config.FolderGateMods = mods; }
            else { Config.PreviewFiles = en; Config.PreviewGateEnabled = gate; Config.PreviewGateMods = mods; }
        }

        private void _fnUpdateHoverState()
        {
            if (_hoverPill == null) return;
            _hoverPill.Enabled = _hoverToggle.On;
            _fnRefreshChips();
        }

        private void _fnUpdateCardState()
        {
            if (_cardPill == null) return;
            _cardPill.Enabled = _cardToggle.On;
            _fnRefreshChips();
        }

        private void _fnUpdateEntryState()
        {
            if (_entryPill == null) return;
            _entryPill.Enabled = _entryToggle.On;
            _fnRefreshChips();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_hoverRecording || _cardRecording || _entryRecording)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                if (e.KeyCode == Keys.Escape) { _fnStopCapture(); return; }
                if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.Menu || e.KeyCode == Keys.ShiftKey) return;
                int mods = 0;
                if ((e.Modifiers & Keys.Control) == Keys.Control) mods |= NativeMethods.MOD_CONTROL;
                if ((e.Modifiers & Keys.Alt) == Keys.Alt) mods |= NativeMethods.MOD_ALT;
                if ((e.Modifiers & Keys.Shift) == Keys.Shift) mods |= NativeMethods.MOD_SHIFT;
                if (_hoverRecording) { _hoverMods = mods; _hoverKey = (int)e.KeyCode; _hoverPill.SetHotkey(mods, (int)e.KeyCode); }
                else if (_cardRecording) { _cardMods = mods; _cardKey = (int)e.KeyCode; _cardPill.SetHotkey(mods, (int)e.KeyCode); }
                else { _entryMods = mods; _entryKey = (int)e.KeyCode; _entryPill.SetHotkey(mods, (int)e.KeyCode); }
                _fnStopCapture();
                _fnApplySettings(true, false);   // 快捷键录制完成即注册生效
                return;
            }
            if (e.KeyCode == Keys.Escape) Close();
        }

        // ================= 主题 =================

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Win11.ApplyRounded(Handle, Config.IsDark);
        }

        private void ApplyThemeColors()
        {
            BackColor = Config.BG;
            ForeColor = Config.FG;
            _fnApplyTheme(Controls);
            if (_expandBtn != null) { _expandBtn.BackColor = Config.BG; _expandBtn.FlatAppearance.BorderColor = Config.Border; _expandBtn.FlatAppearance.MouseOverBackColor = Config.BgHover; _expandBtn.ForeColor = Config.FG; }
            if (_areaPickBtn != null) { _areaPickBtn.BackColor = Config.Accent; _areaPickBtn.FlatAppearance.MouseOverBackColor = Config.Accent; }
            if (_areaClearBtn != null) { _areaClearBtn.BackColor = Config.BG; _areaClearBtn.FlatAppearance.BorderColor = Config.Border; _areaClearBtn.FlatAppearance.MouseOverBackColor = Config.BgHover; _areaClearBtn.ForeColor = Config.FG; }
            if (_verLbl != null) _verLbl.ForeColor = Config.PlaceholderFg;
            if (_closeBtn != null) { _closeBtn.ForeColor = Config.PlaceholderFg; _closeBtn.BackColor = Config.BG; }
        }

        private void _fnApplyTheme(Control.ControlCollection coll)
        {
            foreach (Control c in coll)
            {
                if (c is Button) continue;                                  // 尾部统一处理
                if (c is Separator) { c.BackColor = Config.Border; continue; }
                if (c is AccGroup) { ((AccGroup)c).ApplyTheme(); continue; }
                if (c is SlimScrollBar) { c.Invalidate(); continue; }       // 透明底自绘，不覆盖
                if (c is ToggleSwitch || c is HotkeyPill || c is DelayStepper || c is ChipLabel) { c.Invalidate(); continue; }
                if (c is DarkCombo) { c.Invalidate(); continue; }
                if (c is ComboBox || c is PictureBox) continue;
                if ((c.Tag as string) == "paint") { c.Invalidate(); continue; }
                if ((c.Tag as string) == "sub") { c.ForeColor = Config.PlaceholderFg; continue; }
                c.BackColor = Config.BG;
                c.ForeColor = Config.FG;
                var link = c as LinkLabel;
                if (link != null) link.LinkColor = Config.Accent;
                _fnApplyTheme(c.Controls);
            }
        }

        // ================= 静态展示工具（Program.cs 依赖） =================

        public static string ModsToString(int mods)
        {
            var parts = new List<string>();
            if ((mods & NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((mods & NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
            if ((mods & NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");
            return parts.Count > 0 ? string.Join("+", parts) + "+" : "";
        }

        public static string KeyToString(int key)
        {
            if (key == 0) return I18n.T("hotkey_none");
            return ((Keys)key).ToString();
        }

        public static string HotkeyDisplay(bool enabled, int mods, int key)
        {
            if (!enabled) return I18n.T("hotkey_disabled");
            return ModsToString(mods) + KeyToString(key);
        }

        // ================= 自绘控件 =================

        private class Separator : Panel { }

        // 可视区容器：双缓冲 + 裁切滚动内容层（WinForms 子控件溢出父 bounds 默认被裁剪）
        private class ViewportPanel : Panel
        {
            public ViewportPanel() { DoubleBuffered = true; AutoScroll = false; }
        }

        // 手风琴分组：标题行（点 + 标题 + chip + 开关 + 箭头）+ 可折叠内容行
        private class AccGroup : Panel
        {
            public const int HeadH = 38;
            public const int InL = 14;
            public const int InR = 14;

            private bool _open;
            private int _contentH;
            private readonly Panel _head;
            private readonly DotIcon _dot;
            private readonly Label _title;
            private readonly ChevronIcon _chev;
            private readonly List<ChipLabel> _chips = new List<ChipLabel>();
            private ToggleSwitch _toggle;
            public event EventHandler HeadClicked;

            public AccGroup(int width, string title, bool open)
            {
                Width = width;
                Height = HeadH;
                BackColor = Config.BG;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                _open = open;

                _head = new Panel
                {
                    Location = new Point(1, 1),
                    Size = new Size(width - 2, HeadH - 1),
                    BackColor = Config.BgHover,
                    Cursor = Cursors.Hand
                };
                _dot = new DotIcon { Location = new Point(12, 15), Cursor = Cursors.Hand };
                _title = new Label
                {
                    Text = title,
                    AutoSize = false,
                    Size = new Size(width - 2 - 25 - 195, HeadH - 4),
                    Location = new Point(24, 1),
                    TextAlign = ContentAlignment.MiddleLeft,
                    AutoEllipsis = true,
                    Font = new Font(Config.FontName, 10f, FontStyle.Bold),
                    BackColor = Config.BgHover,
                    Cursor = Cursors.Hand
                };
                _chev = new ChevronIcon { Location = new Point(width - 2 - 13 - 12, 13), Cursor = Cursors.Hand };
                _head.Controls.Add(_dot);
                _head.Controls.Add(_title);
                _head.Controls.Add(_chev);
                Controls.Add(_head);

                EventHandler click = (s, e) => { if (HeadClicked != null) HeadClicked(this, EventArgs.Empty); };
                _head.Click += click;
                _title.Click += click;
                _dot.Click += click;
                _chev.Click += click;
                _dot.Lit = open;
                _chev.Up = open;
            }

            public void SetToggle(ToggleSwitch t)
            {
                _toggle = t;
                t.Location = new Point(_chev.Left - 10 - t.Width, (HeadH - t.Height) / 2);
                _head.Controls.Add(t);
                PlaceChips();
            }

            public ChipLabel AddChip(string text)
            {
                return AddChip(text, false);
            }

            public ChipLabel AddChip(string text, bool amber)
            {
                var c = new ChipLabel(text, amber);
                _chips.Add(c);
                _head.Controls.Add(c);
                PlaceChips();
                _fnSyncChips();
                return c;
            }

            public LinkLabel AddHeadLink(string text, string url)
            {
                var link = new LinkLabel
                {
                    Text = text,
                    AutoSize = true,
                    Font = new Font(Config.FontName, 8.5f),
                    LinkArea = new LinkArea(0, text.Length),
                    Cursor = Cursors.Hand,
                    BackColor = Config.BgHover
                };
                link.LinkColor = Config.Accent;
                link.VisitedLinkColor = Config.Accent;
                link.Click += (s, e) => { try { System.Diagnostics.Process.Start(url); } catch { } };
                int titleTextW = TextRenderer.MeasureText(_title.Text, _title.Font).Width + 4;
                _title.Width = Math.Min(_title.Width, titleTextW);
                int lx = _title.Right + 8;
                foreach (Control c in _head.Controls)
                    if (c is LinkLabel) lx = Math.Max(lx, c.Right + 8);
                link.Location = new Point(lx, (HeadH - link.PreferredHeight) / 2);
                _head.Controls.Add(link);
                return link;
            }

            public void SetChip(int idx, string text)
            {
                if (idx < 0 || idx >= _chips.Count) return;
                var c = _chips[idx];
                if (string.IsNullOrEmpty(text)) c.SetShown(false);
                else { c.SetShown(true); c.SetText(text); }
                PlaceChips();
                _fnSyncChips();
            }

            private void PlaceChips()
            {
                int rightEdge = (_toggle != null ? _toggle.Left : _chev.Left) - 12;
                for (int i = _chips.Count - 1; i >= 0; i--)
                {
                    var c = _chips[i];
                    if (!c.Shown) continue;
                    c.Location = new Point(rightEdge - c.Width, (HeadH - c.Height) / 2);
                    rightEdge -= c.Width + 5;
                }
            }

            private void _fnSyncChips()
            {
                foreach (var c in _chips) c.Visible = (!_open) && c.Shown;
            }

            public bool Open
            {
                get { return _open; }
                set
                {
                    if (_open == value) return;
                    _open = value;
                    _dot.Lit = value;
                    _chev.Up = value;
                    _dot.Invalidate();
                    _chev.Invalidate();
                    foreach (Control c in Controls)
                        if (c != _head) c.Visible = value;
                    _fnSyncChips();
                    Height = _open ? HeadH + _contentH : HeadH;
                    Invalidate();
                }
            }

            // 内容行：左标题/描述、右控件；描述限宽自动换行动态撑高
            public int AddRow(int y, string title, string desc, Control right)
            {
                var t = new Label
                {
                    Text = title,
                    Location = new Point(InL, HeadH + y),
                    AutoSize = true,
                    Font = new Font(Config.FontName, 9.5f),
                    Visible = _open
                };
                Controls.Add(t);
                Label d = null;
                int contentRight = Width - InR;
                int descW = 0;
                if (!string.IsNullOrEmpty(desc))
                {
                    int descRight = right != null ? contentRight - right.Width - 24 : contentRight;
                    descW = Math.Max(160, descRight - InL);
                    d = new Label
                    {
                        Text = desc,
                        Location = new Point(InL, HeadH + y + 19),
                        AutoSize = true,
                        MaximumSize = new Size(descW, 0),
                        ForeColor = Config.PlaceholderFg,
                        Font = new Font(Config.FontName, 8f),
                        Tag = "sub",
                        Visible = _open
                    };
                    Controls.Add(d);
                }
                if (right != null)
                {
                    int dy = right.Height <= 22 ? 0 : -2;
                    right.Location = new Point(contentRight - right.Width, HeadH + y + dy);
                    right.Visible = _open;
                    Controls.Add(right);
                }
                // 显式按渲染同算法测高：PreferredHeight 在字体回退/DPI 差异下会少算换行行数，
                // 描述最后一行被下一行压住（ffmpeg 路径行的实例）
                int descH = 0;
                if (d != null)
                    descH = TextRenderer.MeasureText(desc, d.Font, new Size(descW, 10000),
                        TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height;
                int rowH = d != null ? Math.Max(RowH, 19 + descH + 14) : RowH;   // +14 保险余量：不同 DPI/字体回退下测量仍可能差半个字形
                _contentH = Math.Max(_contentH, y + rowH);
                if (_open) Height = HeadH + _contentH;
                return y + rowH;
            }

            // 手动行（自行定位控件）扩展内容高度
            public void ExpandRow(int y, int rowH)
            {
                _contentH = Math.Max(_contentH, y + rowH);
                if (_open) Height = HeadH + _contentH;
            }

            // 自由排版组（关于）：说明段落手动定位，几何审计的行式规则不适用
            public bool FreeformLayout;

            public void FinishRows()
            {
                _contentH += 10;
                if (_open) Height = HeadH + _contentH;
            }

            public void ApplyTheme()
            {
                BackColor = Config.BG;
                _head.BackColor = Config.BgHover;
                _title.BackColor = Config.BgHover;
                foreach (Control c in _head.Controls)
                {
                    if (c is DotIcon || c is ChevronIcon || c is ChipLabel || c is ToggleSwitch)
                    {
                        c.BackColor = Config.BgHover;
                        c.Invalidate();
                        continue;
                    }
                    // 标题标签必须跟随头部条底色，否则浅色系下出现与头部割裂的白色矩形
                    if (c == _title) { c.BackColor = Config.BgHover; c.ForeColor = Config.FG; continue; }
                    var hl = c as LinkLabel;
                    if (hl != null) { _fnThemeLink(hl); continue; }
                    c.BackColor = Config.BG;
                    c.ForeColor = Config.FG;
                }
                foreach (Control c in Controls)
                {
                    if (c == _head) continue;
                    if (c is HotkeyPill || c is DelayStepper) { c.Invalidate(); continue; }
                    if (c is ComboBox || c is DarkCombo) { c.Invalidate(); continue; }
                    if (c is Button) continue;
                    if ((c.Tag as string) == "paint") { c.Invalidate(); continue; }
                    // LinkLabel 检查必须在 "sub" 之前：MakeLink 出来的链接全带 Tag="sub"，
                    // 先走 sub 分支就永远刷不到 LinkColor，深色下一片系统深蓝看不清
                    var ll = c as LinkLabel;
                    if (ll != null) { _fnThemeLink(ll); continue; }
                    if ((c.Tag as string) == "sub") { c.ForeColor = Config.PlaceholderFg; continue; }
                    c.BackColor = Config.BG;
                    c.ForeColor = Config.FG;
                }
                Invalidate();
            }


            // 链接主题化：系统默认蓝或任一主题的 Accent（上次刷的可能是切换前的旧 Accent）都刷成当前
            // Accent；自定义色的不动（如更新提示红点 #E03131）
            private static void _fnThemeLink(LinkLabel link)
            {
                bool themed = link.LinkColor == Color.FromArgb(0, 0, 255)                              // LinkLabel 默认系统蓝
                    || link.LinkColor == ColorTranslator.FromHtml("#0067c0")                           // 浅色 Accent
                    || link.LinkColor == ColorTranslator.FromHtml("#6aa9ff");                          // 深色 Accent
                if (!themed) return;
                link.LinkColor = Config.Accent;
                link.ActiveLinkColor = Config.Accent;
                link.Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                using (var p = new Pen(Config.Border, 1))
                {
                    e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
                    if (_open) e.Graphics.DrawLine(p, 1, HeadH, Width - 2, HeadH);
                }
            }
        }

        // 摘要 chip：折叠态显示当前配置（快捷键/延时/排序等）
        private class ChipLabel : Control
        {
            private string _text;
            private readonly bool _amber;
            private bool _shown = true;

            public bool Shown { get { return _shown; } }

            public ChipLabel(string text, bool amber)
            {
                _text = text;
                _amber = amber;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
                SetStyle(ControlStyles.Selectable, false);
                _fnMeasure();
            }

            public void SetShown(bool v) { _shown = v; }

            public void SetText(string t)
            {
                _text = t;
                _fnMeasure();
                Invalidate();
            }

            private void _fnMeasure()
            {
                using (var f = new Font(Config.FontName, 8f))
                {
                    // NoPadding 测量对 CJK 字符（如"秒"）会偏窄，绘制时贴边被裁——用带内边距的
                    // 默认测量再放宽 10px，保证任何文本四周都有余量
                    var sz = TextRenderer.MeasureText(_text, f);
                    Size = new Size(sz.Width + 10, 20);
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                bool dark = Config.IsDark;
                Color bg = _amber ? (dark ? ColorTranslator.FromHtml("#453a15") : ColorTranslator.FromHtml("#fdf0cd")) : Config.BgHover;
                // 非 amber chip 文字不能用 PlaceholderFg：浅色 #7a7a88 压在 #efeff2 底上对比度不足看不清
                Color fg = _amber ? (dark ? ColorTranslator.FromHtml("#e5b84f") : ColorTranslator.FromHtml("#a8720a"))
                                  : (dark ? ColorTranslator.FromHtml("#9a9aa8") : ColorTranslator.FromHtml("#55555f"));
                Color border = _amber ? bg : Config.Border;
                using (var b = new SolidBrush(bg)) g.FillRectangle(b, 0, 0, Width, Height);
                using (var p = new Pen(border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
                using (var f = new Font(Config.FontName, 8f))
                    TextRenderer.DrawText(g, _text, f, new Rectangle(0, 0, Width, Height), fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        // 深色下拉：ComboBox 的边框/箭头按钮是系统 chrome（OwnerDraw 只能画内容区；进程未声明
        // 暗色感知时系统暗色也渲染白框，DarkMode_Explorer 又依赖未公开 API 的进程级初始化），
        // 自绘整个控件 + 弹层列表，颜色全部实时读 Config → 跟随应用主题（含"跟随系统"）。
        // 不可聚焦（Selectable=false）→ 从根上没有系统焦点环。只支持 DropDownList 语义
        private class DarkCombo : Control
        {
            public event EventHandler SelectedIndexChanged;
            private int _index = -1;
            private static DarkComboPopup _popup;
            private static DarkCombo _popupOwner;

            public class ComboItemCollection
            {
                internal readonly List<string> List = new List<string>();
                public void Add(string s) { List.Add(s); }
                public void Clear() { List.Clear(); }
                public int Count { get { return List.Count; } }
                public bool Contains(string s) { return s != null && List.Contains(s); }
                public void AddRange(object[] items) { foreach (var it in items) List.Add(it == null ? "" : it.ToString()); }
            }
            public readonly ComboItemCollection Items = new ComboItemCollection();

            public DarkCombo()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                    | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                SetStyle(ControlStyles.Selectable, false);
                Cursor = Cursors.Hand;
                Height = 24;
            }

            internal int ItemCount { get { return Items.Count; } }
            internal string ItemAt(int i) { return Items.List[i]; }

            public void SetItems(object[] items, int index)
            {
                Items.Clear();
                foreach (var it in items) Items.Add(it.ToString());
                SelectedIndex = index;
            }
            public int SelectedIndex
            {
                get { return _index; }
                set { SetIndex(value, false); }
            }
            public object SelectedItem
            {
                get { return _index >= 0 && _index < Items.Count ? (object)Items.List[_index] : null; }
                set { SetIndex(value == null ? -1 : Items.List.IndexOf(value.ToString()), false); }
            }
            private void SetIndex(int i, bool silent)
            {
                if (i == _index) return;
                _index = i;
                Invalidate();
                if (!silent)
                {
                    var h = SelectedIndexChanged;
                    if (h != null) h(this, EventArgs.Empty);
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                using (var b = new SolidBrush(Enabled ? Config.BG : Config.BgHover)) g.FillRectangle(b, ClientRectangle);
                using (var p = new Pen(Config.Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
                string text = _index >= 0 && _index < Items.Count ? Items.List[_index] : "";
                Color fg = Enabled ? Config.FG : Config.PlaceholderFg;
                TextRenderer.DrawText(g, text, Font, new Rectangle(7, 0, Width - 32, Height), fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, "▾", Font, new Rectangle(Width - 24, 0, 20, Height), fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            protected override void OnMouseClick(MouseEventArgs e)
            {
                base.OnMouseClick(e);
                if (e.Button == MouseButtons.Left && Enabled && Items.Count > 0) ShowPopup();
            }

            private void ShowPopup()
            {
                ClosePopup();
                _popupOwner = this;
                _popup = new DarkComboPopup(this);
                var wa = Screen.FromControl(this).WorkingArea;
                var pt = PointToScreen(new Point(-2, Height + 2));
                int ph = ItemCount * 24 + 6;
                if (pt.Y + ph > wa.Bottom) pt.Y = Math.Max(wa.Top, PointToScreen(Point.Empty).Y - ph - 2);
                pt.X = Math.Min(Math.Max(wa.Left, pt.X), wa.Right - Width - 8);
                _popup.Bounds = new Rectangle(pt.X, pt.Y, Math.Max(120, Width), ph);
                _popup.Show(this);
                _popup.Deactivate += (s, e) => ClosePopup();
            }
            internal void ClosePopup()
            {
                if (_popup == null) return;
                try { _popup.Close(); _popup.Dispose(); } catch { }
                _popup = null; _popupOwner = null;
            }
            internal void Commit(int idx)
            {
                ClosePopup();
                if (idx < 0 || idx >= Items.Count || idx == _index) return;
                SetIndex(idx, false);
            }
        }

        // 下拉弹层：无边框置顶小窗，条目自绘（悬停 BtnHover、当前项 Accent），点击外部自动关
        private class DarkComboPopup : Form
        {
            private readonly DarkCombo _combo;
            private int _hover = -1;

            public DarkComboPopup(DarkCombo combo)
            {
                _combo = combo;
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                ShowInTaskbar = false;
                TopMost = true;
                DoubleBuffered = true;
                BackColor = Config.BG;
                Height = combo.ItemCount * 24 + 6;
            }

            private Rectangle ItemRect(int i) { return new Rectangle(3, 3 + i * 24, Width - 6, 24); }

            protected override void OnPaint(PaintEventArgs e)
            {
                using (var p = new Pen(Config.Border)) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
                for (int i = 0; i < _combo.ItemCount; i++)
                {
                    var rc = ItemRect(i);
                    bool cur = i == _combo.SelectedIndex;
                    using (var b = new SolidBrush(i == _hover ? Config.BtnHover : Config.BG)) e.Graphics.FillRectangle(b, rc);
                    TextRenderer.DrawText(e.Graphics, _combo.ItemAt(i), Font, rc, cur ? Config.Accent : Config.FG,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                int i = Hit(e.Location);
                if (i != _hover) { _hover = i; Invalidate(); }
            }
            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                if (_hover != -1) { _hover = -1; Invalidate(); }
            }
            protected override void OnMouseClick(MouseEventArgs e)
            {
                base.OnMouseClick(e);
                int i = Hit(e.Location);
                if (i >= 0) _combo.Commit(i);
            }
            protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
            {
                if (keyData == Keys.Escape) { _combo.ClosePopup(); return true; }
                return base.ProcessCmdKey(ref msg, keyData);
            }
            private int Hit(Point p) { for (int i = 0; i < _combo.ItemCount; i++) if (ItemRect(i).Contains(p)) return i; return -1; }
        }

        // 标题行状态点：展开 = Accent
        private class DotIcon : Control
        {
            public bool Lit;

            public DotIcon()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
                SetStyle(ControlStyles.Selectable, false);
                Size = new Size(8, 8);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                using (var b = new SolidBrush(Lit ? Config.Accent : Config.Border))
                    e.Graphics.FillEllipse(b, 1, 1, 6, 6);
            }
        }

        // 标题行箭头：收起朝下 / 展开朝上
        private class ChevronIcon : Control
        {
            public bool Up;

            public ChevronIcon()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
                SetStyle(ControlStyles.Selectable, false);
                Size = new Size(12, 12);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                using (var p = new Pen(Config.PlaceholderFg, 1.6f))
                {
                    if (Up) { g.DrawLine(p, 2, 7, 6, 3); g.DrawLine(p, 6, 3, 10, 7); }
                    else { g.DrawLine(p, 2, 5, 6, 9); g.DrawLine(p, 6, 9, 10, 5); }
                }
            }
        }

        // 开关：激活时 Accent 底 + 白色滑块
        private class ToggleSwitch : Control
        {
            private bool _on;
            public event EventHandler Toggled;

            public bool On
            {
                get { return _on; }
                set { if (_on == value) return; _on = value; Invalidate(); }
            }

            public ToggleSwitch()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                SetStyle(ControlStyles.Selectable, false);
                Size = new Size(44, 22);
                Cursor = Cursors.Hand;
                Click += (s, e) => { On = !On; if (Toggled != null) Toggled(this, EventArgs.Empty); };
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Color track;
                if (!Enabled) track = Config.Border;
                else if (_on) track = Config.Accent;
                else track = Config.IsDark ? ColorTranslator.FromHtml("#3a3a42") : ColorTranslator.FromHtml("#d4d4da");
                using (var path = _fnRoundRect(0.5f, 0.5f, Width - 1, Height - 1, (Height - 1) / 2f))
                using (var b = new SolidBrush(track))
                    g.FillPath(b, path);
                int d = Height - 4;
                int x = _on ? Width - d - 2 : 2;
                using (var b = new SolidBrush(Color.White))
                    g.FillEllipse(b, x, 2, d, d);
            }
        }

        // 键位胶囊：[Ctrl]+[Alt]+[Q]，点击进入录制
        private class HotkeyPill : Control
        {
            private int _mods;
            private int _key;
            private bool _rec;
            public event EventHandler RecordClick;

            public bool Recording
            {
                get { return _rec; }
                set { _rec = value; _fnRelayout(); Invalidate(); }
            }

            public HotkeyPill()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
                SetStyle(ControlStyles.Selectable, false);
                Cursor = Cursors.Hand;
                Size = new Size(60, 24);
                Click += (s, e) => { if (Enabled && RecordClick != null) RecordClick(this, EventArgs.Empty); };
            }

            public void SetHotkey(int mods, int key)
            {
                _mods = mods;
                _key = key;
                _fnRelayout();
                Invalidate();
            }

            private List<string> _fnParts()
            {
                if (_rec) return new List<string> { I18n.T("hotkey_press_key") };
                var parts = new List<string>();
                if ((_mods & NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
                if ((_mods & NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
                if ((_mods & NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");
                parts.Add(SettingsForm.KeyToString(_key));
                return parts;
            }

            private void _fnRelayout()
            {
                var parts = _fnParts();
                int w = 0;
                using (var f = new Font(Config.FontName, 8.5f))
                {
                    foreach (var p in parts)
                    {
                        var sz = TextRenderer.MeasureText(p, f, Size.Empty, TextFormatFlags.NoPadding);
                        w += sz.Width + 16;
                    }
                }
                w += Math.Max(0, parts.Count - 1) * 16;
                Size = new Size(Math.Max(w, 60), 24);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var parts = _fnParts();
                using (var f = new Font(Config.FontName, 8.5f))
                {
                    int x = 0;
                    for (int i = 0; i < parts.Count; i++)
                    {
                        int w = TextRenderer.MeasureText(parts[i], f, Size.Empty, TextFormatFlags.NoPadding).Width + 16;
                        Color bg = !Enabled ? Config.BG : (_rec ? Config.Accent : Config.BgHover);
                        Color border = _rec ? Config.Accent : Config.Border;
                        Color fg = !Enabled ? Config.PlaceholderFg : (_rec ? Color.White : Config.FG);
                        using (var path = _fnRoundRect(x + 0.5f, 1.5f, w - 1, Height - 3, 4))
                        {
                            using (var b = new SolidBrush(bg)) g.FillPath(b, path);
                            using (var p = new Pen(border)) g.DrawPath(p, path);
                        }
                        TextRenderer.DrawText(g, parts[i], f, new Rectangle(x, 0, w, Height), fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                        x += w;
                        if (i < parts.Count - 1)
                        {
                            TextRenderer.DrawText(g, "+", f, new Rectangle(x, 0, 16, Height), Enabled ? Config.PlaceholderFg : Config.Border, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                            x += 16;
                        }
                    }
                }
            }
        }

        // 步进器：[-] 0.50 秒 [+]
        private class DelayStepper : Control
        {
            private double _val = 0.30;

            public event EventHandler ValueChanged;

            public double Value
            {
                get { return _val; }
                set
                {
                    var nv = Math.Max(0.10, Math.Min(3.00, Math.Round(value, 2)));
                    if (nv == _val) return;
                    _val = nv;
                    Invalidate();
                    if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
                }
            }

            public DelayStepper()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
                SetStyle(ControlStyles.Selectable, false);
                Size = new Size(116, 26);
                Cursor = Cursors.Hand;
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.X < 22) Value = _val - 0.05;
                else if (e.X > Width - 22) Value = _val + 0.05;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = _fnRoundRect(0.5f, 0.5f, Width - 1, Height - 1, 4))
                {
                    using (var b = new SolidBrush(Config.BgHover)) g.FillPath(b, path);
                    using (var p = new Pen(Config.Border)) g.DrawPath(p, path);
                }
                using (var f = new Font(Config.FontName, 8.5f))
                {
                    TextRenderer.DrawText(g, "−", f, new Rectangle(0, 0, 22, Height), Config.FG, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    TextRenderer.DrawText(g, "+", f, new Rectangle(Width - 22, 0, 22, Height), Config.FG, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    string mid = string.Format("{0:0.00} {1}", _val, I18n.T("delay_unit"));
                    TextRenderer.DrawText(g, mid, f, new Rectangle(22, 0, Width - 44, Height), Config.FG, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                using (var p = new Pen(Config.Border))
                {
                    g.DrawLine(p, 22, 4, 22, Height - 4);
                    g.DrawLine(p, Width - 22, 4, Width - 22, Height - 4);
                }
            }
        }

        // 毫秒步进器：[-] 350 ms [+]，关闭延迟用（秒级粒度太粗，120/350/800ms 是常见档位）
        private class MsStepper : Control
        {
            private int _val = 350;

            public event EventHandler ValueChanged;

            public int Value
            {
                get { return _val; }
                set
                {
                    var nv = Math.Max(50, Math.Min(2000, value));
                    if (nv == _val) return;
                    _val = nv;
                    Invalidate();
                    if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
                }
            }

            public MsStepper()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
                SetStyle(ControlStyles.Selectable, false);
                Size = new Size(116, 26);
                Cursor = Cursors.Hand;
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.X < 22) Value = _val - 50;
                else if (e.X > Width - 22) Value = _val + 50;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = _fnRoundRect(0.5f, 0.5f, Width - 1, Height - 1, 4))
                {
                    using (var b = new SolidBrush(Config.BgHover)) g.FillPath(b, path);
                    using (var p = new Pen(Config.Border)) g.DrawPath(p, path);
                }
                using (var f = new Font(Config.FontName, 8.5f))
                {
                    TextRenderer.DrawText(g, "−", f, new Rectangle(0, 0, 22, Height), Config.FG, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    TextRenderer.DrawText(g, "+", f, new Rectangle(Width - 22, 0, 22, Height), Config.FG, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    TextRenderer.DrawText(g, _val + " ms", f, new Rectangle(22, 0, Width - 44, Height), Config.FG, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                using (var p = new Pen(Config.Border))
                {
                    g.DrawLine(p, 22, 4, 22, Height - 4);
                    g.DrawLine(p, Width - 22, 4, Width - 22, Height - 4);
                }
            }
        }

        private static GraphicsPath _fnRoundRect(float x, float y, float w, float h, float r)
        {
            var path = new GraphicsPath();
            float d = Math.Min(r * 2, Math.Min(w, h));
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + w - d, y, d, d, 270, 90);
            path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            path.AddArc(x, y + h - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}