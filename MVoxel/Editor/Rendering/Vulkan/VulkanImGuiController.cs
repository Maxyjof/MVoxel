using System.Reflection;
using System.Runtime.InteropServices;
using ImGuiNET;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Common.Input;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vector2 = System.Numerics.Vector2;

namespace MVoxel.Editor.Rendering.Vulkan;

/// <summary>
/// 为 Vulkan 渲染器准备 ImGui 上下文和窗口输入
/// </summary>
internal sealed class VulkanImGuiController : IDisposable
{
    private readonly GameWindow _window;
    private readonly IntPtr _context;
    private readonly Action<TextInputEventArgs> _textInputHandler;
    private readonly Vector2 _initialScale = Vector2.One;
    private Vector2 _previousScroll;
    private bool _frameBegun;
    private bool _disposed;
    private CursorState _cursorStateBeforeOverride;
    private MouseCursor? _cursorBeforeOverride;
    private bool _cursorOverridden;

    /// <summary>
    /// 创建一个不依赖旧图形上下文的 ImGui 输入上下文
    /// </summary>
    internal VulkanImGuiController(GameWindow window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _previousScroll = new Vector2(window.MouseState.Scroll.X, window.MouseState.Scroll.Y);
        _textInputHandler = textInput =>
        {
            ImGui.SetCurrentContext(_context);
            ImGui.GetIO().AddInputCharacter((uint)textInput.Unicode);
        };
        window.TextInput += _textInputHandler;
        _context = ImGui.CreateContext();
        ImGui.SetCurrentContext(_context);
        ImGuiIOPtr io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable | ImGuiConfigFlags.NavEnableKeyboard;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset | ImGuiBackendFlags.HasMouseCursors;
        io.Fonts.AddFontDefault();
        ImGui.StyleColorsDark();
        RecreateFontDeviceTexture();
        SetPerFrameData(1F / 60F);
        ImGui.NewFrame();
        _frameBegun = true;
    }

    /// <summary>
    /// 当前 ImGui 上下文句柄
    /// </summary>
    internal IntPtr Context => _context;

    /// <summary>
    /// 当前字体图集的 RGBA 像素
    /// </summary>
    internal byte[] FontPixels { get; private set; } = [255, 255, 255, 255];

    /// <summary>
    /// 当前字体图集宽度
    /// </summary>
    internal int FontWidth { get; private set; } = 1;

    /// <summary>
    /// 当前字体图集高度
    /// </summary>
    internal int FontHeight { get; private set; } = 1;

    /// <summary>
    /// 获取 ImGui 是否请求捕获鼠标
    /// </summary>
    internal bool WantCaptureMouse => ImGui.GetIO().WantCaptureMouse;

    /// <summary>
    /// 获取 ImGui 是否请求捕获键盘
    /// </summary>
    internal bool WantCaptureKeyboard => ImGui.GetIO().WantCaptureKeyboard;

    /// <summary>
    /// 控制 ImGui 是否接管窗口光标
    /// </summary>
    internal bool ManageCursor { get; set; }

    /// <summary>
    /// 更新输入并开始新的 ImGui 帧
    /// </summary>
    internal void Update(float deltaSeconds)
    {
        ImGui.SetCurrentContext(_context);
        if (_frameBegun)
        {
            ImGui.Render();
        }

        SetPerFrameData(deltaSeconds);
        UpdateInput();
        ImGui.NewFrame();
        _frameBegun = true;
    }

    /// <summary>
    /// 结束当前 ImGui 帧并生成绘制数据
    /// </summary>
    internal void Render()
    {
        ImGui.SetCurrentContext(_context);
        if (_frameBegun)
        {
            ImGui.Render();
            _frameBegun = false;
        }
    }

