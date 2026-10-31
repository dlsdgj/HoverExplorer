# HoverExplorer

![HoverExplorer](images/1_cover.png)

鼠标悬停即可浏览文件夹内容与预览文件，无需点击打开。

> Hover to browse folder contents and preview files — no clicks needed.

## 界面预览 / Screenshots

**文件列表面板** —— 悬停桌面/资源管理器文件夹即时弹出；触发区域可自定义，半透明色块实时预览

**File list panel** — Hover desktop/Explorer folders to pop up instantly; trigger area customizable with semi-transparent color preview

![文件列表面板](images/2_layout.png)

**收藏·历史列表** —— 桌面触发区或 `Ctrl+Shift+E` 热键呼出，行首星标一键收藏/取消

**Favorites · History list** — Summon via desktop trigger area or `Ctrl+Shift+E` hotkey; star icon to favorite/unfavorite

![收藏·历史列表](images/3_favorites.png)

**桌面卡片** —— 文件钉成桌面浮动卡片，卡片上滚轮切换 11 套配色（逐张独立记忆）

**Desktop cards** — Pin files as floating cards; scroll on a card to cycle 11 color schemes (per-card memory)

![桌面卡片配色](images/4_ai_colors.png)

**音频波形播放器** —— Media Foundation 后台解码整段文件生成峰值/RMS 波形，波形即进度，点击/拖动跳转

**Audio waveform player** — Media Foundation decodes full file in background to peak/RMS waveform; waveform = progress, click/drag to seek

![音频波形播放器](images/5_waveform.png)

## 功能 / Features

