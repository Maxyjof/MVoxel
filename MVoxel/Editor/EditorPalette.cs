using System.Drawing;

namespace MVoxel.Editor;

/// <summary>
/// 编辑器界面的统一配色表，全部为 sRGB 字节色，
/// 交给 ImGui 前统一转成 0 到 1 浮点
/// </summary>
internal static class EditorPalette
{
    /// <summary>窗口底色</summary>
    internal static readonly Color Window = Color.FromArgb(255, 18, 23, 30);

    /// <summary>面板底色</summary>
    internal static readonly Color Panel = Color.FromArgb(255, 34, 40, 50);

    /// <summary>面板标题与分组条底色</summary>
    internal static readonly Color PanelHeader = Color.FromArgb(255, 48, 58, 73);

    /// <summary>输入控件底色</summary>
    internal static readonly Color Input = Color.FromArgb(255, 22, 28, 37);

    /// <summary>描边色</summary>
    internal static readonly Color Border = Color.FromArgb(255, 105, 122, 144);

    /// <summary>主强调色（选中、焦点）</summary>
    internal static readonly Color Accent = Color.FromArgb(255, 93, 187, 255);

    /// <summary>柔和强调色（悬停底色）</summary>
    internal static readonly Color AccentSoft = Color.FromArgb(255, 40, 92, 130);

    /// <summary>正文色</summary>
    internal static readonly Color Text = Color.FromArgb(255, 245, 247, 250);

    /// <summary>次要文字色</summary>
    internal static readonly Color MutedText = Color.FromArgb(255, 189, 199, 212);

    /// <summary>图标色</summary>
    internal static readonly Color Icon = Color.FromArgb(255, 220, 235, 247);

    /// <summary>成功状态色</summary>
    internal static readonly Color Success = Color.FromArgb(255, 99, 215, 145);

    /// <summary>错误状态色</summary>
    internal static readonly Color Error = Color.FromArgb(255, 255, 112, 120);
}
