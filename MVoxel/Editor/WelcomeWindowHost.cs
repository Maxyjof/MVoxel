using ImGuiNET;
using MVoxel.Editor.Rendering;
using MVoxel.Editor.Rendering.Vulkan;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace MVoxel.Editor;

/// <summary>独立的欢迎窗口宿主，项目加载成功后由主编辑器关闭。</summary>
internal sealed class ImGuiWelcomeWindow : GameWindow
{
    private readonly IEditorWelcomeHost _editor;
    private readonly ImGuiWelcomeContent _content;
    private VulkanImGuiController? _controller;
    private VulkanWindowContext? _vulkan;
    private VulkanImGuiRenderer? _renderer;
    private IntPtr _imguiContext;
    private int _closeRequested;
    private int _closeAllowed;

    internal ImGuiWelcomeWindow(IEditorWelcomeHost editor)
        : base(GameWindowSettings.Default, new NativeWindowSettings
        {
            Title = "欢迎使用 MVoxel",
            ClientSize = new Vector2i(780, 620),
            MinimumClientSize = new Vector2i(680, 520),
            WindowBorder = WindowBorder.Resizable,
            StartVisible = true,
            StartFocused = true,
            API = ContextAPI.NoAPI,
            Vsync = VSyncMode.On,
            Icon = AppBranding.LoadWindowIcon(),
        })
    {
        _editor = editor;
        _content = new ImGuiWelcomeContent(editor);
    }

    internal bool IsOpen => !IsExiting && Volatile.Read(ref _closeRequested) == 0;

    internal void Initialize()
    {
        _vulkan = new VulkanWindowContext(this);
        _controller = new VulkanImGuiController(this);
        _imguiContext = ImGui.GetCurrentContext();
        _renderer = new VulkanImGuiRenderer(_vulkan, _controller);
        _content.Initialize(_renderer, new EditorFontCatalog());
        _content.ConfigureForHost(_controller, UiDiagnostics.Dpi(this));
    }

    internal void RequestClose()
    {
        Interlocked.Exchange(ref _closeAllowed, 1);
        Interlocked.Exchange(ref _closeRequested, 1);
    }

    internal bool PumpFrame(double deltaSeconds)
    {
        if (!IsOpen) return false;
        ProcessEvents(0D);
        if (!IsOpen) return false;
        ImGui.SetCurrentContext(_imguiContext);
        _controller?.Update((float)Math.Max(deltaSeconds, 1.0 / 120.0));
        _content.Draw();
        _controller?.Render();
        _vulkan?.RenderFrame(new System.Numerics.Vector4(0.05F, 0.07F, 0.10F, 1F), commandBuffer =>
        {
            if (_renderer is not null && _controller is not null) _renderer.Record(commandBuffer, _controller);
        });
        return IsOpen;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs args)
    {
        if (Volatile.Read(ref _closeAllowed) == 0)
        {
            _editor.CloseEditor();
            args.Cancel = true;
            return;
        }
        base.OnClosing(args);
    }

    protected override void OnUnload()
    {
        _content.Dispose();
        _renderer?.Dispose();
        _vulkan?.Dispose();
        _controller?.Dispose();
        _renderer = null;
        _vulkan = null;
        _controller = null;
        base.OnUnload();
    }
}