- **桌面悬停 / Desktop hover**: 鼠标悬停桌面文件夹 → 弹出文件列表，悬停文件 → 直接预览 — Hover desktop folder → file list; hover file → direct preview
- **资源管理器悬停 / Explorer hover**: 资源管理器中悬停浏览（可选），详细/图标视图均可；详细视图默认仅图标/文件名区触发 — Hover inside Explorer (optional); works in details/icon views; details view triggers only on icon/name area
- **文件预览 / File preview**: 图片、文本、代码、压缩包(zip/7z/rar)、Office、字体、exe、epub、音视频、SVG 等 — Images, text, code, archives (zip/7z/rar), Office, fonts, exe, epub, audio/video, SVG, etc.
- **播放器倍速与关闭 / Player speed & close**: 播放条上可切 0.5×–2× 倍速（点击循环），一键 ✕ 关闭预览窗；视频/音频均支持 — Playback speed 0.5×–2× on the control bar (click to cycle) plus a ✕ to close the preview; video and audio
- **按类型开关预览 / Preview types on-off**: 逐类开关要预览的文件类型（图片/视频/音频/文档/文本/压缩包/字体/其他），关掉的类型不再弹预览窗 — Toggle previews per type (image/video/audio/document/text/archive/font/other); disabled types never open a preview
- **悬停门控 / Modifier-gated hover**: 开启后必须按住所选修饰键（Ctrl/Shift/Alt 或组合）悬停才触发，文件夹列表与文件预览可分别设置，重命名、多选不再被弹窗打扰 —  hover only triggers while a chosen modifier key (Ctrl/Shift/Alt or a combo) is held; folder lists and file previews can be gated separately, so renaming and multi-select stay undisturbed
- **视频预览默认静音 / Mute video previews by default**: 悬停触发的视频先静音，点击播放条 🔊 恢复声音；音频预览不受影响 — Hover-triggered videos start muted; click 🔊 on the control bar to unmute; audio previews unaffected
- **m4v 等移动端容器 / m4v & mobile containers**: 补齐 `.m4v` `.m4b` `.3gp` `.mts` `.m2ts` `.webm` `.flv` 等扩展名，直接出画面而非只有元数据 — Adds `.m4v` `.m4b` `.3gp` `.mts` `.m2ts` `.webm` `.flv` so they preview instead of showing metadata only
- **高规格视频兜底解码 / High-spec video fallback**: 系统解码器打不开时（ProRes / 10bit / 4:2:2 / HEVC）自动改用 ffmpeg 帧流播放 — When the system decoder fails (ProRes / 10-bit / 4:2:2 / HEVC), falls back to an ffmpeg frame-stream player
- **音频波形播放器 / Audio waveform**: 迷你播放条，波形即进度，MF 后台解码峰值/RMS，点击/拖动 seek — Mini player bar, waveform = progress, MF background peak/RMS decode, click/drag to seek
- **收藏·历史列表 / Favorites · History**: 自定义触发区域或热键弹出常用文件夹；星标收藏；行悬停操作按钮；宽度可拖拽记忆 — Custom trigger area or hotkey; star to favorite; row hover action buttons; draggable width with memory
- **关闭延迟可调 / Adjustable close delay**: 50–2000ms 统一控制列表/预览/收藏离开后关闭速度 — 50–2000ms unified close speed for list/preview/favorites after mouse leaves
- **设置即改即生效 / Live settings**: 无确定/取消按钮，任何改动实时保存生效 — No OK/Cancel; all changes save and apply instantly
- **快捷键切换 / Hotkey toggle**: 全局快捷键启用/禁用悬停（默认 `Ctrl+Alt+Q`，可自定义） — Global hotkey to enable/disable hover (default `Ctrl+Alt+Q`, customizable)
- **悬停延时可调 / Adjustable hover delay**: 自定义触发延时，文件夹和文件统一生效 — Custom trigger delay, applies to folders and files
- **深浅主题 / Light/Dark theme**: 深色/浅色/跟随系统三选一；跟随系统时 Windows 切换深浅色（含 PowerToys 等自动切换）面板即时跟上 — Dark/light/follow-system; in follow-system mode panels switch instantly when Windows (incl. PowerToys schedulers) flips its app theme
- **卡片配色（逐张独立）/ Per-card color scheme**: 11 套标签带方案；滚轮切换该卡片配色，实时高亮，~1s 消失 — 11 label-band schemes; scroll to cycle per-card, live highlight, ~1s fade
- **拖拽弹簧式文件夹 / Drag-spring folders**: 拖文件悬停任意文件夹即弹内容，子文件夹逐层展开导航，松手放入 — Drag file over any folder to pop contents; navigate subfolders; drop at target level
- **暂停触控区 / Pause zone**: 框选屏幕区域，驻留 0.3s 切换启用/暂停，与快捷键等效 — Screen region; dwell 0.3s to toggle pause, equivalent to hotkey
- **MP3 秒开秒停 / MP3 instant play**: MCI 毫秒级打开，VBR 按 Xing 头校正时长；识别假 MP3 并超时保护 — MCI millisecond open; VBR corrected by Xing header; fake MP3 detection with timeout
- **MP3 修复工具 / MP3 fixer**: `Mp3Fixer.exe` 批量把容器型假 MP3 转真 MP3，ffmpeg 自动探测/下载 — `Mp3Fixer.exe` batch-converts fake MP3 to real; ffmpeg auto-detect/download
- **ffmpeg 一键下载 / One-click ffmpeg download**: 设置里可一键下载官方构建（gyan.dev），自动落到程序能探测到的位置，高规格视频兜底解码开箱即用 — One-click download of the official build (gyan.dev) from settings; lands where the app auto-detects it, so fallback decoding works out of the box
- **面板高度自适应 / Auto panel height**: 条目多时自动长高（以工作区为限）；手动拖过后转记忆模式 — Auto-grow with items (workspace limit); manual drag switches to memory mode
- **7 语言 / 7 languages**: 中文、English、日本語、한국어、Français、Русский、Español
- **关闭系统提示 / Disable system tooltip**: 可选隐藏 Windows 原生 tooltip — Optional hide Windows native tooltip
- **开机自启 / Autostart**: 可选写入注册表 Run 键，免安装 — Optional registry Run key, portable
- **隐藏托盘图标 / Hide tray icon**: 隐藏后右键面板空白处呼出菜单 — Right-click panel blank area for menu when hidden
- **固定文件卡片 / Pin file card**: 预览窗图钉固定为桌面卡片，▶ 执行，中键关闭，右键原生菜单 — Pin preview as desktop card; ▶ to run; middle-click close; right-click native menu
- **文件列表右键菜单 / List context menu**: 打开/编辑/新窗口/重命名/删除/复制路径·名称/原生菜单 — Open/edit/new window/rename/delete/copy path·name/native menu
- **预览窗复制菜单 / Preview copy menu**: 复制选中文本/全文/文件/路径，移开自动收起 — Copy selected/all/file/path; auto-dismiss on leave
- **按钮提示 / Button tooltips**: 悬停显示功能说明于指针上方，可关闭 — Hover shows description above cursor; disableable
- **预览尺寸记忆 / Preview size memory**: 拖手柄或 `Ctrl+滚轮` 缩放，尺寸自动记住 — Drag handle or `Ctrl+scroll` to resize; size remembered
- **音视频尺寸分开记忆 / Separate audio/video size**: 视频与音频各自记住窗口尺寸；视频按真实宽高比 — Video/audio remember size separately; video uses real aspect ratio
- **列表顶部文件夹名 / Folder name header**: 点击/✎ 就地重命名，双击用资源管理器打开，`Ctrl+F` 展开搜索 — Click/✎ to rename in place; double-click to open in Explorer; `Ctrl+F` for search
- **行号与翻页键 / Row numbers & page keys**: 悬停行显示等宽行号；PageUp/PageDown 翻页 — Hover row shows monospace row number; PageUp/PageDown to paginate
- **文本预览编码保留 / Text encoding preservation**: 保存按原 BOM 写回（UTF-8/16/32），不丢 BOM — Save preserves original BOM (UTF-8/16/32)
- **悬停触发更精准 / Precise hover trigger**: 指针必须移动才触发；重命名/地址栏输入时不弹 — Pointer must move to trigger; suppressed during rename/address input
- **输入法友好 / IME friendly**: 输入框持焦点时暂停置顶，候选窗不被遮挡 — Pause topmost when input focused; IME candidate not occluded