    /// <summary>
    /// 重建字体图集数据供 Vulkan 纹理上传
    /// </summary>
    internal unsafe void RecreateFontDeviceTexture()
    {
        ImGui.SetCurrentContext(_context);
        ImGuiIOPtr io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out _);
        FontWidth = Math.Max(width, 1);
        FontHeight = Math.Max(height, 1);
        FontPixels = new byte[checked(FontWidth * FontHeight * 4)];
        if (pixels != IntPtr.Zero)
        {
            Marshal.Copy(pixels, FontPixels, 0, FontPixels.Length);
        }

        io.Fonts.SetTexID((IntPtr)1);
        io.Fonts.ClearTexData();
    }

    /// <summary>
    /// 加载文件字体并刷新字体图集
    /// </summary>
    internal void LoadCustomFont(string path, float size)
    {
        ImGui.SetCurrentContext(_context);
        ImGui.GetIO().Fonts.Clear();
        ImGui.GetIO().Fonts.AddFontFromFileTTF(path, size);
        RecreateFontDeviceTexture();
    }

    /// <summary>
    /// 从程序集资源加载字体
    /// </summary>
    internal bool LoadEmbeddedFont(string resourceName, float sizePixels)
    {
        Assembly assembly = typeof(VulkanImGuiController).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return false;
        }

        byte[] data = new byte[stream.Length];
        stream.ReadExactly(data);
        unsafe
        {
            fixed (byte* pointer = data)
            {
                ImGui.GetIO().Fonts.AddFontFromMemoryTTF((IntPtr)pointer, data.Length, sizePixels);
            }
        }

        RecreateFontDeviceTexture();
        return true;
    }

    /// <summary>
    /// 释放输入事件和 ImGui 上下文
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.TextInput -= _textInputHandler;
        ImGui.SetCurrentContext(_context);
        ImGui.DestroyContext(_context);
    }

    /// <summary>
    /// 更新 ImGui 的显示尺寸和时间
    /// </summary>
    private void SetPerFrameData(float deltaSeconds)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        int logicalWidth = _window.ClientSize.X;
        int logicalHeight = _window.ClientSize.Y;
        int framebufferWidth = _window.FramebufferSize.X;
        int framebufferHeight = _window.FramebufferSize.Y;
        io.DisplaySize = new Vector2(Math.Max(logicalWidth, 1), Math.Max(logicalHeight, 1));
        io.DisplayFramebufferScale = logicalWidth > 0 && logicalHeight > 0
            ? new Vector2((float)framebufferWidth / logicalWidth, (float)framebufferHeight / logicalHeight)
            : _initialScale;
        io.DeltaTime = Math.Max(deltaSeconds, 0.0001F);
    }

    /// <summary>
    /// 把 OpenTK 输入快照转换为 ImGui 输入事件
    /// </summary>
    private void UpdateInput()
    {
        ImGuiIOPtr io = ImGui.GetIO();
        MouseState mouse = _window.MouseState;
        KeyboardState keyboard = _window.KeyboardState;
        for (int index = 0; index < 5; index++)
        {
            io.MouseDown[index] = mouse[(MouseButton)index];
        }

        io.MousePos = new Vector2(mouse.X, mouse.Y);
        io.MouseWheel = (mouse.Scroll.Y - _previousScroll.Y);
        io.MouseWheelH = mouse.Scroll.X - _previousScroll.X;
        _previousScroll = new Vector2(mouse.Scroll.X, mouse.Scroll.Y);
        io.AddFocusEvent(_window.IsFocused);

        foreach (Keys key in Enum.GetValues<Keys>())
        {
            ImGuiKey imguiKey = TranslateKey(key);
            if (imguiKey != ImGuiKey.None)
            {
                io.AddKeyEvent(imguiKey, keyboard.IsKeyDown(key));
            }
        }

        io.AddKeyEvent(ImGuiKey.ModCtrl, keyboard.IsKeyDown(Keys.LeftControl) || keyboard.IsKeyDown(Keys.RightControl));
        io.AddKeyEvent(ImGuiKey.ModAlt, keyboard.IsKeyDown(Keys.LeftAlt) || keyboard.IsKeyDown(Keys.RightAlt));
        io.AddKeyEvent(ImGuiKey.ModShift, keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift));
        io.AddKeyEvent(ImGuiKey.ModSuper, keyboard.IsKeyDown(Keys.LeftSuper) || keyboard.IsKeyDown(Keys.RightSuper));
        UpdateCursor(io);
    }

    /// <summary>
    /// 根据 ImGui 状态更新窗口光标
    /// </summary>
    private void UpdateCursor(ImGuiIOPtr io)
    {
        if (!ManageCursor || io.ConfigFlags.HasFlag(ImGuiConfigFlags.NoMouseCursorChange) || !io.WantCaptureMouse)
        {
            RestoreCursor();
            return;
        }

        if (!_cursorOverridden)
        {
            _cursorOverridden = true;
            _cursorStateBeforeOverride = _window.CursorState;
            _cursorBeforeOverride = _window.Cursor;
        }

        _window.CursorState = io.MouseDrawCursor ? CursorState.Hidden : CursorState.Normal;
        if (!io.MouseDrawCursor)
        {
            _window.Cursor = MouseCursor.Default;
        }
    }

    /// <summary>
    /// 恢复窗口原始光标状态
    /// </summary>
    private void RestoreCursor()
    {
        if (!_cursorOverridden)
        {
            return;
        }

        _window.CursorState = _cursorStateBeforeOverride;
        if (_cursorBeforeOverride is not null)
        {
            _window.Cursor = _cursorBeforeOverride;
        }

        _cursorBeforeOverride = null;
        _cursorOverridden = false;
    }

    /// <summary>
    /// 把 OpenTK 按键映射到 ImGui 按键
    /// </summary>
    private static ImGuiKey TranslateKey(Keys key)
    {
        if (key is >= Keys.D0 and <= Keys.D9)
        {
            return ImGuiKey._0 + (key - Keys.D0);
        }

        if (key is >= Keys.A and <= Keys.Z)
        {
            return ImGuiKey.A + (key - Keys.A);
        }

        return key switch
        {
            Keys.Tab => ImGuiKey.Tab,
            Keys.Left => ImGuiKey.LeftArrow,
            Keys.Right => ImGuiKey.RightArrow,
            Keys.Up => ImGuiKey.UpArrow,
            Keys.Down => ImGuiKey.DownArrow,
            Keys.PageUp => ImGuiKey.PageUp,
            Keys.PageDown => ImGuiKey.PageDown,
            Keys.Home => ImGuiKey.Home,
            Keys.End => ImGuiKey.End,
            Keys.Insert => ImGuiKey.Insert,
            Keys.Delete => ImGuiKey.Delete,
            Keys.Backspace => ImGuiKey.Backspace,
            Keys.Space => ImGuiKey.Space,
            Keys.Enter => ImGuiKey.Enter,
            Keys.Escape => ImGuiKey.Escape,
            Keys.CapsLock => ImGuiKey.CapsLock,
            Keys.LeftControl => ImGuiKey.LeftCtrl,
            Keys.RightControl => ImGuiKey.RightCtrl,
            Keys.LeftShift => ImGuiKey.LeftShift,
            Keys.RightShift => ImGuiKey.RightShift,
            Keys.LeftAlt => ImGuiKey.LeftAlt,
            Keys.RightAlt => ImGuiKey.RightAlt,
            Keys.F1 => ImGuiKey.F1,
            Keys.F2 => ImGuiKey.F2,
            Keys.F3 => ImGuiKey.F3,
            Keys.F4 => ImGuiKey.F4,
            Keys.F5 => ImGuiKey.F5,
            Keys.F6 => ImGuiKey.F6,
            Keys.F7 => ImGuiKey.F7,
            Keys.F8 => ImGuiKey.F8,
            Keys.F9 => ImGuiKey.F9,
            Keys.F10 => ImGuiKey.F10,
            Keys.F11 => ImGuiKey.F11,
            Keys.F12 => ImGuiKey.F12,
            _ => ImGuiKey.None
        };
    }
}
