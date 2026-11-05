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
        private const int SidebarW = 156;  // 侧栏导航宽度
        private int _contentW;             // 主区内容宽度（扣除侧栏+滚动条+内边距）
        private int _curPage = -1;          // 当前分页索引（-1 确保首次切换生效）
        private readonly List<Label> _navItems = new List<Label>();
        private static readonly int[] _boldNavPages = { 0, 2, 3, 4 };
        private static readonly int[] _indentNavPages = { 1 };
        // 侧栏显示顺序 → 实际 pageIdx（列表外观上移到预览列表下方，原预览内容下移并改名预览窗口）
        private static readonly int[] _navDisplayOrder = { 0, 2, 1, 3, 4, 5, 6, 7 };

        private readonly List<AccGroup> _groups = new List<AccGroup>();
        private AccGroup _hoverGroup;
        private AccGroup _windowGroup;
        private AccGroup _cardGroup;
        private AccGroup _cardTriggerGroup;
        private AccGroup _entryGroup;
        private AccGroup _tcGroup;
        private AccGroup _pvTypeGroup;
        private ViewportPanel _viewport;
        private FlowLayoutPanel _scrollPanel;
        private SlimScrollBar _scrollBar;
        private Button _expandBtn;
        private Label _closeBtn;
        private PictureBox _iconBox;
        private Label _verLbl;
        private LinkLabel _updateLbl;
        private Panel _sidebar;   // 侧栏导航（宽度固定，高度随窗口拉伸）
        // 窗口最小尺寸（约为默认 660×720 的 80%）
        private const int _minW = 528;
        private const int _minH = 576;

        private ToggleSwitch _hoverToggle;
        private ToggleSwitch _cardToggle;
        private ToggleSwitch _keepToggle;
        private ToggleSwitch _rowSwapToggle;
        private DarkCombo _modeExplorer;  // 悬停浏览模式：0 关闭 1 总是 2 按住Ctrl 3 按住Shift 4 按住Alt 5 长按
        private Label _modeExplorerDesc;  // 悬停浏览行的描述
        private Label _modePreviewDesc;   // 文件预览行的描述
        private bool _internalSync;       // 托盘→设置回写时抑制 SelectedIndexChanged 的反向应用
        private DarkCombo _modePreview;   // 文件预览模式（同上）
        private LongPressStepper _longPressStepper;
        private ToggleSwitch _pvMuteToggle;         // 视频预览默认静音
        private ToggleSwitch _entryToggle;
        // 预览类型分类开关（图片/视频/音频/文档/文本/压缩包/字体/其他）
        private ToggleSwitch _pvImageToggle;
        private ToggleSwitch _pvVideoToggle;
        private DelayStepper _pvVideoDelayStepper;  // 视频预览单独触发延时
        private DarkCombo _pvEdgeCombo;             // 文件预览边缘触发位置
        private MsStepper _pvEdgeWidthStepper;      // 边缘触发区宽度(px)
        private ToggleSwitch _pvAudioToggle;
        private ToggleSwitch _pvDocToggle;
        private ToggleSwitch _pvTextToggle;
        private ToggleSwitch _pvArchiveToggle;
        private ToggleSwitch _pvFontToggle;
        private ToggleSwitch _pvOtherToggle;
        private ToggleSwitch _pvSpeedToggle;
        private ToggleSwitch _pvWaveformToggle;
        private ToggleSwitch _pvSpectrumToggle;
        private TextBox _ffmpegPathBox;
        private Label _ffmpegStatusLbl;
        private TextBox _trayIconBox;
        private DarkCombo _trayMiddleCombo;
        private ToggleSwitch _trayShowExplorerToggle;
        private ToggleSwitch _trayShowPreviewToggle;
        private ToggleSwitch _trayShowOrigToggle;
        private ToggleSwitch _trayShowDebugLogToggle;
        private ToggleSwitch _trayShowHideTooltipToggle;
        private ToggleSwitch _trayShowButtonTipsToggle;
        private ToggleSwitch _trayShowAutostartToggle;
        private HotkeyPill _hoverPill;
        private HotkeyPill _cardPill;
        private HotkeyPill _entryPill;
        private DelayStepper _delayStepper;
        private MsStepper _closeDelayStepper;
        private MsStepper _entryDelayStepper;
        private MsStepper _pauseDelayStepper;

        private DarkCombo _cornerCombo;
        private DarkCombo _tcSortCombo;
        private DarkCombo _sortCombo;
        private ListPreviewPanel _listPreview;   // 列表外观·实时预览（模拟弹出面板）
        private CardPreviewPanel _cardPreview;   // 桌面卡片·外观实时预览（模拟一张卡片）
        private ToggleSwitch _statusFilterToggle;   // 列表底部格式筛选开关
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
            // 尺寸：默认 660×min(720, 工作区-60)；上次关闭时持久化的尺寸（Config.SettingsW/H）优先
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Width = Config.SettingsW >= _minW ? Math.Min(Config.SettingsW, wa.Width - 20) : 660;
            Height = Math.Min(Config.SettingsH >= _minH ? Config.SettingsH : 720, wa.Height - 60);
            MinimumSize = new Size(_minW, Math.Min(_minH, wa.Height - 60));
            ShowInTaskbar = false;
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);   // 拖拽调尺寸时整窗重绘（含 OnPaint 头部）
            KeyPreview = true;
            _contentW = Width - SidebarW - PadX * 2 - ScrollBarW;

            _hoverMods = Config.HotkeyModifiers;
            _hoverKey = Config.HotkeyKey;
            _cardMods = Config.CardHotkeyModifiers;
            _cardKey = Config.CardHotkeyKey;
            _entryMods = Config.EntryHotkeyModifiers;
            _entryKey = Config.EntryHotkeyKey;
            _entryRect = Config.EntryRectW >= 5 ? new Rectangle(Config.EntryRectX, Config.EntryRectY, Config.EntryRectW, Config.EntryRectH) : Rectangle.Empty;
            _entryColor = Array.IndexOf(_areaColors, Config.EntryRectColor) >= 0 ? Config.EntryRectColor : _areaColors[0];

            // ---- 头部（固定）：图标 + 标题 + 副标题 + 全部展开/收起 + 关闭 ----
            // 标题栏底色由 OnPaint 绘制（与组标题 BgHover 同色）
            try
            {
                using (var ic = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                if (ic != null)
                {
                    _iconBox = new PictureBox
                    {
                        Image = ic.ToBitmap(),
                        SizeMode = PictureBoxSizeMode.Zoom,
                        Size = new Size(28, 28),
                        Location = new Point(PadX, 10),
                        BackColor = Config.BgHover
                    };
                    _iconBox.MouseDown += (s, e) => _fnDrag(e);
                    Controls.Add(_iconBox);
                }
            }
            catch { }
            var titleLbl = new Label
            {
                // 顶部标题带版本号（原"设置"+副标题说明合并为一行）
                Text = "HoverExplorer v" + Program.Version + " " + I18n.T("settings"),
                Location = new Point(PadX + 34, 12),
                AutoSize = true,
                Font = new Font(Config.FontName, 13f, FontStyle.Bold),
                BackColor = Config.BgHover
            };
            titleLbl.MouseDown += (s, e) => _fnDrag(e);
            Controls.Add(titleLbl);

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
            _expandBtn.Visible = false;

            // ---- 内容滚动区（SlimScrollBar 自绘：无轨道 4px 滑块、悬停 8px；FlowLayoutPanel 垂直堆叠，组高度变化时下方自动下移）----
            // 结构：viewport（固定可视区，裁切溢出）→ _scrollPanel（内容层，Y 偏移滚动）+ _scrollBar（Dock Right）
            int viewH = Height - HeadAreaH;   // 赞助区已并入"关于"折叠组随滚动，不再预留固定高度；底部无按钮栏
            // ---- 侧栏导航 ----
            _sidebar = new HotEdgePanel()
            {
                Location = new Point(0, HeadAreaH),
                Size = new Size(SidebarW, viewH),
                BackColor = Config.BgHover
            };
            Controls.Add(_sidebar);
            int navY = 14;
            string[] navLabels = { I18n.T("nav_preview_list"), I18n.T("group_appearance"), I18n.T("group_content"), I18n.T("group_card"), I18n.T("group_entry"), I18n.T("group_advanced"), I18n.T("group_tray"), I18n.T("group_about") };
            for (int i = 0; i < navLabels.Length; i++)
            {
                int idx = i;
                var item = new Label
                {
                    Text = navLabels[i],
                    Location = new Point(0, navY),
                    Size = new Size(SidebarW - 2, 34),
                    TextAlign = ContentAlignment.MiddleLeft,
                    Padding = new Padding(Array.IndexOf(_indentNavPages, idx) >= 0 ? 36 : 18, 0, 0, 0),
                    Font = new Font(Config.FontName, 9f, Array.IndexOf(_boldNavPages, idx) >= 0 ? FontStyle.Bold : FontStyle.Regular),
                    Cursor = Cursors.Hand,
                    BackColor = Config.BgHover,
                    ForeColor = Config.FG,
                    Tag = "nav",
                };
                item.MouseEnter += (s, e) => { if (_navDisplayOrder[idx] != _curPage) item.BackColor = Config.BG; };
                item.MouseLeave += (s, e) => { if (_navDisplayOrder[idx] != _curPage) item.BackColor = Config.BgHover; };
                item.Click += (s, e) => _fnSwitchPage(_navDisplayOrder[idx]);
                _sidebar.Controls.Add(item);
                _navItems.Add(item);
                navY += 34;
            }

            _viewport = new ViewportPanel
            {
                Location = new Point(SidebarW, HeadAreaH),
                Size = new Size(Width - SidebarW, viewH),
                BackColor = Config.BG
            };
            _scrollPanel = new HotEdgeFlowPanel
            {
                Location = new Point(0, 0),
                Size = new Size(Width - SidebarW - ScrollBarW, viewH),
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

            // ==== 组：悬停预览·触发（page 0：预览窗口）====
            _hoverGroup = _fnNewGroup(I18n.T("group_trigger"), true, 0);
            _hoverToggle = new ToggleSwitch { On = Config.HotkeyEnabled };
            _hoverToggle.Toggled += (s, e) => { _fnUpdateHoverState(); _fnApplySettings(true, false); };
            _hoverGroup.AddChip(HotkeyDisplay(Config.HotkeyEnabled, _hoverMods, _hoverKey));
            _hoverGroup.AddChip(string.Format("{0:0.00} {1}", Config.HoverDelay, I18n.T("delay_unit")));

            int y = 8;
            _hoverPill = new HotkeyPill();
            _hoverPill.SetHotkey(_hoverMods, _hoverKey);
            _hoverPill.RecordClick += (s, e) => _fnStartHoverCapture();
            y = _fnMakeHotkeyRow(_hoverGroup, y, I18n.T("hotkey_row"), I18n.T("hotkey_desc"),
                _hoverToggle, _hoverPill, _fnStartHoverCapture,
                () => { _hoverPill.SetHotkey(0, 0); _hoverMods = 0; _hoverKey = 0; _fnApplySettings(true, false); });

            // 暂停触控区：独立框选矩形（设置方式复制触发区域那套：框选按钮+清除+矩形标签+桌面标识块）。
            // 鼠标驻留该区域 PauseRectDelayMs 后切换悬停启用/暂停；未框选时仅剩快捷键 Ctrl+Alt+Q
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
            // 暂停区行内的手动控件（矩形标签+框选/清除按钮）登记重排
            _hoverGroup.TagLastRow(null, py =>
            {
                _pauseRectLbl.Location = new Point(AccGroup.InL, AccGroup.HeadH + py + 19);
                _pausePickBtn.Location = new Point(_hoverGroup.Width - AccGroup.InR - _pausePickBtn.Width, AccGroup.HeadH + py);
                _pauseClearBtn.Location = new Point(_pausePickBtn.Left - _pauseClearBtn.Width - 8, AccGroup.HeadH + py);
            });

            // 暂停触控区驻留延时：紧跟暂停触控区行，与收藏·历史触发区域的延时相互独立
            _pauseDelayStepper = new MsStepper { Value = Config.PauseRectDelayMs };
            _pauseDelayStepper.ValueChanged += (s, e) => _fnApplySettings(false, false);
            y = _hoverGroup.AddRow(y, I18n.T("pause_delay_row"), I18n.T("pause_delay_desc"), _pauseDelayStepper);

            _delayStepper = new DelayStepper { Value = Config.HoverDelay };
            _delayStepper.ValueChanged += (s, e) => { _fnRefreshChips(); _fnApplySettings(false, false); };
            y = _hoverGroup.AddRow(y, I18n.T("delay_row"), I18n.T("hover_delay_desc"), _delayStepper);

            // 悬停浏览（桌面/资源管理器）与文件预览：各一个下拉搞定——关闭 / 总是 / 按住某键 / 长按才触发。
            // 即改即生效，ApplySettingsLive 会把托盘勾选态同步（原在"行为"组，归入"触发"更贴切）
            _modeExplorer = _fnMakeModeCombo(ModeFromConfig(Config.ExplorerEnabled, Config.FolderGateEnabled, Config.FolderGateMods));
            _modeExplorer.SelectedIndexChanged += (s, e) => _fnApplySettings(false, false);
            y = _hoverGroup.AddRow(y, I18n.T("explorer_hover"), I18n.T("mode_desc"), _modeExplorer, out _modeExplorerDesc);

            _modePreview = _fnMakeModeCombo(ModeFromConfig(Config.PreviewFiles, Config.PreviewGateEnabled, Config.PreviewGateMods));
            _modePreview.SelectedIndexChanged += (s, e) => _fnApplySettings(false, false);
            y = _hoverGroup.AddRow(y, I18n.T("file_preview"), I18n.T("mode_desc"), _modePreview, out _modePreviewDesc);

            _longPressStepper = new LongPressStepper { Value = Config.LongPressDelayMs };
            _longPressStepper.ValueChanged += (s, e) => _fnApplySettings(false, false);
            y = _hoverGroup.AddRow(y, I18n.T("long_press_delay"), I18n.T("long_press_delay_desc"), _longPressStepper);

            _hoverGroup.FinishRows();

            // ==== 组：行为（page 0）====
            _windowGroup = _fnNewGroup(I18n.T("group_window"), true, 0);
            _windowGroup.AddChip(Config.CloseDelayMs + " ms");
            _windowGroup.AddChip("");   // 排序 chip（_fnRefreshChips 填充）

            int yw = 8;
            _closeDelayStepper = new MsStepper { Value = Config.CloseDelayMs };
            _closeDelayStepper.ValueChanged += (s, e) => { _fnRefreshChips(); _fnApplySettings(false, false); };
            yw = _windowGroup.AddRow(yw, I18n.T("close_delay_row"), I18n.T("close_delay_desc"), _closeDelayStepper);

            _keepToggle = new ToggleSwitch { On = Config.KeepOnLeave };
            _keepToggle.Toggled += (s, e) => _fnApplySettings(false, false);
            yw = _windowGroup.AddRow(yw, I18n.T("keep_open"), I18n.T("keep_open_desc"), _keepToggle);

            // 默认排序（原独立"排序"组并入行为组）：文件夹列表的排列方式
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
            yw = _windowGroup.AddRow(yw, I18n.T("sort_default"), I18n.T("sort_default_desc"), _sortCombo);

            // 列表底部格式筛选：关闭后状态栏不显示格式标签（保留拖动/搜索入口），切换后刷新存活面板
            _statusFilterToggle = new ToggleSwitch { On = Config.StatusFilter };
            _statusFilterToggle.Toggled += (s, e) => _fnApplySettings(false, true);
            yw = _windowGroup.AddRow(yw, I18n.T("status_filter"), I18n.T("status_filter_desc"), _statusFilterToggle);

            // 说明文字（黄底黑字常显）
            var tipBg = Color.FromArgb(0xFF, 0xF5, 0x9C);
            var dblHint = new Label { Text = I18n.T("hint_dbl_click"), AutoSize = true, MaximumSize = new Size(_windowGroup.Width - PadX * 2, 0), BackColor = tipBg, ForeColor = Color.Black, Font = new Font(Config.FontName, 8f), Padding = new Padding(6, 3, 6, 3), Location = new Point(PadX, AccGroup.HeadH + yw + 6), Tag = "tip" };
            _windowGroup.Controls.Add(dblHint);
            var wheelHint = new Label { Text = I18n.T("hint_ctrl_wheel"), AutoSize = true, MaximumSize = new Size(_windowGroup.Width - PadX * 2, 0), BackColor = tipBg, ForeColor = Color.Black, Font = new Font(Config.FontName, 8f), Padding = new Padding(6, 3, 6, 3), Location = new Point(PadX, dblHint.Bottom + 4), Tag = "tip" };
            _windowGroup.Controls.Add(wheelHint);
            _windowGroup.ExpandRow(yw + 6 + dblHint.Height + 4 + wheelHint.Height + 4, 0);
            _windowGroup.AddTailHook(ty =>
            {
                int hw = Math.Max(160, _windowGroup.Width - PadX * 2);
                dblHint.MaximumSize = new Size(hw, 0);
                dblHint.Location = new Point(PadX, AccGroup.HeadH + ty + 6);
                wheelHint.MaximumSize = new Size(hw, 0);
                wheelHint.Location = new Point(PadX, dblHint.Bottom + 4);
                _windowGroup.NoticeContentBottom(ty + 6 + dblHint.Height + 4 + wheelHint.Height + 4);
            });

            _windowGroup.FinishRows();


            // ==== 组：预览类型（逐类开关，的"按类型预览"设置）====
            _pvTypeGroup = _fnNewGroup(I18n.T("group_content"), true, 1);
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

            // 全局设置（不分类型）：文件预览的触发位置——条目中段留给改名/右键/拖拽，
            // 只有停在左/右缘触发区才弹预览窗。放组首，避免被误读成下面某个类型的子设置
            _pvEdgeCombo = new DarkCombo { Font = new Font(Config.FontName, 8.5f), Width = 170, Height = 24 };
            _pvEdgeCombo.Items.AddRange(new object[] {
                I18n.T("edge_off"), I18n.T("edge_left"), I18n.T("edge_right"), I18n.T("edge_both"),
            });
            _pvEdgeCombo.SelectedIndex = Math.Max(0, Math.Min(3, Config.PreviewEdgeMode));
            _pvEdgeCombo.SelectedIndexChanged += (s, e) => { _fnApplySettings(false, false); };
            y = _pvTypeGroup.AddRow(y, I18n.T("pv_edge"), I18n.T("pv_edge_desc"), _pvEdgeCombo);

            _pvEdgeWidthStepper = new MsStepper { Value = Math.Max(50, Config.PreviewEdgeWidth) };
            _pvEdgeWidthStepper.ValueChanged += (s, e) => { _fnApplySettings(false, false); };
            y = _pvTypeGroup.AddRow(y, I18n.T("pv_edge_width"), null, _pvEdgeWidthStepper);

            _pvImageToggle = new ToggleSwitch { On = Config.PreviewImage };
            _pvImageToggle.Toggled += (s, e) => { _fnApplySettings(false, false); };
            y = _pvTypeGroup.AddRow(y, I18n.T("pv_type_image"), null, _pvImageToggle);

            _pvVideoToggle = new ToggleSwitch { On = Config.PreviewVideo };
            _pvVideoToggle.Toggled += (s, e) => { _fnApplySettings(false, false); };
            y = _pvTypeGroup.AddRow(y, I18n.T("pv_type_video"), null, _pvVideoToggle);

            _pvVideoDelayStepper = new DelayStepper { Value = Config.VideoPreviewDelay };
            _pvVideoDelayStepper.ValueChanged += (s, e) => { _fnApplySettings(false, false); };
            y = _pvTypeGroup.AddRow(y, I18n.T("pv_video_delay"), I18n.T("pv_video_delay_desc"), _pvVideoDelayStepper);

            _pvAudioToggle = new ToggleSwitch { On = Config.PreviewAudio };
            _pvAudioToggle.Toggled += (s, e) => { _fnApplySettings(false, false); };
            y = _pvTypeGroup.AddRow(y, I18n.T("pv_type_audio"), null, _pvAudioToggle);

            _pvWaveformToggle = new ToggleSwitch { On = Config.WaveformEnabled };
            _pvWaveformToggle.Toggled += (s, e) => { _fnApplySettings(false, false); };
            y = _pvTypeGroup.AddRow(y, I18n.T("pv_waveform"), I18n.T("pv_waveform_desc"), _pvWaveformToggle);

            _pvSpectrumToggle = new ToggleSwitch { On = Config.SpectrumEnabled };
            _pvSpectrumToggle.Toggled += (s, e) => { _fnApplySettings(false, false); };
            y = _pvTypeGroup.AddRow(y, I18n.T("pv_spectrum"), I18n.T("pv_spectrum_desc"), _pvSpectrumToggle);

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
            // ffmpeg 状态行（固定高 20 的手动行）登记进行栈
            _pvTypeGroup.AddBlock((by, w) =>
            {
                _ffmpegStatusLbl.Location = new Point(AccGroup.InL, AccGroup.HeadH + by + 2);
                return by + 20;
            });

            // 音频卡片滚轮提示（黄底黑字常显）
            var audioHint = new Label { Text = I18n.T("hint_audio_wheel"), AutoSize = true, MaximumSize = new Size(_pvTypeGroup.Width - PadX * 2, 0), BackColor = Color.FromArgb(0xFF, 0xF5, 0x9C), ForeColor = Color.Black, Font = new Font(Config.FontName, 8f), Padding = new Padding(6, 3, 6, 3), Location = new Point(PadX, AccGroup.HeadH + y + 6), Tag = "tip" };
            _pvTypeGroup.Controls.Add(audioHint);
            _pvTypeGroup.ExpandRow(y + 6 + audioHint.Height + 4, 0);
            _pvTypeGroup.AddTailHook(ty =>
            {
                audioHint.MaximumSize = new Size(Math.Max(160, _pvTypeGroup.Width - PadX * 2), 0);
                audioHint.Location = new Point(PadX, AccGroup.HeadH + ty + 6);
                _pvTypeGroup.NoticeContentBottom(ty + 6 + audioHint.Height + 4);
            });

            // 面板/预览键盘用法说明：小字号 + 自动换行（长文案放行内会超框被裁）
            var kbHint = new Label { Text = I18n.T("kb_hint"), AutoSize = true, MaximumSize = new Size(_pvTypeGroup.Width - PadX * 2, 0), BackColor = Config.ListHdbg, ForeColor = Config.FgDir, Font = new Font(Config.FontName, 7.5f), Padding = new Padding(6, 3, 6, 3), Location = new Point(PadX, AccGroup.HeadH + y + 6), Tag = "tip" };
            _pvTypeGroup.Controls.Add(kbHint);
            _pvTypeGroup.ExpandRow(y + 6 + kbHint.Height + 4, 0);
            _pvTypeGroup.AddTailHook(ty =>
            {
                kbHint.MaximumSize = new Size(Math.Max(160, _pvTypeGroup.Width - PadX * 2), 0);
                kbHint.Location = new Point(PadX, AccGroup.HeadH + ty + 6);
                _pvTypeGroup.NoticeContentBottom(ty + 6 + kbHint.Height + 4);
            });

            _pvTypeGroup.FinishRows();


            // ==== 组：列表配色 ====
            var palGroup = _fnNewGroup(I18n.T("group_appearance"), true, 2);
            y = 8;
            // 列表顶部滚轮提示 + 在线生成器链接（标题下方，黄底黑字常显）
            var listHint = new Label { Text = I18n.T("hint_list_wheel"), AutoSize = true, MaximumSize = new Size(palGroup.Width - PadX * 2, 0), BackColor = Color.FromArgb(0xFF, 0xF5, 0x9C), ForeColor = Color.Black, Font = new Font(Config.FontName, 8f), Padding = new Padding(6, 3, 6, 3), Location = new Point(PadX, AccGroup.HeadH + y), Tag = "tip" };
            palGroup.Controls.Add(listHint);
            y += listHint.Height + 6;
            var listGenLink = MakeLink(I18n.T("list_gen_link"), PadX, AccGroup.HeadH + y, "https://dlsdgj.github.io/hoverExplorers-cheme-generator/");
            palGroup.Controls.Add(listGenLink);
            y += listGenLink.Height + 8;
            // 头部提示块登记进行栈：宽度重排时重定位重测
            palGroup.AddBlock((by, w) =>
            {
                listHint.MaximumSize = new Size(Math.Max(160, w - PadX * 2), 0);
                listHint.Location = new Point(PadX, AccGroup.HeadH + by);
                listGenLink.Location = new Point(PadX, AccGroup.HeadH + by + listHint.Height + 6);
                return by + listHint.Height + 6 + listGenLink.Height + 8;
            });
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
            var schemeCombo = new DarkCombo { Font = new Font(Config.FontName, 9f), Location = new Point(12 + schemeLbl.PreferredWidth + 8, row1Y), Width = 130, Visible = false, WheelSwitch = true };
            palGroup.Controls.Add(schemeCombo);
            var btnDelScheme = mkBtn(I18n.T("del_scheme"));
            var btnSaveAs = mkBtn(I18n.T("save_as"));
            var btnPreset = mkBtn(I18n.T("warm_zebra"));
            var btnClear = mkBtn(I18n.T("clear"));
            var btnWallpaper = mkBtn(I18n.T("wallpaper_pick"));
            using (var bg = Graphics.FromHwnd(IntPtr.Zero)) { using (var f9 = new Font(Config.FontName, 9f)) { foreach (var b in new[]{btnDelScheme,btnSaveAs,btnPreset,btnClear,btnWallpaper}) b.Width = (int)bg.MeasureString(b.Text, f9).Width + 24; } }
            btnDelScheme.Location = new Point(schemeCombo.Right + 8, row1Y);
            btnSaveAs.Location = new Point(btnDelScheme.Right + 8, row1Y);
            palGroup.Controls.Add(btnDelScheme); palGroup.Controls.Add(btnSaveAs);
            int rowPresetY = row1Y + 32;
            btnPreset.Location = new Point(12, rowPresetY);
            btnClear.Location = new Point(btnPreset.Right + 8, rowPresetY);
            btnWallpaper.Location = new Point(btnClear.Right + 8, rowPresetY);
            palGroup.Controls.Add(btnPreset); palGroup.Controls.Add(btnClear); palGroup.Controls.Add(btnWallpaper);
            Tips.Set(btnWallpaper, I18n.T("wallpaper_pick_desc"));

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
                if (_listPreview != null) _listPreview.Invalidate();   // 配色变了同步重绘预览
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
            // 方案/预设按钮/可视化编辑器/AI 提示词手动块登记进行栈：宽度重排时整体重定位
            palGroup.AddBlock((by, w) =>
            {
                int r1 = AccGroup.HeadH + by;          // 行1：方案下拉
                int r1b = r1 + 32;                     // 行2：预设按钮
                int r2 = r1b + 32;                     // 行3：可视化编辑器
                int r3 = r2 + editorH + 8;             // 行4：AI 提示词
                schemeLbl.Location = new Point(12, r1 + 4);
                schemeCombo.Location = new Point(12 + schemeLbl.PreferredWidth + 8, r1);
                btnDelScheme.Location = new Point(schemeCombo.Right + 8, r1);
                btnSaveAs.Location = new Point(btnDelScheme.Right + 8, r1);
                btnPreset.Location = new Point(12, r1b);
                btnClear.Location = new Point(btnPreset.Right + 8, r1b);
                btnWallpaper.Location = new Point(btnClear.Right + 8, r1b);
                editorPanel.Location = new Point(12, r2);
                editorPanel.Width = Math.Max(200, w - 24);
                promptLbl.Location = new Point(12, r3 + 4);
                btnCopyPrompt.Location = new Point(promptLbl.Right + 12, r3);
                btnPasteImport.Location = new Point(btnCopyPrompt.Right + 8, r3);
                return by + 64 + editorH + 42;
            });

            // ==== 实时预览：模拟弹出列表面板（置于 AI 提示词下方、分区字体行之前）====
            int fy = row3Y - AccGroup.HeadH + 34;
            fy = palGroup.AddRow(fy, I18n.T("appearance_preview"), null, null);
            int pvH = 92;
            _listPreview = new ListPreviewPanel { Location = new Point(12, AccGroup.HeadH + fy), Size = new Size(palGroup.Width - 24, pvH), Tag = "paint", Visible = palGroup.Open };
            palGroup.Controls.Add(_listPreview);
            palGroup.TagLastRow(w => 46 + pvH + 8, ry =>
            {
                _listPreview.Location = new Point(12, AccGroup.HeadH + ry + 46);
                _listPreview.Width = Math.Max(200, palGroup.Width - 24);
            });
            palGroup.ExpandRow(fy + pvH + 8, 0);
            fy += pvH + 8;

            // ==== 分区字体 5 行（表头/标题/列表文字/格式标签/数量·大小信息）====
            // 每行右侧 = 字体族下拉 + 字号步进器；改动即存并刷新存活面板与预览
            string[] fKeys = { "header", "title", "item", "chip", "meta" };
            var fNames = new[] { Config.ListHeaderFontName, Config.ListTitleFontName, Config.ListItemFontName, Config.ListChipFontName, Config.ListMetaFontName };
            var fSizes = new[] { Config.ListHeaderFontSize, Config.ListTitleFontSize, Config.ListItemFontSize, Config.ListChipFontSize, Config.ListMetaFontSize };
            var famList = new List<string>();
            try { using (var ifc = new InstalledFontCollection()) foreach (var f in ifc.Families) famList.Add(f.Name); } catch { }
            Action<int> applyFontRow = delegate(int i)
            {
                switch (i)
                {
                    case 0: Config.ListHeaderFontName = fNames[i]; Config.ListHeaderFontSize = fSizes[i]; break;
                    case 1: Config.ListTitleFontName = fNames[i]; Config.ListTitleFontSize = fSizes[i]; break;
                    case 2: Config.ListItemFontName = fNames[i]; Config.ListItemFontSize = fSizes[i]; break;
                    case 3: Config.ListChipFontName = fNames[i]; Config.ListChipFontSize = fSizes[i]; break;
                    default: Config.ListMetaFontName = fNames[i]; Config.ListMetaFontSize = fSizes[i]; break;
                }
                Config.Save();
                Program.RefreshAllPanels();
                if (_listPreview != null) _listPreview.Invalidate();
            };
            fy = row3Y - AccGroup.HeadH + 34;   // 预览已在上方插入，此处仅复位（值相同）
            for (int i = 0; i < fKeys.Length; i++)
            {
                int idx = i;
                var combo = new DarkCombo { Font = new Font(Config.FontName, 8.5f), Width = 140, Height = 24 };
                foreach (var nm in famList) combo.Items.Add(nm);
                if (combo.Items.Contains(fNames[idx])) combo.SelectedItem = fNames[idx];
                else if (famList.Count > 0) { combo.Items.Add(fNames[idx]); combo.SelectedItem = fNames[idx]; }
                var step = new FontStepper { Value = Math.Max(6, Math.Min(24, (int)fSizes[idx])) };
                step.ValueChanged += (s, e) => { fSizes[idx] = step.Value; applyFontRow(idx); };
                combo.SelectedIndexChanged += (s, e) => { var nm = (string)combo.SelectedItem; if (!string.IsNullOrEmpty(nm)) { fNames[idx] = nm; applyFontRow(idx); } };
                fy = palGroup.AddRow(fy, I18n.T("font_" + fKeys[idx]), null, step);
                // 字体族下拉摆在字号步进器左侧（AddRow 只贴右一个控件）
                combo.Location = new Point(step.Left - 8 - combo.Width, step.Top + (step.Height - combo.Height) / 2);
                palGroup.TagLastRow(null, cy => combo.Location = new Point(step.Left - 8 - combo.Width, step.Top + (step.Height - combo.Height) / 2));
                palGroup.Controls.Add(combo);
                combo.Visible = palGroup.Open;
            }

            // 名称靠右（原"预览窗口"组）：行布局互换影响已开面板的按钮位置 → 应用时刷新存活面板
            _rowSwapToggle = new ToggleSwitch { On = Config.RowSwapSides };
            _rowSwapToggle.Toggled += (s, e) => _fnApplySettings(false, true);
            fy = palGroup.AddRow(fy, I18n.T("row_swap"), I18n.T("row_swap_desc"), _rowSwapToggle);

            // 窗口边角（原"预览窗口"组）：直角（卡片风默认）/ 圆角·粗描边（Win11）；切换即时重刷存活窗口
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
            fy = palGroup.AddRow(fy, I18n.T("corner_row"), I18n.T("corner_desc"), _cornerCombo);

            // （实时预览已上移至 AI 提示词下方）

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
            btnClear.Click += (s, e) => { Config.ListPalette = ""; Config.ApplyListPalette(); Config.Save(); fillEditor(""); Program.RefreshAllPanels(); if (_listPreview != null) _listPreview.Invalidate(); };
            btnWallpaper.Click += (s, e) =>
            {
                string wpPath = null;
                try
                {
                    using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
                        if (key != null) wpPath = key.GetValue("WallPaper") as string;
                    if (string.IsNullOrEmpty(wpPath) || !File.Exists(wpPath))
                        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers"))
                            if (key != null) wpPath = key.GetValue("BackgroundHistoryPath0") as string;
                }
                catch { }
                if (string.IsNullOrEmpty(wpPath) || !File.Exists(wpPath))
                {
                    MessageBox.Show(this, I18n.T("wallpaper_pick_desc"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Color baseCol;
                try
                {
                    using (var img = Image.FromFile(wpPath))
                    using (var thumb = new Bitmap(48, 27))
                    using (var g = Graphics.FromImage(thumb))
                    {
                        g.DrawImage(img, 0, 0, 48, 27);
                        long rr = 0, gg = 0, bb = 0;
                        for (int yy = 0; yy < 27; yy++)
                            for (int xx = 0; xx < 48; xx++)
                            {
                                var px = thumb.GetPixel(xx, yy);
                                rr += px.R; gg += px.G; bb += px.B;
                            }
                        baseCol = Color.FromArgb((int)(rr / (48 * 27)), (int)(gg / (48 * 27)), (int)(bb / (48 * 27)));
                    }
                }
                catch
                {
                    MessageBox.Show(this, I18n.T("wallpaper_pick_desc"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Func<Color, int, Color> adj = (c, d) => Color.FromArgb(Math.Max(0, Math.Min(255, c.R + d)), Math.Max(0, Math.Min(255, c.G + d)), Math.Max(0, Math.Min(255, c.B + d)));
                Func<Color, Color, double, Color> mix = (a, c, t) => Color.FromArgb((int)(a.R * (1 - t) + c.R * t), (int)(a.G * (1 - t) + c.G * t), (int)(a.B * (1 - t) + c.B * t));
                Func<Color, string> hex = c => c.R.ToString("x2") + c.G.ToString("x2") + c.B.ToString("x2");
                Func<float, float, float, Color> fromHSB = (h, sat, bri) =>
                {
                    if (sat == 0f) return Color.FromArgb((int)(bri * 255), (int)(bri * 255), (int)(bri * 255));
                    float v = bri;
                    float ch = v * sat;
                    float x = ch * (1f - Math.Abs((h / 60f) % 2f - 1f));
                    float m = v - ch;
                    float r, g, b;
                    if (h < 60f) { r = ch; g = x; b = 0f; }
                    else if (h < 120f) { r = x; g = ch; b = 0f; }
                    else if (h < 180f) { r = 0f; g = ch; b = x; }
                    else if (h < 240f) { r = 0f; g = x; b = ch; }
                    else if (h < 300f) { r = x; g = 0f; b = ch; }
                    else { r = ch; g = 0f; b = x; }
                    return Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
                };
                double lum = baseCol.R * 0.299 + baseCol.G * 0.587 + baseCol.B * 0.114;
                bool light = lum > 128;
                Color pBg = baseCol;
                Color pZb = adj(baseCol, light ? -8 : 8);
                Color pFg = light ? Color.FromArgb(0x3b, 0x2f, 0x25) : Color.FromArgb(0xf0, 0xea, 0xdc);
                Color pMu = mix(pFg, pBg, 0.55);
                Color pHv = adj(baseCol, light ? -14 : 14);
                Color pSel = fromHSB(baseCol.GetHue(), Math.Min(1f, baseCol.GetSaturation() + 0.3f), light ? 0.4f : 0.6f);
                Color pSelFg = Color.White;
                Color pBar = adj(baseCol, light ? -22 : 22);
                Color pDrop = Color.FromArgb(0x2d, 0x7a, 0x3d);
                Color pBd = mix(pBg, pFg, 0.3);
                Color pSb = mix(pBg, pFg, 0.45);
                Color pHdBg = adj(baseCol, light ? -5 : 5);
                Color pBtnHv = adj(baseCol, light ? -18 : 18);
                string pal = "bg=" + hex(pBg) + "\nzb=" + hex(pZb) + "\nfg=" + hex(pFg) + "\nmu=" + hex(pMu) +
                    "\nhv=" + hex(pHv) + "\nsel=" + hex(pSel) + "\nselfg=" + hex(pSelFg) + "\nbar=" + hex(pBar) +
                    "\ndrop=" + hex(pDrop) + "\nac=" + hex(pSel) + "\nacfg=" + hex(pSelFg) + "\nbd=" + hex(pBd) +
                    "\nsb=" + hex(pSb) + "\nhdbg=" + hex(pHdBg) + "\nbtnhv=" + hex(pBtnHv) + "\ncanvas=" + hex(pBg);
                Config.ListPalette = pal;
                Config.ApplyListPalette();
                Config.Save();
                fillEditor(pal);
                Program.RefreshAllPanels();
                if (_listPreview != null) _listPreview.Invalidate();
            };
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
            palGroup.ExpandRow(Math.Max(row3Y - AccGroup.HeadH + 34, fy), 0);
            palGroup.FinishRows();


            // ==== 组：桌面卡片·触发（page 3：桌面卡片）====
            _cardTriggerGroup = _fnNewGroup(I18n.T("group_card"), true, 3);
            _cardToggle = new ToggleSwitch { On = Config.CardHotkeyEnabled };
            _cardToggle.Toggled += (s, e) => { _fnUpdateCardState(); _fnApplySettings(true, false); };
            _cardTriggerGroup.AddChip(HotkeyDisplay(Config.CardHotkeyEnabled, _cardMods, _cardKey));

            y = 8;
            _cardPill = new HotkeyPill();
            _cardPill.SetHotkey(_cardMods, _cardKey);
            _cardPill.RecordClick += (s, e) => _fnStartCardCapture();
            y = _fnMakeHotkeyRow(_cardTriggerGroup, y, I18n.T("hotkey_row"), I18n.T("card_hotkey_desc"),
                _cardToggle, _cardPill, _fnStartCardCapture,
                () => { _cardPill.SetHotkey(0, 0); _cardMods = 0; _cardKey = 0; _fnApplySettings(true, false); });
            _cardTriggerGroup.FinishRows();

            // ==== 组：桌面卡片·外观（page 5）====
            _cardGroup = _fnNewGroup(I18n.T("group_card"), true, 3);
            y = 8;
            // 在线卡片配色生成器链接（标题下方）
            var cardGenLink = MakeLink(I18n.T("card_gen_link"), PadX, AccGroup.HeadH + y, "https://dlsdgj.github.io/hoverexplorer-card-palette/");
            _cardGroup.Controls.Add(cardGenLink);
            y += cardGenLink.Height + 8;
            _cardGroup.AddBlock((by, w) =>
            {
                cardGenLink.Location = new Point(PadX, AccGroup.HeadH + by);
                return by + cardGenLink.Height + 8;
            });

            // ---- 卡片配色编辑器（方案下拉 + 色块编辑 + 字体 + 导入，参考列表配色）----
            string[] cpKeys = { "bg", "fg", "badge_bg", "badge_fg", "strip" };
            string[] cpLabels = Array.ConvertAll(cpKeys, k => I18n.T("cp_lbl_" + k));
            int cpRow1Y = AccGroup.HeadH + y;
            var cpSchemeLbl = new Label { Text = I18n.T("palette_scheme") + ":", Font = new Font(Config.FontName, 9f), ForeColor = Config.FG, Location = new Point(12, cpRow1Y + 4), AutoSize = true, Visible = false };
            _cardGroup.Controls.Add(cpSchemeLbl);
            var cpSchemeCombo = new DarkCombo { Font = new Font(Config.FontName, 9f), Location = new Point(12 + cpSchemeLbl.PreferredWidth + 8, cpRow1Y), Width = 130, Visible = false, WheelSwitch = true };
            _cardGroup.Controls.Add(cpSchemeCombo);
            Func<string, Button> cpBtn = delegate(string text) { var b = new Button { Text = text, Font = new Font(Config.FontName, 9f), FlatStyle = FlatStyle.Flat, BackColor = Config.BG, ForeColor = Config.FG, Height = 26, Visible = false }; b.FlatAppearance.BorderColor = Config.Border; return b; };
            Action cpApply = null;
            Action cpLive = null;          // 实时应用：写回当前方案 + 刷新卡片 + 重绘预览
            bool cpFillBusy = false;       // cpFill 程序化赋值期间抑制实时应用
            var cpBtnDel = cpBtn(I18n.T("del_scheme")); var cpBtnSaveAs = cpBtn(I18n.T("save_as"));
            var cpBtnImport = cpBtn(I18n.T("paste_import"));
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
                blk.Click += (s, e) => { using (var cd = new ColorDialog()) { cd.Color = Color.FromArgb(255, cpBlk[idx].BackColor); if (cd.ShowDialog() == DialogResult.OK) { var old = cpBlk[idx].BackColor; var nc = Color.FromArgb(old.A, cd.Color.R, cd.Color.G, cd.Color.B); cpBlk[idx].BackColor = nc; cpHex[idx].Text = CardPalette.ToRgba(nc); if (cpLive != null) cpLive(); } } };
                blk.MouseDown += (s, e) => { if (e.Button == MouseButtons.Right) { using (var af = new Form { Text = "Alpha 0-255", Width = 200, Height = 110, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false }) { var nud = new NumericUpDown { Location = new Point(20, 15), Width = 80, Minimum = 0, Maximum = 255, Value = cpBlk[idx].BackColor.A }; var ok = new Button { Text = "OK", Location = new Point(110, 13), Width = 60, Height = 26, DialogResult = DialogResult.OK }; af.Controls.Add(nud); af.Controls.Add(ok); if (af.ShowDialog(this) == DialogResult.OK) { var old = cpBlk[idx].BackColor; var nc = Color.FromArgb((int)nud.Value, old.R, old.G, old.B); cpBlk[idx].BackColor = nc; cpHex[idx].Text = CardPalette.ToRgba(nc); if (cpLive != null) cpLive(); } } } };
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
            var cpPromptText = I18n.T("card_palette_prompt_text");
            cpBtnCopyPrompt.Click += (s, e) => { try { Clipboard.SetText(cpPromptText); } catch { } };
            // 填充编辑器
            Action<CardPalette> cpFill = delegate(CardPalette p) {
                if (p == null) return;
                cpFillBusy = true;
                var vals = new Dictionary<string, Color> { {"bg",p.Bg},{"fg",p.Fg},{"badge_bg",p.BadgeBg},{"badge_fg",p.BadgeFg},{"strip",p.StripColor} };
                for (int i = 0; i < cpKeys.Length; i++) { Color c; vals.TryGetValue(cpKeys[i], out c); cpBlk[i].BackColor = c; cpHex[i].Text = CardPalette.ToRgba(c); }
                cpStripW.Value = Math.Max(0, Math.Min(10, p.StripWidth));
                if (cpFontCombo.Items.Contains(p.FontName)) cpFontCombo.SelectedItem = p.FontName; else { cpFontCombo.Items.Add(p.FontName); cpFontCombo.SelectedItem = p.FontName; }
                cpFontSize.Value = (decimal)Math.Max(6, Math.Min(24, p.FontSize));
                cpRadius.Value = Math.Max(0, Math.Min(12, p.Radius));
                cpFillBusy = false;
                if (_cardPreview != null) _cardPreview.Invalidate();   // 方案变了同步重绘预览
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
            // 实时应用：写回当前方案 + 刷新全部桌面卡片 + 重绘预览
            cpLive = delegate() { cpApply(); Program.RefreshAllCards(); if (_cardPreview != null) _cardPreview.Invalidate(); };
            for (int i = 0; i < cpKeys.Length; i++) { int idx = i; cpHex[i].Leave += (s, e) => { var txt = cpHex[idx].Text.Trim(); Color nc; if (txt.StartsWith("rgba", StringComparison.OrdinalIgnoreCase) || txt.StartsWith("rgb", StringComparison.OrdinalIgnoreCase)) nc = CardPalette.Hx(txt); else { var h = txt.TrimStart('#'); if (System.Text.RegularExpressions.Regex.IsMatch(h, "^[0-9a-fA-F]{6}$|^[0-9a-fA-F]{8}$")) nc = CardPalette.Hx(h); else return; } cpBlk[idx].BackColor = nc; if (!cpFillBusy && cpLive != null) cpLive(); }; }
            cpStripW.ValueChanged += (s, e) => { if (!cpFillBusy && cpLive != null) cpLive(); };
            cpFontCombo.SelectedIndexChanged += (s, e) => { if (!cpFillBusy && cpLive != null) cpLive(); };
            cpFontSize.ValueChanged += (s, e) => { if (!cpFillBusy && cpLive != null) cpLive(); };
            cpRadius.ValueChanged += (s, e) => { if (!cpFillBusy && cpLive != null) cpLive(); };
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
            cpRefreshSchemes(); cpFill(CardPalettes.Current);

            _cardGroup.ExpandRow(cpBtnImport.Bottom - AccGroup.HeadH + 4, 0);
            // 卡片配色编辑器手动块登记进行栈：宽度重排时整体重定位（行1 方案/行2 编辑器/行3 提示词）
            _cardGroup.AddBlock((by, w) =>
            {
                int r1 = AccGroup.HeadH + by;
                int r2 = r1 + 32;
                int r3 = r2 + cpEditor.Height + 4;
                cpSchemeLbl.Location = new Point(12, r1 + 4);
                cpSchemeCombo.Location = new Point(12 + cpSchemeLbl.PreferredWidth + 8, r1);
                cpBtnDel.Location = new Point(cpSchemeCombo.Right + 8, r1);
                cpBtnSaveAs.Location = new Point(cpBtnDel.Right + 8, r1);
                cpEditor.Location = new Point(12, r2);
                cpEditor.Width = Math.Max(200, w - 24);
                cpPromptLbl.Location = new Point(12, r3 + 4);
                cpBtnCopyPrompt.Location = new Point(cpPromptLbl.Right + 8, r3);
                cpBtnImport.Location = new Point(cpBtnCopyPrompt.Right + 8, r3);
                _cardGroup.NoticeContentBottom(by + 32 + cpEditor.Height + 34);
                return by + 32 + cpEditor.Height + 40;   // 链尾推进到 cardHint 的 y
            });

            // 滚轮切换配色说明（黄底黑字常显）
            int cardHintY = cpBtnImport.Bottom - AccGroup.HeadH + 10;
            var cardHint = new Label { Text = I18n.T("hint_card_wheel"), AutoSize = true, MaximumSize = new Size(_cardGroup.Width - PadX * 2, 0), BackColor = Color.FromArgb(0xFF, 0xF5, 0x9C), ForeColor = Color.Black, Font = new Font(Config.FontName, 8f), Padding = new Padding(6, 3, 6, 3), Location = new Point(PadX, AccGroup.HeadH + cardHintY), Tag = "tip" };
            _cardGroup.Controls.Add(cardHint);
            _cardGroup.ExpandRow(cardHintY + cardHint.Height + 4, 0);
            _cardGroup.AddBlock((by, w) =>
            {
                cardHint.MaximumSize = new Size(Math.Max(160, w - PadX * 2), 0);
                cardHint.Location = new Point(PadX, AccGroup.HeadH + by);
                _cardGroup.NoticeContentBottom(by + cardHint.Height + 4);
                return by + cardHint.Height + 10;
            });

            // 实时预览：按当前卡片配色/圆角/色条模拟一张桌面卡片（编辑应用/切方案后 Invalidate 重绘）
            int cpvY = cardHintY + cardHint.Height + 10;
            cpvY = _cardGroup.AddRow(cpvY, I18n.T("card_preview"), null, null);
            int cpvH = 48;
            _cardPreview = new CardPreviewPanel { Location = new Point(12, AccGroup.HeadH + cpvY), Size = new Size(_cardGroup.Width - 24, cpvH), Tag = "paint", Visible = _cardGroup.Open };
            _cardGroup.Controls.Add(_cardPreview);
            _cardGroup.ExpandRow(cpvY + cpvH + 8, 0);
            _cardGroup.AddTailHook(ty =>
            {
                _cardPreview.Location = new Point(12, AccGroup.HeadH + ty);
                _cardPreview.Width = Math.Max(200, _cardGroup.Width - 24);
                _cardGroup.NoticeContentBottom(ty + cpvH + 8);
            });

            _cardGroup.FinishRows();


            // ==== 组 3：历史·收藏（page 4）====
            _entryGroup = _fnNewGroup(I18n.T("group_entry"), true, 4);
            _entryToggle = new ToggleSwitch { On = Config.EntryHotkeyEnabled };
            _entryToggle.Toggled += (s, e) => { _fnUpdateEntryState(); _fnApplySettings(true, false); };
            _entryGroup.AddChip(HotkeyDisplay(Config.EntryHotkeyEnabled, _entryMods, _entryKey));
            _entryGroup.AddChip("");   // 区域尺寸 chip（_fnUpdateAreaLbl 填充）

            y = 8;
            _entryPill = new HotkeyPill();
            _entryPill.SetHotkey(_entryMods, _entryKey);
            _entryPill.RecordClick += (s, e) => _fnStartEntryCapture();
            y = _fnMakeHotkeyRow(_entryGroup, y, I18n.T("entry_hotkey"), null,
                _entryToggle, _entryPill, _fnStartEntryCapture,
                () => { _entryPill.SetHotkey(0, 0); _entryMods = 0; _entryKey = 0; _fnApplySettings(true, false); });

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
            // 触发区域行内的手动控件（说明标签+框选/清除/色块按钮）登记重排，行高随标签换行重算
            _entryGroup.TagLastRow(w2 =>
            {
                int mw = Math.Max(160, w2 - AccGroup.InL - 200);
                _areaLbl.MaximumSize = new Size(mw, 0);
                int lh = TextRenderer.MeasureText(_areaLbl.Text, _areaLbl.Font, new Size(mw, 10000),
                    TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height;
                return Math.Max(RowH, 19 + lh + 8);
            }, ay =>
            {
                _areaLbl.Location = new Point(AccGroup.InL, AccGroup.HeadH + ay + 19);
                _areaPickBtn.Location = new Point(_entryGroup.Width - AccGroup.InR - _areaPickBtn.Width, AccGroup.HeadH + ay);
                _areaClearBtn.Location = new Point(_areaPickBtn.Left - _areaClearBtn.Width - 8, AccGroup.HeadH + ay);
                _areaColorBlock.Location = new Point(_areaClearBtn.Left - _areaColorBlock.Width - 8, AccGroup.HeadH + ay);
            });

            // 触发区域驻留延时：紧跟触发区域行，与暂停触控区的延时相互独立
            _entryDelayStepper = new MsStepper { Value = Config.EntryRectDelayMs };
            _entryDelayStepper.ValueChanged += (s, e) => _fnApplySettings(false, false);
            y = _entryGroup.AddRow(y, I18n.T("entry_delay_row"), I18n.T("entry_delay_desc"), _entryDelayStepper);

            // 收藏列表功能说明（黄底黑字常显）
            int favHintY = y + 6;
            var favHint = new Label { Text = I18n.T("hint_fav_features"), AutoSize = true, MaximumSize = new Size(_entryGroup.Width - PadX * 2, 0), BackColor = Color.FromArgb(0xFF, 0xF5, 0x9C), ForeColor = Color.Black, Font = new Font(Config.FontName, 8f), Padding = new Padding(6, 3, 6, 3), Location = new Point(PadX, AccGroup.HeadH + favHintY), Tag = "tip" };
            _entryGroup.Controls.Add(favHint);
            _entryGroup.ExpandRow(favHintY + favHint.Height + 4, 0);
            _entryGroup.AddTailHook(ty =>
            {
                favHint.MaximumSize = new Size(Math.Max(160, _entryGroup.Width - PadX * 2), 0);
                favHint.Location = new Point(PadX, AccGroup.HeadH + ty + 6);
                _entryGroup.NoticeContentBottom(ty + 6 + favHint.Height + 4);
            });

            _entryGroup.FinishRows();


            // （原"组 4：排序"已并入 page 0 的"行为"组）


            // ==== 组 5：Total Commander 支持（暂停开发）====
            _tcGroup = _fnNewGroup(I18n.T("group_advanced"), true, 5);
            _tcGroup.AddChip(I18n.T("tag_paused"), true);

            _tcSortCombo = new DarkCombo { Font = new Font(Config.FontName, 8.5f), Enabled = false, Width = 170, Height = 24 };
            _tcSortCombo.Items.AddRange(new object[] { "自动 (读 TC ini)", "名称 ↑", "名称 ↓", "日期 ↑", "日期 ↓", "大小 ↑", "大小 ↓", "扩展名 ↑", "扩展名 ↓" });
            _tcSortCombo.SelectedIndex = Config.TcSortMode + 1;

            y = 8;
            y = _tcGroup.AddRow(y, I18n.T("tc_sort_row"), I18n.T("tc_sort_disabled"), _tcSortCombo);
            // Obsidian/Perch 联动说明
            var tcHint = new Label { Text = I18n.T("hint_integration"), AutoSize = true, MaximumSize = new Size(_tcGroup.Width - PadX * 2, 0), BackColor = Color.FromArgb(0xFF, 0xF5, 0x9C), ForeColor = Color.Black, Font = new Font(Config.FontName, 8f), Padding = new Padding(6, 3, 6, 3), Location = new Point(PadX, AccGroup.HeadH + y + 6), Tag = "tip" };
            _tcGroup.Controls.Add(tcHint);
            var obsLink = MakeLink(I18n.T("hidden_features_obsidian_link"), PadX, tcHint.Bottom + 6, "https://github.com/dlsdgj/hover-link-preview");
            _tcGroup.Controls.Add(obsLink);
            var perchLink2 = MakeLink(I18n.T("hidden_features_perch_link"), PadX, obsLink.Bottom + 4, "https://github.com/dlsdgj/perch");
            _tcGroup.Controls.Add(perchLink2);
            _tcGroup.ExpandRow(y + 6 + tcHint.Height + 6 + obsLink.Height + 4 + perchLink2.Height + 4, 0);
            _tcGroup.AddTailHook(ty =>
            {
                tcHint.MaximumSize = new Size(Math.Max(160, _tcGroup.Width - PadX * 2), 0);
                tcHint.Location = new Point(PadX, AccGroup.HeadH + ty + 6);
                obsLink.Location = new Point(PadX, tcHint.Bottom + 6);
                perchLink2.Location = new Point(PadX, obsLink.Bottom + 4);
                _tcGroup.NoticeContentBottom(ty + 6 + tcHint.Height + 6 + obsLink.Height + 4 + perchLink2.Height + 4);
            });
            _tcGroup.FinishRows();

            // ---- "托盘管理"折叠组 ----
            var trayGroup = _fnNewGroup(I18n.T("group_tray"), true, 6);
            y = 8;
            _trayIconBox = new TextBox
            {
                Text = Config.TrayIconPath,
                Font = new Font("Consolas", 8.5f),
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Config.BG,
                ForeColor = Config.FG,
                Size = new Size(150, 22),
            };
            var trayBrowseBtn = new Button
            {
                Text = I18n.T("tray_browse"),
                FlatStyle = FlatStyle.Flat,
                BackColor = Config.BG,
                ForeColor = Config.FG,
                Font = new Font(Config.FontName, 8.5f),
                Size = new Size(48, 22),
                Cursor = Cursors.Hand,
            };
            trayBrowseBtn.FlatAppearance.BorderColor = Config.Border;
            var trayDefBtn = new Button
            {
                Text = I18n.T("tray_default"),
                FlatStyle = FlatStyle.Flat,
                BackColor = Config.BG,
                ForeColor = Config.FG,
                Font = new Font(Config.FontName, 8.5f),
                Size = new Size(48, 22),
                Cursor = Cursors.Hand,
            };
            trayDefBtn.FlatAppearance.BorderColor = Config.Border;
            var trayIconRow = new Panel { Size = new Size(206, 24), BackColor = Config.BG };
            _trayIconBox.Location = new Point(0, 1);
            trayBrowseBtn.Location = new Point(156, 0);
            trayIconRow.Controls.Add(_trayIconBox);
            trayIconRow.Controls.Add(trayBrowseBtn);
            y = trayGroup.AddRow(y, I18n.T("tray_icon_path"), I18n.T("tray_icon_desc"), trayIconRow);
            trayBrowseBtn.Click += (s, e) =>
            {
                using (var ofd = new OpenFileDialog { Filter = "ICO (*.ico)|*.ico", Title = I18n.T("tray_icon_path") })
                {
                    if (ofd.ShowDialog() == DialogResult.OK)
                    {
                        _trayIconBox.Text = ofd.FileName;
                        _fnApplyTrayIcon();
                    }
                }
            };
            trayDefBtn.Click += (s, e) => { _trayIconBox.Text = ""; _fnApplyTrayIcon(); };
            _trayIconBox.LostFocus += (s, e) => _fnApplyTrayIcon();
            _trayIconBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { _trayIconBox.Text = _trayIconBox.Text.Trim(); _fnApplyTrayIcon(); } };

            _trayMiddleCombo = new DarkCombo { Font = new Font(Config.FontName, 8.5f), Width = 130, Height = 24 };
            _trayMiddleCombo.SetItems(new object[] { I18n.T("tray_middle_none"), I18n.T("tray_middle_settings"), I18n.T("tray_middle_restart"), I18n.T("tray_middle_exit") }, Math.Max(0, Math.Min(3, Config.TrayMiddleAction)));
            _trayMiddleCombo.SelectedIndexChanged += (s, e) => { Config.TrayMiddleAction = _trayMiddleCombo.SelectedIndex; Config.Save(); };
            y = trayGroup.AddRow(y, I18n.T("tray_middle_click"), I18n.T("tray_middle_hint"), _trayMiddleCombo);

            y = trayGroup.AddRow(y, I18n.T("tray_menu_items"), null, null);

            int panelW = 130;
            int showX = 6, enableX = 80;
            int listRowH = 28;
            int contentRight = trayGroup.Width - AccGroup.InR;
            int panelLeft = contentRight - panelW;
            var trayPlacers = new List<Action<int, int>>();   // 表头+列表行的重排回调：(行 y, 行面板左 x)

            {
                int hy = AccGroup.HeadH + y;
                var lItem = new Label { Text = I18n.T("tray_col_item"), AutoSize = true, Font = new Font(Config.FontName, 8f), ForeColor = Config.PlaceholderFg, Location = new Point(AccGroup.InL, hy + 6) };
                var lShow = new Label { Text = I18n.T("tray_col_show"), AutoSize = true, Font = new Font(Config.FontName, 8f), ForeColor = Config.PlaceholderFg };
                lShow.Location = new Point(panelLeft + showX + 22 - lShow.PreferredWidth / 2, hy + 6);
                var lEnable = new Label { Text = I18n.T("tray_col_enable"), AutoSize = true, Font = new Font(Config.FontName, 8f), ForeColor = Config.PlaceholderFg };
                lEnable.Location = new Point(panelLeft + enableX + 22 - lEnable.PreferredWidth / 2, hy + 6);
                trayGroup.Controls.Add(lItem);
                trayGroup.Controls.Add(lShow);
                trayGroup.Controls.Add(lEnable);
                trayPlacers.Add((py, pl) =>
                {
                    lItem.Location = new Point(AccGroup.InL, AccGroup.HeadH + py + 6);
                    lShow.Location = new Point(pl + showX + 22 - lShow.PreferredWidth / 2, AccGroup.HeadH + py + 6);
                    lEnable.Location = new Point(pl + enableX + 22 - lEnable.PreferredWidth / 2, AccGroup.HeadH + py + 6);
                });
                y += listRowH;
                trayGroup.ExpandRow(y, 0);
            }

            Action<string, ToggleSwitch, ToggleSwitch, string> addListRow = (name, showT, enableT, desc) =>
            {
                int ry = AccGroup.HeadH + y;
                var lName = new Label { Text = name, AutoSize = true, Font = new Font(Config.FontName, 9f), Location = new Point(AccGroup.InL, ry + 3) };
                // 行说明：灰色小字紧跟名称之后（如"隐藏提示 → 系统tooltip"）
                Label lDesc = null;
                if (!string.IsNullOrEmpty(desc))
                {
                    lDesc = new Label { Text = desc, AutoSize = true, Font = new Font(Config.FontName, 8f), ForeColor = Config.PlaceholderFg, Location = new Point(AccGroup.InL + lName.PreferredWidth + 8, ry + 5) };
                    trayGroup.Controls.Add(lDesc);
                }
                var rowPanel = new Panel { Size = new Size(panelW, 22), Location = new Point(panelLeft, ry), BackColor = Config.BG };
                showT.Location = new Point(showX, 0);
                rowPanel.Controls.Add(showT);
                if (enableT != null) { enableT.Location = new Point(enableX, 0); rowPanel.Controls.Add(enableT); }
                trayGroup.Controls.Add(lName);
                trayGroup.Controls.Add(rowPanel);
                trayPlacers.Add((py, pl) =>
                {
                    lName.Location = new Point(AccGroup.InL, AccGroup.HeadH + py + 3);
                    if (lDesc != null) lDesc.Location = new Point(AccGroup.InL + lName.PreferredWidth + 8, AccGroup.HeadH + py + 5);
                    rowPanel.Location = new Point(pl, AccGroup.HeadH + py);
                });
                y += listRowH;
                trayGroup.ExpandRow(y, 0);
            };

            _trayShowExplorerToggle = new ToggleSwitch { On = Config.TrayShowExplorer };
            _trayShowExplorerToggle.Toggled += (s, e) => { Config.TrayShowExplorer = _trayShowExplorerToggle.On; Config.Save(); Program.RebuildTrayMenu(); };
            var enableExplorer = new ToggleSwitch { On = Config.ExplorerEnabled };
            enableExplorer.Toggled += (s, e) => { Config.ExplorerEnabled = enableExplorer.On; Config.Save(); Program.RebuildTrayMenu(); };
            addListRow(I18n.T("tray_show_explorer"), _trayShowExplorerToggle, enableExplorer, null);

            _trayShowPreviewToggle = new ToggleSwitch { On = Config.TrayShowPreview };
            _trayShowPreviewToggle.Toggled += (s, e) => { Config.TrayShowPreview = _trayShowPreviewToggle.On; Config.Save(); Program.RebuildTrayMenu(); };
            var enablePreview = new ToggleSwitch { On = Config.PreviewFiles };
            enablePreview.Toggled += (s, e) => { Config.PreviewFiles = enablePreview.On; Config.Save(); Program.RebuildTrayMenu(); };
            addListRow(I18n.T("tray_show_preview"), _trayShowPreviewToggle, enablePreview, null);

            _trayShowOrigToggle = new ToggleSwitch { On = Config.TrayShowOrigSize };
            _trayShowOrigToggle.Toggled += (s, e) => { Config.TrayShowOrigSize = _trayShowOrigToggle.On; Config.Save(); Program.RebuildTrayMenu(); };
            var enableOrig = new ToggleSwitch { On = Config.PreviewOriginalSize };
            enableOrig.Toggled += (s, e) => { Config.PreviewOriginalSize = enableOrig.On; Config.Save(); Program.RebuildTrayMenu(); };
            addListRow(I18n.T("tray_show_orig"), _trayShowOrigToggle, enableOrig, null);

            _trayShowDebugLogToggle = new ToggleSwitch { On = Config.TrayShowDebugLog };
            _trayShowDebugLogToggle.Toggled += (s, e) => { Config.TrayShowDebugLog = _trayShowDebugLogToggle.On; Config.Save(); Program.RebuildTrayMenu(); };
            var enableDebugLog = new ToggleSwitch { On = Config.DebugLog };
            enableDebugLog.Toggled += (s, e) => { Config.DebugLog = enableDebugLog.On; Config.Save(); Program.RebuildTrayMenu(); };
            addListRow(I18n.T("tray_show_debuglog"), _trayShowDebugLogToggle, enableDebugLog, null);

            _trayShowHideTooltipToggle = new ToggleSwitch { On = Config.TrayShowHideTooltip };
            _trayShowHideTooltipToggle.Toggled += (s, e) => { Config.TrayShowHideTooltip = _trayShowHideTooltipToggle.On; Config.Save(); Program.RebuildTrayMenu(); };
            var enableHideTooltip = new ToggleSwitch { On = Config.HideTooltip };
            enableHideTooltip.Toggled += (s, e) => { Config.HideTooltip = enableHideTooltip.On; Config.Save(); Program.RebuildTrayMenu(); };
            addListRow(I18n.T("tray_show_hidetooltip"), _trayShowHideTooltipToggle, enableHideTooltip, I18n.T("tray_hidetooltip_desc"));

            _trayShowButtonTipsToggle = new ToggleSwitch { On = Config.TrayShowButtonTips };
            _trayShowButtonTipsToggle.Toggled += (s, e) => { Config.TrayShowButtonTips = _trayShowButtonTipsToggle.On; Config.Save(); Program.RebuildTrayMenu(); };
            var enableButtonTips = new ToggleSwitch { On = Config.ShowTips };
            enableButtonTips.Toggled += (s, e) => { Config.ShowTips = enableButtonTips.On; Config.Save(); Program.RebuildTrayMenu(); Tips.ApplyConfig(); Program.RefreshAllPanels(); };
            addListRow(I18n.T("tray_show_buttontips"), _trayShowButtonTipsToggle, enableButtonTips, null);

            _trayShowAutostartToggle = new ToggleSwitch { On = Config.TrayShowAutostart };
            _trayShowAutostartToggle.Toggled += (s, e) => { Config.TrayShowAutostart = _trayShowAutostartToggle.On; Config.Save(); Program.RebuildTrayMenu(); };
            var enableAutostart = new ToggleSwitch { On = Program.IsAutoStart() };
            enableAutostart.Toggled += (s, e) => { Program.SetAutoStart(enableAutostart.On); Program.RebuildTrayMenu(); };
            addListRow(I18n.T("tray_show_autostart"), _trayShowAutostartToggle, enableAutostart, null);

            var showHideTray = new ToggleSwitch { On = Config.TrayShowHideTray };
            showHideTray.Toggled += (s, e) => { Config.TrayShowHideTray = showHideTray.On; Config.Save(); Program.RebuildTrayMenu(); };
            var enableHideTray = new ToggleSwitch { On = Config.HideTrayIcon };
            enableHideTray.Toggled += (s, e) => { Config.HideTrayIcon = enableHideTray.On; Config.Save(); Program.ApplyHideTrayIcon(); Program.RebuildTrayMenu(); };
            addListRow(I18n.T("tray_show_hidetray"), showHideTray, enableHideTray, null);

            var showAddCard = new ToggleSwitch { On = Config.TrayShowAddCard };
            showAddCard.Toggled += (s, e) => { Config.TrayShowAddCard = showAddCard.On; Config.Save(); Program.RebuildTrayMenu(); };
            addListRow(I18n.T("tray_show_addcard"), showAddCard, null, null);

            // 表头+列表行手动块登记进行栈：宽度重排时右列面板跟随右缘
            trayGroup.AddBlock((by, w) =>
            {
                int pl = w - AccGroup.InR - panelW;
                int ty2 = by;
                foreach (var p in trayPlacers) { p(ty2, pl); ty2 += listRowH; }
                return ty2;
            });

            trayGroup.FinishRows();

            // ---- "关于"折叠组（赞助/版本，随内容滚动）----
            EventHandler copyWx = (s, e) =>
            {
                try { Clipboard.SetText("jtugqivi"); MessageBox.Show(I18n.T("donated"), I18n.T("donate"), MessageBoxButtons.OK, MessageBoxIcon.Information); } catch { }
            };
            var aboutGroup = _fnNewGroup(I18n.T("group_about"), true, 7);
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

            _verLbl = new Label { Text = "v" + Program.Version, AutoSize = true, ForeColor = Config.PlaceholderFg, Font = new Font(Config.FontName, 8f), Tag = "sub", Visible = false };
            _verLbl.Location = new Point(aboutGroup.Width - PadX - 18 - _verLbl.PreferredWidth, aboutBase + qrSize - _verLbl.PreferredHeight);
            aboutGroup.Controls.Add(_verLbl);
            // 更新检测：查 GitHub latest release（逻辑已抽离到 Program.UpdateChecker，托盘绿点共用同一份结果），
            // 新版则在版本号左侧显示红点标识，点击打开 Releases
            _updateLbl = new LinkLabel { Text = "", AutoSize = true, Font = new Font(Config.FontName, 8f, FontStyle.Bold), Tag = "sub", Visible = false, LinkColor = Color.FromArgb(0xE0, 0x31, 0x31), ActiveLinkColor = Color.FromArgb(0xE0, 0x31, 0x31) };
            _updateLbl.LinkClicked += (s, e) => { try { System.Diagnostics.Process.Start("https://github.com/dlsdgj/HoverExplorer/releases"); } catch { } };
            aboutGroup.Controls.Add(_updateLbl);
            UpdateChecker.Check(latest =>
            {
                if (IsDisposed || _updateLbl == null || _verLbl == null) return;
                if (UpdateChecker.Newer(latest))
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
            // 宽度重排：正文宽度重测换行、链接与说明随之前移，版本号/更新红点跟随右缘
            aboutGroup.AddTailHook(_ =>
            {
                int aw = aboutGroup.Width;
                mp3Body.Width = Math.Max(200, aw - PadX * 2);
                mp3Body.Height = Math.Max(48, (int)Math.Ceiling((double)TextRenderer.MeasureText(mp3Body.Text, mp3Body.Font, mp3Body.Size, TextFormatFlags.WordBreak).Height / 12.0) * 12 + 4);
                fixerLink.Location = new Point(PadX, mp3Body.Bottom + 6);
                int fdY = fixerLink.Bottom + 14;
                ffDlTitle.Location = new Point(PadX, fdY);
                ffDlBody.Location = new Point(PadX, fdY + 20);
                ffDlBody.Width = Math.Max(200, aw - PadX * 2);
                ffDlBody.Height = Math.Max(48, (int)Math.Ceiling((double)TextRenderer.MeasureText(ffDlBody.Text, ffDlBody.Font, ffDlBody.Size, TextFormatFlags.WordBreak).Height / 12.0) * 12 + 4);
                ffDlLink.Location = new Point(PadX, ffDlBody.Bottom + 6);
                ffDlStatus.Location = new Point(ffDlLink.Right + 10, ffDlLink.Top + 2);
                int wdY2 = ffDlLink.Bottom + 14;
                wdTitle.Location = new Point(PadX, wdY2);
                wdBody.Location = new Point(PadX, wdY2 + 20);
                wdBody.Width = Math.Max(200, aw - PadX * 2);
                wdBody.Height = Math.Max(60, (int)Math.Ceiling((double)TextRenderer.MeasureText(wdBody.Text, wdBody.Font, wdBody.Size, TextFormatFlags.WordBreak).Height / 12.0) * 12 + 4);
                _verLbl.Location = new Point(aw - PadX - 18 - _verLbl.PreferredWidth, _verLbl.Top);
                if (!string.IsNullOrEmpty(_updateLbl.Text))
                    _updateLbl.Location = new Point(_verLbl.Left - 10 - _updateLbl.PreferredWidth, _verLbl.Top);
                aboutGroup.NoticeContentBottom(wdBody.Bottom + 20 - AccGroup.HeadH);
            });
            aboutGroup.ExpandRow(wdBody.Bottom + 20 - AccGroup.HeadH, 0);
            aboutGroup.FinishRows();
            if (aboutGroup.Open) { aboutGroup.Open = false; aboutGroup.Open = true; }

            _fnUpdateAreaLbl();
            _fnRefreshChips();
            _fnUpdateExpandBtn();
            { var p = Config.LastSettingsPage; if (p < 0 || p >= _navItems.Count) p = 0; _fnSwitchPage(p); }   // 恢复上次分页
            ApplyThemeColors();
        }


        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _fnSyncScroll();   // 布局稳定后再校一次内容高度（构造期 GetPreferredSize 可能偏小）
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Config.LastSettingsPage = _curPage < 0 ? 0 : _curPage;
            Config.SettingsW = Width;   // 尺寸持久化
            Config.SettingsH = Height;
            Config.Save();
            base.OnFormClosed(e);
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
            const int WM_NCHITTEST = 0x0084;
            if (m.Msg == WM_NCHITTEST && _fnNcHitTest(ref m)) return;   // 无边框窗口的边缘热区（拖拽调尺寸）
            if (Win11.WndNcBorder(Handle, ref m)) return;
            base.WndProc(ref m);
        }

        // 八向调整热区：右缘/底缘/右下角必支持，左右下角顺带支持；
        // 上缘不劫持（避免与关闭/展开按钮冲突），左右缘限定头部区以下。
        // 供窗体自身与盖住边缘的子容器（侧栏/视口/滚动层/分组/滚动条）共同调用
        internal bool _fnNcHitTest(ref Message m)
        {
            long lp = m.LParam.ToInt64();
            var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
            const int edge = 6;
            bool left = p.X <= edge, right = p.X >= Width - edge, bottom = p.Y >= Height - edge;
            if (!left && !right && !bottom) return false;
            if ((left || right) && p.Y < HeadAreaH) return false;
            if (left && bottom) { m.Result = (IntPtr)12; return true; }    // HTBOTTOMLEFT
            if (right && bottom) { m.Result = (IntPtr)17; return true; }   // HTBOTTOMRIGHT
            if (left) { m.Result = (IntPtr)10; return true; }              // HTLEFT
            if (right) { m.Result = (IntPtr)11; return true; }             // HTRIGHT
            m.Result = (IntPtr)15;                                         // HTBOTTOM
            return true;
        }

        // 子容器把 WM_NCLBUTTONDOWN(尺寸 HT 代码) 转交窗体：由窗体进入系统调整循环
        internal bool ForwardSizeDrag(ref Message m)
        {
            const int WM_NCLBUTTONDOWN = 0x00A1;
            int ht = (int)m.WParam;
            if (ht >= 10 && ht <= 17)   // HTLEFT..HTBOTTOMRIGHT
            {
                NativeMethods.SendMessage(Handle, WM_NCLBUTTONDOWN, ht, m.LParam.ToInt32());
                return true;
            }
            return false;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            _fnRelayoutWindow();
        }

        // 尺寸变化整体重排：侧栏/视口/滚动条拉伸，各组宽度重排（行栈重算），最后同步滚动范围
        private void _fnRelayoutWindow()
        {
            if (_scrollPanel == null || _viewport == null || _closeBtn == null) return;   // 构造期尺寸变化时布局尚未建立
            int viewH = Height - HeadAreaH;
            if (_sidebar != null) _sidebar.Size = new Size(SidebarW, viewH);
            _viewport.Size = new Size(Width - SidebarW, viewH);
            _closeBtn.Location = new Point(Width - 32, 8);
            _fnPlaceExpandBtn();
            _contentW = Width - SidebarW - PadX * 2 - ScrollBarW;
            foreach (var g in _groups) g.Relayout(_contentW);
            _fnSyncScroll();
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

        private AccGroup _fnNewGroup(string title, bool open, int pageIdx)
        {
            int contentW = _contentW;   // 主区内容宽度（扣除侧栏+滚动条）
            var g = new AccGroup(contentW, title, open);
            g.PageIdx = pageIdx;
            g.Margin = new Padding(0, 0, 0, 10);
            _scrollPanel.Controls.Add(g);
            _groups.Add(g);

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

        // 侧栏分页切换：显示该页的组，隐藏其他；重算滚动
        private void _fnSwitchPage(int idx)
        {
            if (idx == _curPage) return;
            _curPage = idx;
            foreach (var g in _groups) g.Visible = (g.PageIdx == idx);
            // 高亮当前导航项
            for (int i = 0; i < _navItems.Count; i++)
            {
                bool on = _navDisplayOrder[i] == idx;
                _navItems[i].BackColor = on ? Config.BG : Config.BgHover;
                _navItems[i].ForeColor = on ? Config.Accent : Config.FG;
                bool boldBase = Array.IndexOf(_boldNavPages, i) >= 0;
                _navItems[i].Font = on ? new Font(Config.FontName, 10f, FontStyle.Bold) : new Font(Config.FontName, 9f, boldBase ? FontStyle.Bold : FontStyle.Regular);
            }
            if (_scrollBar != null) _scrollBar.Value = 0;
            _fnSyncScroll();
        }

        // GitHub latest release 版本检测：已抽离到 Program.UpdateChecker（托盘绿点与设置窗红点共用同一份结果）

        // ================= 自绘滚动 =================

        // 组高度/视口变化后同步滚动范围（内容层高度补齐，避免 FlowLayoutPanel 裁掉溢出子控件）
        // 注意：GetPreferredSize(Size(w,0)) 高度约束 0 会返回 0（上版滑块不显示根因），改为直接累加子控件 bounds
        private void _fnSyncScroll()
        {
            if (_viewport == null || _scrollBar == null) return;
            _scrollPanel.PerformLayout();   // 组高度变化后先重排（FlowLayoutPanel 布局默认挂起，直接读 bounds 是旧值）
            int contentH = _scrollPanel.Padding.Top;
            foreach (Control c in _scrollPanel.Controls)
                if (c.Visible) contentH = Math.Max(contentH, c.Bottom + c.Margin.Bottom);
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

        // 光标下的滚轮切换下拉（配色方案用）；null=不在 WheelSwitch 下拉上
        private DarkCombo _fnWheelCombo()
        {
            if (_viewport == null) return null;
            var ctl = _viewport.GetChildAtPoint(_viewport.PointToClient(Cursor.Position));
            while (ctl != null && !(ctl is DarkCombo))
            {
                if (!ctl.Visible) return null;
                ctl = ctl.GetChildAtPoint(ctl.PointToClient(Cursor.Position));
            }
            var dc = ctl as DarkCombo;
            return dc != null && dc.Visible && dc.Enabled ? dc : null;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            // 悬停在配色方案下拉上：滚轮切换方案而非滚动页面
            var dc = _fnWheelCombo();
            if (dc != null && dc.WheelSwitch)
            {
                dc.WheelStep(e.Delta > 0 ? -1 : 1);
                var hm = e as HandledMouseEventArgs;
                if (hm != null) hm.Handled = true;
                return;
            }
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
            if (_hoverGroup == null || _windowGroup == null || _cardGroup == null || _cardTriggerGroup == null || _entryGroup == null) return;
            if (_hoverPill == null || _cardPill == null || _entryPill == null || _delayStepper == null || _closeDelayStepper == null || _sortCombo == null) return;
            _hoverGroup.SetChip(0, HotkeyDisplay(_hoverToggle.On, _hoverMods, _hoverKey));
            _hoverGroup.SetChip(1, string.Format("{0:0.00} {1}", _delayStepper.Value, I18n.T("delay_unit")));
            _windowGroup.SetChip(0, _closeDelayStepper.Value + " ms");
            _windowGroup.SetChip(1, _sortCombo.SelectedIndex >= 0 ? _sortCombo.SelectedItem.ToString() : "");
            _cardTriggerGroup.SetChip(0, HotkeyDisplay(_cardToggle.On, _cardMods, _cardKey));
            _entryGroup.SetChip(0, HotkeyDisplay(_entryToggle.On, _entryMods, _entryKey));
        }

        private int _fnMakeHotkeyRow(AccGroup g, int y, string title, string desc,
            ToggleSwitch toggle, HotkeyPill pill,
            Action startCapture, Action clearHotkey)
        {
            int panelH = 30;
            var delBtn = new Button
            {
                Text = I18n.T("hotkey_delete"),
                FlatStyle = FlatStyle.Flat,
                BackColor = Config.BgHover,
                ForeColor = Config.FG,
                Font = new Font(Config.FontName, 8f),
                AutoSize = true,
                Padding = new Padding(6, 2, 6, 2),
                Cursor = Cursors.Hand
            };
            delBtn.FlatAppearance.BorderColor = Config.Border;
            delBtn.FlatAppearance.MouseOverBackColor = Config.BG;
            var resetBtn = new Button
            {
                Text = I18n.T("hotkey_reset"),
                FlatStyle = FlatStyle.Flat,
                BackColor = Config.BgHover,
                ForeColor = Config.FG,
                Font = new Font(Config.FontName, 8f),
                AutoSize = true,
                Padding = new Padding(6, 2, 6, 2),
                Cursor = Cursors.Hand
            };
            resetBtn.FlatAppearance.BorderColor = Config.Border;
            resetBtn.FlatAppearance.MouseOverBackColor = Config.BG;
            int pillAreaW = 100;
            int panelW = toggle.Width + 8 + delBtn.Width + 4 + resetBtn.Width + 8 + Math.Max(pillAreaW, pill.Width);
            var panel = new Panel
            {
                Size = new Size(panelW, panelH),
                BackColor = Config.BG
            };
            toggle.Location = new Point(0, (panelH - toggle.Height) / 2);
            delBtn.Location = new Point(toggle.Right + 8, (panelH - delBtn.Height) / 2);
            resetBtn.Location = new Point(delBtn.Right + 4, (panelH - resetBtn.Height) / 2);
            pill.Location = new Point(panelW - pill.Width, (panelH - pill.Height) / 2);
            panel.Controls.Add(toggle);
            panel.Controls.Add(delBtn);
            panel.Controls.Add(resetBtn);
            panel.Controls.Add(pill);
            delBtn.Click += (s, e) => clearHotkey();
            resetBtn.Click += (s, e) =>
            {
                if (pill.Recording) _fnStopCapture();
                else startCapture();
            };
            pill.SizeChanged += (s, e) =>
            {
                // pill 宽度随键序列变化：扩大 panel 容纳 pill，保持 panel 右边缘固定（对齐行右），
                // pill 右对齐到 panel 右边缘——避免 pill 向左延伸覆盖前方的重置/删除按钮
                int need = toggle.Width + 8 + delBtn.Width + 4 + resetBtn.Width + 8 + Math.Max(pillAreaW, pill.Width);
                if (need != panel.Width)
                {
                    int oldRight = panel.Right;
                    panel.Width = need;
                    panel.Left = oldRight - need;
                }
                pill.Location = new Point(panel.Width - pill.Width, (panelH - pill.Height) / 2);
            };
            int origY = y;
            y = g.AddRow(y, title, null, panel);
            Label d = null;
            int descH = 0;
            if (!string.IsNullOrEmpty(desc))
            {
                int descW = g.Width - AccGroup.InR - AccGroup.InL;
                d = new Label
                {
                    Text = desc,
                    Location = new Point(AccGroup.InL, AccGroup.HeadH + origY + panelH + 2),
                    AutoSize = true,
                    MaximumSize = new Size(descW, 0),
                    ForeColor = Config.PlaceholderFg,
                    Font = new Font(Config.FontName, 8f),
                    Tag = "sub",
                    Visible = g.Open
                };
                g.Controls.Add(d);
                descH = TextRenderer.MeasureText(desc, d.Font, new Size(descW, 10000),
                    TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height;
            }
            int rowH = d != null ? Math.Max(RowH, panelH + 2 + descH + 8) : Math.Max(RowH, panelH + 6);
            g.ExpandRow(origY, rowH);
            // 说明小字与行高登记重排：宽度变化时重测换行、重定位、重算行高
            g.TagLastRow(w2 =>
            {
                if (d == null) return Math.Max(RowH, panelH + 6);
                int dw = Math.Max(160, w2 - AccGroup.InR - AccGroup.InL);
                d.MaximumSize = new Size(dw, 0);
                int dh = TextRenderer.MeasureText(desc, d.Font, new Size(dw, 10000),
                    TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height;
                return Math.Max(RowH, panelH + 2 + dh + 8);
            }, py =>
            {
                if (d != null) d.Location = new Point(AccGroup.InL, AccGroup.HeadH + py + panelH + 2);
            });
            return origY + rowH;
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
                _fnPlacePill(_cardPill, _cardTriggerGroup);
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
            Config.PauseRectDelayMs = _pauseDelayStepper.Value;
            Config.CardHotkeyEnabled = _cardToggle.On;
            Config.CardHotkeyModifiers = _cardMods;
            Config.CardHotkeyKey = _cardKey;
            Config.KeepOnLeave = _keepToggle.On;
            Config.RowSwapSides = _rowSwapToggle.On;
            Config.StatusFilter = _statusFilterToggle.On;
            if (_modeExplorer != null) ApplyMode(true, _modeExplorer.SelectedIndex);
            if (_modePreview != null) ApplyMode(false, _modePreview.SelectedIndex);
            if (_longPressStepper != null) Config.LongPressDelayMs = _longPressStepper.Value;
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
            if (_pvVideoDelayStepper != null) Config.VideoPreviewDelay = _pvVideoDelayStepper.Value;
            if (_pvEdgeCombo != null) Config.PreviewEdgeMode = Math.Max(0, Math.Min(3, _pvEdgeCombo.SelectedIndex));
            if (_pvEdgeWidthStepper != null) Config.PreviewEdgeWidth = _pvEdgeWidthStepper.Value;
            if (_pvAudioToggle != null) Config.PreviewAudio = _pvAudioToggle.On;
            if (_pvDocToggle != null) Config.PreviewDocument = _pvDocToggle.On;
            if (_pvTextToggle != null) Config.PreviewText = _pvTextToggle.On;
            if (_pvArchiveToggle != null) Config.PreviewArchive = _pvArchiveToggle.On;
            if (_pvFontToggle != null) Config.PreviewFont = _pvFontToggle.On;
            if (_pvOtherToggle != null) Config.PreviewOther = _pvOtherToggle.On;
            if (_pvSpeedToggle != null) Config.SpeedToggle = _pvSpeedToggle.On;
            if (_pvWaveformToggle != null) Config.WaveformEnabled = _pvWaveformToggle.On;
            if (_pvSpectrumToggle != null) Config.SpectrumEnabled = _pvSpectrumToggle.On;
            if (_pvMuteToggle != null) Config.PreviewMuted = _pvMuteToggle.On;
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

        private void _fnApplyTrayIcon()
        {
            if (_trayIconBox != null)
            {
                string p = (_trayIconBox.Text ?? "").Trim();
                if (p != Config.TrayIconPath)
                {
                    Config.TrayIconPath = p;
                    Config.Save();
                    Program.ApplyTrayIcon();
                }
            }
        }

        // 模式下拉（悬停浏览/文件预览共用）：0 关闭 1 总是 2 按住Ctrl 3 按住Shift 4 按住Alt 5 长按
        private DarkCombo _fnMakeModeCombo(int mode)
        {
            var cb = new DarkCombo { Font = new Font(Config.FontName, 8.5f), Width = 130, Height = 24 };
            cb.SetItems(new object[] { I18n.T("mode_off"), I18n.T("mode_always"), I18n.T("mode_ctrl"), I18n.T("mode_shift"), I18n.T("mode_alt"), I18n.T("mode_long_press") },
                Math.Max(0, Math.Min(5, mode)));
            return cb;
        }



        // Config 三个底层字段（启用/门控开/门控键）→ 模式档位
        private static int ModeFromConfig(bool enabled, bool gateOn, int mods)
        {
            if (!enabled) return 0;
            if (!gateOn) return 1;
            if (mods == 1) return 3;
            if (mods == 4) return 4;
            if (mods == 8) return 5;
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
        }

        // 模式档位 → 底层字段
        private static void ApplyMode(bool explorer, int mode)
        {
            bool en = mode > 0;
            bool gate = mode >= 2;
            int mods = mode == 3 ? 1 : mode == 4 ? 4 : mode == 5 ? 8 : 2;
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
            if (_iconBox != null) _iconBox.BackColor = Config.BgHover;
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
                if ((c.Tag as string) == "nav") continue;                  // 侧栏导航项样式由 _fnSwitchPage 管理
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

        // 边缘热区容器：侧栏/视口/滚动层等子窗口盖住了窗体边缘，
        // WM_NCHITTEST 与尺寸拖拽转交宿主设置窗处理（无边框窗口的八向调整）
        private class HotEdgePanel : Panel
        {
            protected override void WndProc(ref Message m)
            {
                var sf = FindForm() as SettingsForm;
                if (sf != null)
                {
                    const int WM_NCHITTEST = 0x0084;
                    const int WM_NCLBUTTONDOWN = 0x00A1;
                    if (m.Msg == WM_NCHITTEST) { if (sf._fnNcHitTest(ref m)) return; }
                    else if (m.Msg == WM_NCLBUTTONDOWN) { if (sf.ForwardSizeDrag(ref m)) return; }
                }
                base.WndProc(ref m);
            }
        }

        // 同上，滚动内容层为 FlowLayoutPanel
        private class HotEdgeFlowPanel : FlowLayoutPanel
        {
            protected override void WndProc(ref Message m)
            {
                var sf = FindForm() as SettingsForm;
                if (sf != null)
                {
                    const int WM_NCHITTEST = 0x0084;
                    const int WM_NCLBUTTONDOWN = 0x00A1;
                    if (m.Msg == WM_NCHITTEST) { if (sf._fnNcHitTest(ref m)) return; }
                    else if (m.Msg == WM_NCLBUTTONDOWN) { if (sf.ForwardSizeDrag(ref m)) return; }
                }
                base.WndProc(ref m);
            }
        }

        // 可视区容器：双缓冲 + 裁切滚动内容层（WinForms 子控件溢出父 bounds 默认被裁剪）
        private class ViewportPanel : HotEdgePanel
        {
            public ViewportPanel() { DoubleBuffered = true; AutoScroll = false; }
        }

        // 手风琴分组：标题行（点 + 标题 + chip + 开关 + 箭头）+ 可折叠内容行
        private class AccGroup : Panel
        {
            // 分组滚到窗口底部时会盖住下缘热区：命中测试与尺寸拖拽转交宿主窗体
            protected override void WndProc(ref Message m)
            {
                var sf = FindForm() as SettingsForm;
                if (sf != null)
                {
                    const int WM_NCHITTEST = 0x0084;
                    const int WM_NCLBUTTONDOWN = 0x00A1;
                    if (m.Msg == WM_NCHITTEST) { if (sf._fnNcHitTest(ref m)) return; }
                    else if (m.Msg == WM_NCLBUTTONDOWN) { if (sf.ForwardSizeDrag(ref m)) return; }
                }
                base.WndProc(ref m);
            }

            public const int HeadH = 38;
            public const int InL = 14;
            public const int InR = 14;
            public int PageIdx = 0;   // 所属分页（侧栏导航用）

            // ==== 宽度可变重排：行栈统一登记（AddRow 行 + 手动块），Relayout 自上而下重算 ====
            private abstract class StackEntry { public abstract int Layout(AccGroup g, int y); }

            // AddRow 产生的行：标题/描述/右侧控件，重排时按新宽度重定位、重测描述换行
            private sealed class RowEntry : StackEntry
            {
                public Label Title, Desc;
                public Control Right;
                public Func<int, int> HeightFn;   // 自定义行高（热键/区域等含手动内容的行），参数=组宽
                public Action<int> ExtraPlace;    // 行内额外手动控件定位，参数=行 y（相对内容区）
                public override int Layout(AccGroup g, int y)
                {
                    Title.Location = new Point(InL, HeadH + y);
                    int rowH;
                    if (HeightFn != null)
                        rowH = HeightFn(g.Width);
                    else if (Desc != null)
                    {
                        int contentRight = g.Width - InR;
                        int descRight = Right != null ? contentRight - Right.Width - 24 : contentRight;
                        int descW = Math.Max(160, descRight - InL);
                        Desc.MaximumSize = new Size(descW, 0);
                        int descH = TextRenderer.MeasureText(Desc.Text, Desc.Font, new Size(descW, 10000),
                            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height;
                        Desc.Location = new Point(InL, HeadH + y + 19);
                        rowH = Math.Max(RowH, 19 + descH + 14);
                    }
                    else rowH = RowH;
                    if (Right != null)
                    {
                        int dy = Right.Height <= 22 ? 0 : -2;
                        Right.Location = new Point(g.Width - InR - Right.Width, HeadH + y + dy);
                    }
                    if (ExtraPlace != null) ExtraPlace(y);
                    return y + rowH;
                }
            }

            // 手动块：自行定位一批控件并返回链推进后的 y（FFmpeg 状态行、方案按钮区、托盘列表行等）
            private sealed class BlockEntry : StackEntry
            {
                public Func<int, int, int> Fn;   // (y, 组宽) → 新 y
                public override int Layout(AccGroup g, int y) { return Fn(y, g.Width); }
            }

            private readonly List<StackEntry> _stack = new List<StackEntry>();
            private int _stackStartY = -1;                            // 首行起始 y（首次 AddRow 记录）
            private readonly List<Action<int>> _tailHooks = new List<Action<int>>();   // 链尾手动块（提示段落/预览区），参数=链尾 y

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
                _chev = new ChevronIcon { Location = new Point(width - 2 - 13 - 12, 13), Cursor = Cursors.Hand, Visible = false };
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
                Label dl;
                return AddRow(y, title, desc, right, out dl);
            }

            public int AddRow(int y, string title, string desc, Control right, out Label descLabel)
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
                descLabel = d;
                _stack.Add(new RowEntry { Title = t, Desc = d, Right = right });   // 登记行栈，供宽度重排
                if (_stackStartY < 0) _stackStartY = y;
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

            // 给最近一次 AddRow 的行挂自定义高度/额外定位（热键行、暂停区行、入口区域行等手动内容行）
            public void TagLastRow(Func<int, int> heightFn, Action<int> extraPlace)
            {
                for (int i = _stack.Count - 1; i >= 0; i--)
                {
                    var re = _stack[i] as RowEntry;
                    if (re == null) continue;
                    if (heightFn != null) re.HeightFn = heightFn;
                    if (extraPlace != null) re.ExtraPlace = extraPlace;
                    return;
                }
            }

            // 在链中插入手动块（按代码顺序与 AddRow 行交替重排）
            public void AddBlock(Func<int, int, int> fn)
            {
                // 组以 AddBlock 开头（如桌面卡片外观组）时也要记录栈起点，否则首个 AddRow 的 y
                // 会被当作栈起点，重排时整个内容被推到下方形成大片空白
                if (_stackStartY < 0) _stackStartY = 0;
                _stack.Add(new BlockEntry { Fn = fn });
            }

            // 注册链尾手动块（提示段落/预览区）：重排时以链尾 y 自行定位，内部用 NoticeContentBottom 补报高度
            public void AddTailHook(Action<int> hook) { _tailHooks.Add(hook); }

            // 手动块补报内容底边（相对 y），只增不减
            public void NoticeContentBottom(int relY)
            {
                if (relY > _contentH) _contentH = relY;
            }

            // 宽度变化后整体重排：头部条 → 行栈 → 链尾块 → 收尾（行高与 AddRow 构建时同一套测高算法）
            public void Relayout(int w)
            {
                Width = w;
                _head.Size = new Size(w - 2, HeadH - 1);
                _chev.Location = new Point(w - 2 - 13 - 12, 13);
                if (_toggle != null)
                    _toggle.Location = new Point(_chev.Left - 10 - _toggle.Width, (HeadH - _toggle.Height) / 2);
                // 头部链接按标题实际宽度重排（AddHeadLink 构建逻辑的逆操作）
                _title.Size = new Size(w - 2 - 25 - 195, HeadH - 4);
                int titleTextW = TextRenderer.MeasureText(_title.Text, _title.Font).Width + 4;
                _title.Width = Math.Min(_title.Width, titleTextW);
                int lx = _title.Right + 8;
                foreach (Control c in _head.Controls)
                    if (c is LinkLabel) { c.Location = new Point(lx, c.Top); lx = c.Right + 8; }
                PlaceChips();
                if (_stack.Count > 0)
                {
                    int y = Math.Max(0, _stackStartY);
                    foreach (var e in _stack) y = e.Layout(this, y);
                    _contentH = y;
                    foreach (var h in _tailHooks) h(y);
                    _contentH += 10;
                }
                if (_open) Height = HeadH + _contentH;
                Invalidate();
            }

            // 自由排版组（关于）：说明段落手动定位，几何审计的行式规则不适用
            public bool FreeformLayout;

            public void FinishRows()
            {
                _contentH += 10;
                if (_open)
                {
                    Height = HeadH + _contentH;
                    foreach (Control c in Controls)
                        if (c != _head) c.Visible = true;
                }
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
                if ((c.Tag as string) == "tip") continue;                    // 黄底黑字说明标签，样式自定不被主题覆盖
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
            // true=滚轮循环切换选中项（配色方案下拉用），并吞掉滚轮防止设置页滚动
            public bool WheelSwitch;

            // 滚轮步进：dir -1=上一项 +1=下一项（循环）；与点击选择同效（触发 SelectedIndexChanged）
            internal void WheelStep(int dir)
            {
                if (!Enabled || Items.Count == 0) return;
                int n = Items.Count;
                int baseIdx = _index < 0 ? 0 : _index;
                SetIndex((baseIdx + n + dir) % n, false);
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                if (WheelSwitch)
                {
                    WheelStep(e.Delta > 0 ? -1 : 1);
                    var hm = e as HandledMouseEventArgs;
                    if (hm != null) hm.Handled = true;   // 不再透传给父级滚动页面
                    return;
                }
                base.OnMouseWheel(e);
            }

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
                int ph = Math.Max(30, Math.Min(ItemCount * 24 + 6, wa.Height * 2 / 3));
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
            private int _scroll;
            private int _maxScroll;
            private SlimScrollBar _bar;

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
                _bar = new SlimScrollBar { Width = 10, Dock = DockStyle.Right, BackColor = Config.BG };
                _bar.Scroll += (s, e) => { _scroll = _bar.Value; Invalidate(); };
                Controls.Add(_bar);
            }

            private int VisibleCount { get { return (Height - 6) / 24; } }

            private void EnsureScroll()
            {
                int vis = VisibleCount;
                _maxScroll = Math.Max(0, _combo.ItemCount - vis);
                if (_scroll < 0) _scroll = 0;
                if (_scroll > _maxScroll) _scroll = _maxScroll;
                if (_bar != null)
                {
                    _bar._suppressScroll = true;
                    _bar.Maximum = Math.Max(1, _combo.ItemCount);
                    _bar.LargeChange = Math.Max(1, vis);
                    _bar.Value = _scroll;
                    _bar._suppressScroll = false;
                }
            }

            private Rectangle ItemRect(int i) { return new Rectangle(3, 3 + (i - _scroll) * 24, Width - 10, 24); }

            protected override void OnPaint(PaintEventArgs e)
            {
                EnsureScroll();
                using (var p = new Pen(Config.Border)) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
                int vis = VisibleCount;
                for (int k = 0; k < vis; k++)
                {
                    int i = _scroll + k;
                    if (i >= _combo.ItemCount) break;
                    var rc = ItemRect(i);
                    if (rc.Bottom > Height - 3) break;
                    bool cur = i == _combo.SelectedIndex;
                    using (var b = new SolidBrush(i == _hover ? Config.BtnHover : Config.BG)) e.Graphics.FillRectangle(b, rc);
                    TextRenderer.DrawText(e.Graphics, _combo.ItemAt(i), Font, rc, cur ? Config.Accent : Config.FG,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                EnsureScroll();
                int delta = e.Delta > 0 ? -1 : 1;
                _scroll = Math.Max(0, Math.Min(_maxScroll, _scroll + delta));
                _hover = -1;
                EnsureScroll();
                Invalidate();
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
                EnsureScroll();
                int sel = _combo.SelectedIndex;
                if (keyData == Keys.Down)
                {
                    int n = Math.Min(_combo.ItemCount - 1, sel < 0 ? 0 : sel + 1);
                    if (n > _scroll + VisibleCount - 1) _scroll = n - VisibleCount + 1;
                    EnsureScroll(); _hover = n; Invalidate(); return true;
                }
                if (keyData == Keys.Up)
                {
                    int n = Math.Max(0, sel < 0 ? 0 : sel - 1);
                    if (n < _scroll) _scroll = n;
                    EnsureScroll(); _hover = n; Invalidate(); return true;
                }
                if (keyData == Keys.Enter) { if (sel >= 0) _combo.Commit(sel); else if (_hover >= 0) _combo.Commit(_hover); return true; }
                if (keyData == Keys.PageDown)
                {
                    _scroll = Math.Min(_maxScroll, _scroll + VisibleCount - 1);
                    _hover = Math.Min(_combo.ItemCount - 1, _scroll + VisibleCount - 1);
                    EnsureScroll(); Invalidate(); return true;
                }
                if (keyData == Keys.PageUp)
                {
                    _scroll = Math.Max(0, _scroll - VisibleCount + 1);
                    _hover = _scroll;
                    EnsureScroll(); Invalidate(); return true;
                }
                if (keyData == Keys.Home) { _scroll = 0; _hover = 0; EnsureScroll(); Invalidate(); return true; }
                if (keyData == Keys.End) { _scroll = _maxScroll; _hover = _combo.ItemCount - 1; EnsureScroll(); Invalidate(); return true; }
                return base.ProcessCmdKey(ref msg, keyData);
            }
            private int Hit(Point p)
            {
                if (p.X < 3 || p.X > Width - 6 || p.Y < 3) return -1;
                int k = (p.Y - 3) / 24;
                int i = _scroll + k;
                if (i < 0 || i >= _combo.ItemCount) return -1;
                return i;
            }
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
            if (d <= 0) { path.AddRectangle(new RectangleF(x, y, w, h)); return path; }
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + w - d, y, d, d, 270, 90);
            path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            path.AddArc(x, y + h - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private class LongPressStepper : Control
        {
            private int _val = 1500;

            public event EventHandler ValueChanged;

            public int Value
            {
                get { return _val; }
                set
                {
                    var nv = Math.Max(200, Math.Min(3000, value));
                    if (nv == _val) return;
                    _val = nv;
                    Invalidate();
                    if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
                }
            }

            public LongPressStepper()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
                SetStyle(ControlStyles.Selectable, false);
                Size = new Size(116, 26);
                Cursor = Cursors.Hand;
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.X < 22) Value = _val - 100;
                else if (e.X > Width - 22) Value = _val + 100;
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

        // 字号步进器：[-] 10 [+]，分区字体行用（6~24，步长 1）
        private class FontStepper : Control
        {
            private int _val = 10;

            public event EventHandler ValueChanged;

            public int Value
            {
                get { return _val; }
                set
                {
                    var nv = Math.Max(6, Math.Min(24, value));
                    if (nv == _val) return;
                    _val = nv;
                    Invalidate();
                    if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
                }
            }

            public FontStepper()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
                SetStyle(ControlStyles.Selectable, false);
                Size = new Size(74, 26);
                Cursor = Cursors.Hand;
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.X < 22) Value = _val - 1;
                else if (e.X > Width - 22) Value = _val + 1;
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
                    TextRenderer.DrawText(g, _val.ToString(), f, new Rectangle(22, 0, Width - 44, Height), Config.FG, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                using (var p = new Pen(Config.Border))
                {
                    g.DrawLine(p, 22, 4, 22, Height - 4);
                    g.DrawLine(p, Width - 22, 4, Width - 22, Height - 4);
                }
            }
        }

        // 列表外观·实时预览：模拟弹出列表面板（标题行/表头/列表行/状态栏格式标签），
        // 全部走 Config.List* 配色与分区字体，字体/配色设置变化时 Invalidate 重绘
        private class ListPreviewPanel : Control
        {
            public ListPreviewPanel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                    | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                SetStyle(ControlStyles.Selectable, false);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;   // 文字抗锯齿（ClearType），否则预览字体有明显锯齿
                int w = Width, h = Height;
                // 卡片底：列表画布色 + 圆角描边，后续条带都裁进圆角内
                using (var path = _fnRoundRect(0.5f, 0.5f, w - 1, h - 1, 4))
                {
                    using (var b = new SolidBrush(Config.ListCanvas)) g.FillPath(b, path);
                    using (var p = new Pen(Config.ListBd)) g.DrawPath(p, path);
                    g.SetClip(path);
                }
                // 标题行：文件夹名（粗体）+ 右侧条目计数小字（同 PopupForm：ListHdbg 底、ListFg/ListMu）
                using (var fT = Config.ListTitleFont())
                using (var fM = Config.ListMetaFont())
                using (var bH = new SolidBrush(Config.ListHdbg))
                using (var bFg = new SolidBrush(Config.ListFg))
                using (var bMu = new SolidBrush(Config.ListMu))
                {
                    g.FillRectangle(bH, 1, 1, w - 2, 22);
                    string title = I18n.T("preview_sample_folder");
                    g.DrawString(title, fT, bFg, 8, 1 + (22 - fT.GetHeight(g)) / 2, StringFormat.GenericTypographic);
                    string cnt = I18n.T("panel_status_count", 185);
                    g.DrawString(cnt, fM, bMu, w - 8 - g.MeasureString(cnt, fM).Width, 1 + (22 - fM.GetHeight(g)) / 2, StringFormat.GenericTypographic);
                }
                // 表头：排序按钮（同 PopupForm 排序栏：描边小按钮，当前项带 ▲）
                using (var fH = Config.ListHeaderFont())
                using (var bBg = new SolidBrush(Config.ListBg))
                using (var pBd = new Pen(Config.ListBd))
                using (var bFg = new SolidBrush(Config.ListFg))
                {
                    int bx = 6;
                    var labs = new[] { Config.SortLabels["name"] + " ▲", Config.SortLabels["size"], Config.SortLabels["date"] };
                    foreach (var lab in labs)
                    {
                        int bw = (int)g.MeasureString(lab, fH).Width + 14;
                        var rc = new Rectangle(bx, 25, bw, 18);
                        using (var path = _fnRoundRect(rc.X, rc.Y, rc.Width, rc.Height, 3))
                        {
                            g.FillPath(bBg, path);
                            g.DrawPath(pBd, path);
                        }
                        g.DrawString(lab, fH, bFg, rc.X + 7, rc.Y + (rc.Height - fH.GetHeight(g)) / 2, StringFormat.GenericTypographic);
                        bx += bw + 4;
                    }
                }
                // 列表行：示例文件 + 行内大小小字（同 PopupForm：ListBg 底、ListFg 名称、ListMu 大小）
                using (var fI = Config.ListItemFont())
                using (var fM = Config.ListMetaFont())
                using (var bR = new SolidBrush(Config.ListBg))
                using (var bFg = new SolidBrush(Config.ListFg))
                using (var bMu = new SolidBrush(Config.ListMu))
                using (var pMu = new Pen(Config.ListMu))
                {
                    g.FillRectangle(bR, 1, 45, w - 2, 24);
                    g.DrawRectangle(pMu, 8, 51, 10, 12);   // 图标位示意
                    g.DrawString(I18n.T("preview_sample_file"), fI, bFg, 26, 45 + (24 - fI.GetHeight(g)) / 2, StringFormat.GenericTypographic);
                    string sz = "1.2 MB";
                    g.DrawString(sz, fM, bMu, w - 8 - g.MeasureString(sz, fM).Width, 45 + (24 - fM.GetHeight(g)) / 2, StringFormat.GenericTypographic);
                }
                // 状态栏格式标签（同 PopupForm chips：ListHdbg 底、ListFg 文本）
                using (var fC = Config.ListChipFont())
                using (var bH = new SolidBrush(Config.ListHdbg))
                using (var bFg = new SolidBrush(Config.ListFg))
                {
                    int cx = 6;
                    foreach (var kind in new[] { "MP4", "JPG", "TXT" })
                    {
                        int cw = (int)g.MeasureString(kind, fC).Width + 12;
                        g.FillRectangle(bH, cx, 73, cw, 16);
                        g.DrawString(kind, fC, bFg, cx + 6, 73 + (16 - fC.GetHeight(g)) / 2, StringFormat.GenericTypographic);
                        cx += cw + 4;
                    }
                }
                g.ResetClip();
            }
        }

        // 桌面卡片·实时预览：按 PinCardForm 的实际观感自绘一张示例卡片
        // （自绘阴影/圆角底/左色条/今日绿点/名称/扩展名徽标），全部实时读 CardPalettes.Current，
        // 卡片配色或字体编辑应用后由调用方 Invalidate 重绘
        private class CardPreviewPanel : Control
        {
            public CardPreviewPanel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                    | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                SetStyle(ControlStyles.Selectable, false);
            }

            // 圆角矩形路径（r<=0 退化为直角矩形，同 PinCardForm._fnRoundRect）
            private static GraphicsPath _fnCardRoundRect(float x, float y, float w, float h, float r)
            {
                var path = new GraphicsPath();
                if (r <= 0) { path.AddRectangle(new RectangleF(x, y, w, h)); return path; }
                float d = Math.Min(r * 2, Math.Min(w, h));
                path.AddArc(x, y, d, d, 180, 90);
                path.AddArc(x + w - d, y, d, d, 270, 90);
                path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
                path.AddArc(x, y + h - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var pal = CardPalettes.Current;
                if (pal == null) return;
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;   // 文字抗锯齿（ClearType），否则预览字体有明显锯齿

                using (var fN = new Font(pal.FontName, pal.FontSize))
                using (var fB = new Font(pal.FontName, Math.Max(6f, pal.FontSize - 1f), FontStyle.Bold))
                {
                    // 宽度收缩为卡片实际占用宽度（同 PinCardForm 布局：21 起点 + 名称 + 间隙 + 徽标 + 右缘留白 + 阴影），
                    // 字体变化时跟随；不占满设置面板整行
                    string nm = I18n.T("card_preview_sample");
                    int nameNeed = Math.Min(TextRenderer.MeasureText(nm, fN).Width + 2, 260);
                    int extNeed = TextRenderer.MeasureText("PNG", fB, new Size(200, 20), TextFormatFlags.NoPadding).Width + 8;
                    int needW = 21 + nameNeed + 5 + extNeed + 8 + 2;
                    if (Width != needW) { Width = needW; return; }   // ResizeRedraw 会再触发一次重绘，直接返回避免用旧宽画
                    // 几何同 PinCardForm._fnLayout：内容上下各留 4px padding，至少 24px 高
                    int nameH = (int)Math.Ceiling(fN.GetHeight(g)) + 2;
                    int badgeH = nameH + 2;
                    int ch = Math.Max(24, badgeH + 8);
                    int shadow = 2;                       // 自绘阴影宽度（同 PinCardForm.ShadowPad）
                    int cw = Width - shadow;
                    int cy = Math.Max(0, (Height - ch) / 2);
                    float radius = Math.Max(0, Math.Min(12, pal.Radius));

                    // 自绘阴影（右下偏移，跟着圆角走）
                    for (int i = shadow; i > 0; i--)
                        using (var sp = _fnCardRoundRect(i, cy + i, cw, ch, radius))
                        using (var sb = new SolidBrush(Color.FromArgb(30, 0, 0, 0)))
                            g.FillPath(sb, sp);

                    // 背景（圆角 + 支持半透明）
                    using (var path = _fnCardRoundRect(0, cy, cw, ch, radius))
                        if (pal.Bg.A > 0) using (var b = new SolidBrush(pal.Bg)) g.FillPath(b, path);

                    // 左侧色条（裁剪到卡片圆角形状内）
                    if (pal.StripWidth > 0 && pal.StripColor.A > 0)
                        using (var clip = _fnCardRoundRect(0, cy, cw, ch, radius))
                        {
                            g.SetClip(clip);
                            using (var b = new SolidBrush(pal.StripColor))
                                g.FillRectangle(b, 0, cy, Math.Min(pal.StripWidth, cw), ch);
                            g.ResetClip();
                        }

                    // 示例内容：今日绿点 + 名称 + 扩展名徽标（布局同 PinCardForm）
                    string name = I18n.T("card_preview_sample");
                    string ext = "PNG";
                    int x = 21;                           // 有绿点时名称从 21 起（同 PinCardForm._fnLayout）
                    int nameY = cy + (ch - nameH) / 2;
                    int badgeY = cy + (ch - badgeH) / 2;
                    int nameW = Math.Min(Math.Min(TextRenderer.MeasureText(name, fN).Width + 2, 260), Math.Max(40, cw - x - 8 - 60));
                    int extW = TextRenderer.MeasureText(ext, fB, new Size(200, 20), TextFormatFlags.NoPadding).Width + 8;
                    using (var b = new SolidBrush(Color.FromArgb(0x2f, 0xb3, 0x6b)))
                        g.FillEllipse(b, 10, cy + 10, 5, 5);   // 今天修改过的状态点
                    // 徽标背景（圆角）
                    if (pal.BadgeBg.A > 0)
                        using (var path = _fnCardRoundRect(x + nameW + 5, badgeY, extW, badgeH, radius))
                        using (var b = new SolidBrush(pal.BadgeBg))
                            g.FillPath(b, path);
                    // 徽标文字（居中，同 PinCardForm）
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.None })
                    using (var br = new SolidBrush(pal.BadgeFg))
                        g.DrawString(ext, fB, br, new RectangleF(x + nameW + 5, badgeY, extW, badgeH), sf);
                    // 名称（超长省略号，同 PinCardForm）
                    using (var sf = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                    using (var br = new SolidBrush(pal.Fg))
                        g.DrawString(name, fN, br, new RectangleF(x, nameY, nameW, nameH), sf);
                }
            }
        }
    }
}