## 使用 / Usage

直接运行 `HoverExplorer.exe`，程序驻留系统托盘。

Run `HoverExplorer.exe` directly; the app resides in the system tray.

右键托盘图标（或列表面板空白处）可 / Right-click tray icon (or panel blank area) to:
- 切换语言 / 主题 — Switch language / theme
- 开关资源管理器悬停 / 文件预览 / 按钮提示 / 调试日志 / 系统提示 / 开机自启 / 隐藏托盘图标 — Toggle Explorer hover / file preview / button tooltips / debug log / system tooltip / autostart / hide tray
- **设置** → 自定义快捷键、悬停触发延时、关闭延迟（改动即生效） — **Settings** → hotkey, hover delay, close delay (live)
- 退出 — Exit

面板/预览窗口：拖动标题栏移动（移动后自动固定）；点固定按钮固定；右下角手柄调整尺寸。移动窗口不改变记住的尺寸，只有拖手柄/边缘才算。

Panel/preview: drag title bar to move (auto-pins after move); pin button to pin; bottom-right handle to resize. Moving doesn't change remembered size; only handle/edge drag does.

改名：面板顶部点文件夹名或 ✎ 就地重命名（`Enter` 提交 / `Esc` 取消）。

Rename: click folder name or ✎ at panel top (`Enter` to commit / `Esc` to cancel).

搜索：点击面板激活后按 `Ctrl+F` 展开搜索框（默认隐藏）。

Search: click panel to activate, then `Ctrl+F` to show search box (hidden by default).

设置面板底部提供 GitHub Releases 与百度网盘下载链接。

Settings panel bottom has GitHub Releases and Baidu Netdisk download links.

### 可选依赖：ffmpeg / Optional dependency: ffmpeg

大部分视频用系统解码器（Media Foundation）直接播放，无需任何额外依赖。
Windows 自带解码器的实际能力边界（实测）：H.264/HEVC **只支持 8bit 4:2:0**，
`yuv420p10le` / `yuv422p` / `yuv422p10le` / ProRes（Proxy·HQ·4444）等一律打不开。
只有这些"系统解不了"的规格才需要 ffmpeg 兜底：程序自动探测常见安装位置，
探测不到时该类视频只能显示缩略图。
可在 **设置 → 预览类型 → ffmpeg 路径** 指定 ffmpeg 可执行文件位置（留空=自动探测）。

> 兜底通道只解画面、不出声；系统解码器能开的文件仍走原生通道（带声音）。
> 码流高本身不是问题 —— 42 Mbps 的 8bit 4:2:0 文件系统解码器直接就播；
> 决定生死的是像素格式与编码（ProRes / 10bit / 4:2:2）。

Most videos play through the system decoder (Media Foundation) with no extra dependency.
Windows' built-in decoder only handles 8-bit 4:2:0 for H.264/HEVC — `yuv420p10le`,
`yuv422p`, `yuv422p10le` and ProRes (Proxy/HQ/4444) all fail to open.
Only those "system can't decode" specs need ffmpeg as a fallback: the app auto-detects common
install locations, and without it those videos fall back to a thumbnail.
Set an explicit path under **Settings → Preview types → ffmpeg path** (leave empty to auto-detect).

