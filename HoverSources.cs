using System;
using System.Drawing;

namespace HoverExplorer
{
    // 悬停检测的统一签名：返回目标路径（文件夹或文件），并回传触发矩形 / 是否目录
    internal delegate string HoverDetect(int mx, int my, out Rectangle rect, out bool isDir);

    // 一个"场景"：桌面 / 文件对话框 / Total Commander / 资源管理器 / Perch / 通用 UIA 列表
    // 新场景只需要实现这个接口并把实例加进 Program 的 _hoverSources 列表
    internal interface IHoverSource
    {
        string Id { get; }
        bool Enabled { get; }
        string Detect(int mx, int my, out Rectangle rect, out bool isDir);
    }

    internal sealed class HoverSource : IHoverSource
    {
        private readonly Func<bool> _enabled;
        private readonly HoverDetect _detect;

        internal HoverSource(string id, Func<bool> enabled, HoverDetect detect)
        {
            Id = id;
            _enabled = enabled;
            _detect = detect;
        }

        public string Id { get; private set; }

        // null 表示场景常驻（如桌面）
        public bool Enabled { get { return _enabled == null || _enabled(); } }

        public string Detect(int mx, int my, out Rectangle rect, out bool isDir)
        {
            return _detect(mx, my, out rect, out isDir);
        }
    }

    internal static class HoverSources
    {
        // 最近一次命中的场景 id（设置里的"场景诊断"用）
        internal static string LastSource = "";
    }
}