> The fallback channel decodes picture only (no audio); files the system decoder can open still
> use the native path (with sound). High bitrate itself is not the problem — a 42 Mbps 8-bit
> 4:2:0 file opens natively; what matters is pixel format and codec.

## 快捷键 / Hotkey

默认 `Ctrl+Alt+Q`，功能为**切换悬停启用/禁用**。

Default `Ctrl+Alt+Q` — **toggle hover enable/disable**.

在托盘「设置」面板中可 / In tray **Settings**:
- 勾选/取消「启用快捷键」 — Enable/disable hotkey
- 自由组合修饰键（Ctrl / Alt / Shift） — Combine modifiers (Ctrl / Alt / Shift)
- 点击按键框后按下任意键设置主键 — Click key box then press any key
- 调整悬停触发延时（0.1–3 秒） — Adjust hover delay (0.1–3s)

快捷键配置保存在 `config.json`，重启后生效。

Hotkey config saved in `config.json`, effective after restart.

## 编译 / Build

```powershell
# 使用 .NET Framework 4.x csc.exe 编译 / Build with .NET Framework 4.x csc.exe
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
# 需引用 UIAutomationClient / UIAutomationTypes / WindowsBase / PresentationCore / System.IO.Compression.FileSystem
# 输出 HoverExplorer.exe / Output: HoverExplorer.exe
```

## 文件说明 / Files

| 文件 / File | 说明 / Description |
|------|------|
| `Program.cs` | 主循环、托盘菜单、快捷键、tooltip 隐藏 — Main loop, tray menu, hotkey, tooltip hide |
| `PopupForm.cs` | 悬停弹出面板、文件预览窗体 — Hover popup panel, preview form |
| `PreviewProvider.cs` | 各类文件预览渲染 — File preview renderers |
| `ExplorerSupport.cs` | 资源管理器 UIA 悬停检测 — Explorer UIA hover detection |
| `ExplorerUia.cs` | 名称区判定：表头查找、文字矩形/宽度估算 — Name area: header lookup, text rect/width |
| `RemoteReader.cs` | 桌面 ListView 远程读取 — Desktop ListView remote read |
| `NativeMethods.cs` | Win32 API P/Invoke — Win32 API P/Invoke |
| `Win11.cs` | DWM 毛玻璃/圆角/暗色标题区 — DWM blur/rounded corners/dark caption |
| `SettingsForm.cs` | 设置面板（即改即生效） — Settings panel (live) |
| `MediaPreview.cs` | 音视频播放、波形/频谱、SVG、自绘进度条 — Audio/video play, waveform/spectrum, SVG, progress |
| `MediaDecoder.cs` | ffmpeg 定位/视频探测/帧流兜底播放器（系统解码器打不开时） — ffmpeg lookup, video probe, frame-stream fallback player |
| `WaveDecoder.cs` | 音频全量波形：MF 解码 + 峰值/RMS — Full waveform: MF decode + peak/RMS |
| `EntryListForm.cs` | 收藏·历史列表 — Favorites · History list |
| `PanelPalette.cs` | 面板/预览窗语义配色 — Panel/preview semantic colors |
| `SlimScrollBar.cs` | 设置面板自绘细滚动条 — Settings self-drawn slim scrollbar |
| `MciAudioSession.cs` / `IMediaSession.cs` | MP3 MCI 播放会话与统一接口 — MP3 MCI session & unified interface |
| `Mp3Fixer.cs`（→ Mp3Fixer.exe） | 假 MP3 检测/批量转码工具 — Fake MP3 detect/batch transcode tool |
| `Config.cs` | 配置持久化 + 主题配色 — Config persistence + theme |
| `I18n.cs` | 7 语言国际化 — 7-language i18n |
| `PinCardForm.cs` | 固定文件桌面浮动卡片 — Pinned file desktop card |
| `CardPalette.cs` | 桌面卡片标签带配色（11 套） — Card label-band schemes (11) |
| `PaletteListForm.cs` | 滚轮切换配色的速查列表 — Scroll color cycle quick list |
| `ShellContextMenu.cs` | Windows 原生 shell 右键菜单 — Windows native shell context menu |
| `build.ps1` | 一键编译脚本（自动解析 GAC 引用） — One-click build script (auto GAC refs) |
