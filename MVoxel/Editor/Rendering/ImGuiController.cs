// Derived from Bytesized.OpenTK.DearImGui (MIT), commit f3490eaf248687b25b61b7aaadefe21aa558e25b.
// See LICENSE.Bytesized.txt. MVoxel fixes: client/framebuffer sizing, per-window
// context ownership and Unicode routing, and large draw-list vertex offsets.
// 上方英文段落是上游 MIT 法律署名, 按要求保留原文不动, 本文件其余注释均为中文
﻿using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Common.Input;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vector2 = System.Numerics.Vector2;

namespace MVoxel.Editor.Rendering
{
    /// <summary>
    /// 为 ImGui.NET 与 OpenTK 的集成提供生产可用的控制器
    /// 负责 ImGui 上下文创建、输入映射、字体管理与 OpenGL 渲染
    /// 支持鼠标、键盘与手柄输入, 以及自定义字体加载和设备资源管理
    /// </summary>
    internal sealed class ImGuiController : IDisposable
    {
        /// <summary>
        /// 控制本控制器是否允许以及以何种方式修改窗口光标
        /// </summary>
        public enum CursorManagementMode
        {
            /// <summary>
            /// 永不修改 <see cref="GameWindow.CursorState"/> 或 <see cref="GameWindow.Cursor"/>, 光标行为完全由应用掌管
            /// </summary>
            Disabled = 0,

            /// <summary>
            /// 仅在 ImGui 正在捕获鼠标时修改光标, ImGui 停止捕获后恢复应用原有的光标状态
            /// </summary>
            WhenImGuiCapturesMouse = 1,

            /// <summary>
            /// 始终应用 ImGui 的光标变化(即使 ImGui 并未捕获鼠标), 对游戏而言很少是期望行为
            /// </summary>
            Always = 2
        }

        private readonly IntPtr _context;
        private bool _disposed;
        private bool _frameBegun;
        private int _vertexArray;
        private int _vertexBuffer;
        private int _vertexBufferSize;
        private int _indexBuffer;
        private int _indexBufferSize;
        private int _fontTexture;
        private int _shader;
        private int _shaderFontTextureLocation;
        private int _shaderProjectionMatrixLocation;
        private int _windowWidth;
        private int _windowHeight;
        private Vector2 _scaleFactor = Vector2.One;

        private readonly GameWindow _wnd;

        private bool _cursorOverridden;
        private CursorState _cursorStateBeforeOverride;
        private MouseCursor? _cursorBeforeOverride;

        private OpenTK.Mathematics.Vector2 _previousScroll;

        private readonly int[] _lastViewport = new int[4];
        private readonly int[] _lastScissor = new int[4];

        private static readonly Keys[] KeysToCheck;

        private readonly Action<TextInputEventArgs> _textInputHandler;

        /// <summary>
        /// 静态构造函数, 只填充一次高效的按键列表
        /// </summary>
        static ImGuiController()
        {
            List<Keys> keys = new List<Keys>();
            foreach (Keys key in Enum.GetValues<Keys>())
            {
                if (key != Keys.Unknown && TranslateKey(key) != ImGuiKey.None)
                {
                    keys.Add(key);
                }
            }
            KeysToCheck = keys.ToArray();
        }

        /// <summary>
        /// 获取一个值, 指示 ImGui 是否希望捕获鼠标输入
        /// </summary>
        public bool WantCaptureMouse => ImGui.GetIO().WantCaptureMouse;

        /// <summary>
        /// 获取一个值, 指示 ImGui 是否希望捕获键盘输入
        /// </summary>
        public bool WantCaptureKeyboard => ImGui.GetIO().WantCaptureKeyboard;

        /// <summary>
        /// 获取或设置鼠标滚轮滚动的缩放系数, 滚动过快或过慢时调整此值
        /// </summary>
        public float MouseScrollScale { get; set; } = 1.0f;

        /// <summary>
        /// 获取或设置本控制器管理窗口光标的方式
        /// </summary>
        public CursorManagementMode CursorMode { get; set; } = CursorManagementMode.Disabled;

        /// <summary>
        /// 向后兼容开关, 新代码请使用 <see cref="CursorMode"/>
        /// 设为 true 时光标管理变为 <see cref="CursorManagementMode.Always"/>
        /// 设为 false 时光标管理变为 <see cref="CursorManagementMode.WhenImGuiCapturesMouse"/>
        /// </summary>
        public bool ForceCursorState
        {
            get => CursorMode == CursorManagementMode.Always;
            set => CursorMode = value ? CursorManagementMode.Always : CursorManagementMode.WhenImGuiCapturesMouse;
        }

        /// <summary>
        /// 获取或设置控制器是否在渲染期间自动保存并恢复 OpenGL 状态
        /// (视口、裁剪区、多边形模式、混合模式)
        /// <para>默认为 true, 若自行管理状态, 设为 false 可获得性能提升</para>
        /// </summary>
        public bool AutoSaveRestoreState { get; set; } = true;

        /// <summary>
        /// 初始化 <see cref="ImGuiController"/> 的新实例, 设置 ImGui 上下文、输入事件与设备资源
        /// </summary>
        /// <param name="wnd">要绑定 ImGui 的 OpenTK GameWindow</param>
        /// <exception cref="ArgumentNullException">当 <paramref name="wnd"/> 为 null 时抛出</exception>
        public ImGuiController(GameWindow wnd)
        {
            _wnd = wnd ?? throw new ArgumentNullException(nameof(wnd));
            _windowWidth = wnd.ClientSize.X;
            _windowHeight = wnd.ClientSize.Y;

            _previousScroll = _wnd.MouseState.Scroll;

            _textInputHandler = e =>
            {
                // GLFW 会统一轮询所有窗口, 因此把 Unicode 输入路由回它自己的窗口,
                // 不论当前正在渲染的是哪个窗口或上下文
                var previous = ImGui.GetCurrentContext();
                ImGui.SetCurrentContext(_context);
                PressChar((uint)e.Unicode);
                ImGui.SetCurrentContext(previous);
            };
            _wnd.TextInput += _textInputHandler;

            _context = ImGui.CreateContext();
            ImGui.SetCurrentContext(_context);
            ImGuiIOPtr io = ImGui.GetIO();

            io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;

            io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;

            io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
            io.BackendFlags |= ImGuiBackendFlags.HasMouseCursors;
            io.BackendFlags |= ImGuiBackendFlags.HasGamepad;

            bool fontLoaded = false;
            try
            {
                fontLoaded = LoadEmbeddedFont("Roboto-Regular.ttf", 16.0f * 1.0f);
            }
            catch { }

            if (!fontLoaded)
            {
                io.Fonts.AddFontDefault();
            }

            ImGui.StyleColorsDark();

            CreateDeviceResources();
            SetPerFrameImGuiData(1f / 60f);

            ImGui.NewFrame();
            _frameBegun = true;
        }

        /// <summary>
        /// 释放全部 OpenGL 设备资源并销毁 ImGui 上下文
        /// </summary>
        public void DestroyDeviceObjects()
        {
            Dispose();
        }

        /// <summary>
        /// 创建并初始化 ImGui 渲染所需的全部 OpenGL 资源
        /// </summary>
        public void CreateDeviceResources()
        {
            _vertexArray = GL.GenVertexArray();
            _vertexBuffer = GL.GenBuffer();
            _indexBuffer = GL.GenBuffer();

            // 必须先绑定对象使其正式存在, 才能调用 ObjectLabel (GL 4.3+),
            // Gen* 只是预留了名字
            GL.BindVertexArray(_vertexArray);
            LabelObject(ObjectLabelIdentifier.VertexArray, _vertexArray, "ImGui VAO");
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
            LabelObject(ObjectLabelIdentifier.Buffer, _vertexBuffer, "ImGui VBO");
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);
            LabelObject(ObjectLabelIdentifier.Buffer, _indexBuffer, "ImGui IBO");
            GL.BindVertexArray(0);

            _vertexBufferSize = 10000;
            _indexBufferSize = 2000;

            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
            GL.BufferData(BufferTarget.ArrayBuffer, _vertexBufferSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);
            GL.BufferData(BufferTarget.ElementArrayBuffer, _indexBufferSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);

            string vertexSource = @"#version 330 core
                layout (location = 0) in vec2 Position;
                layout (location = 1) in vec2 UV;
                layout (location = 2) in vec4 Color;
                uniform mat4 ProjMtx;
                out vec2 Frag_UV;
                out vec4 Frag_Color;
                void main()
                {
                    Frag_UV = UV;
                    Frag_Color = Color;
                    gl_Position = ProjMtx * vec4(Position.xy,0,1);
                }";

            string fragmentSource = @"#version 330 core
                in vec2 Frag_UV;
                in vec4 Frag_Color;
                uniform sampler2D Texture;
                out vec4 Out_Color;
                void main()
                {
                    Out_Color = Frag_Color * texture(Texture, Frag_UV.st);
                }";

            _shader = CreateProgram("ImGui", vertexSource, fragmentSource);
            LabelObject(ObjectLabelIdentifier.Program, _shader, "ImGui Shader Program");

            _shaderProjectionMatrixLocation = GL.GetUniformLocation(_shader, "ProjMtx");
            _shaderFontTextureLocation = GL.GetUniformLocation(_shader, "Texture");

            RecreateFontDeviceTexture();
        }

        /// <summary>
        /// 重建 ImGui 使用的字体纹理并上传到 GPU
        /// </summary>
        public void RecreateFontDeviceTexture()
        {
            ImGuiIOPtr io = ImGui.GetIO();
            io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out int bytesPerPixel);

            if (_fontTexture != 0)
            {
                GL.DeleteTexture(_fontTexture);
                _fontTexture = 0;
            }
            _fontTexture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _fontTexture);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);

            LabelObject(ObjectLabelIdentifier.Texture, _fontTexture, "ImGui Font Texture");

            io.Fonts.SetTexID((IntPtr)_fontTexture);
            io.Fonts.ClearTexData();
        }

        /// <summary>
        /// 为 OpenGL 对象设置调试标签, 便于图形调试器识别
        /// </summary>
        /// <param name="objLabelIdent">OpenGL 对象标签标识</param>
        /// <param name="glObject">OpenGL 对象句柄</param>
        /// <param name="name">标签名称</param>
        private void LabelObject(ObjectLabelIdentifier objLabelIdent, int glObject, string name)
        {
            int major = GL.GetInteger(GetPName.MajorVersion);
            int minor = GL.GetInteger(GetPName.MinorVersion);

            if (major >= 4 && (major > 4 || minor >= 3))
            {
                GL.ObjectLabel(objLabelIdent, glObject, name.Length, name);
            }
        }

        /// <summary>
        /// 从文件加载自定义字体并更新 ImGui 字体图集
        /// </summary>
        /// <param name="path">.ttf 或 .otf 字体文件路径</param>
        /// <param name="size">以像素为单位的字号</param>
        public void LoadCustomFont(string path, float size)
        {
            ImGuiIOPtr io = ImGui.GetIO();
            io.Fonts.Clear();
            io.Fonts.AddFontFromFileTTF(path, size);
            RecreateFontDeviceTexture();
        }

        /// <summary>
        /// 尝试加载程序集内嵌资源中的字体
        /// </summary>
        /// <param name="resourceName">资源文件名, 例如 "MyFont.ttf"</param>
        /// <param name="sizePixels">以像素为单位的字号</param>
        /// <returns>加载成功返回 true, 否则返回 false</returns>
        public bool LoadEmbeddedFont(string resourceName, float sizePixels)
        {
            if (string.IsNullOrWhiteSpace(resourceName))
            {
                throw new ArgumentException("Resource name cannot be null or whitespace.", nameof(resourceName));
            }

            if (!float.IsFinite(sizePixels) || sizePixels <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sizePixels), sizePixels, "Font size must be finite and > 0.");
            }

            Assembly assembly = typeof(ImGuiController).Assembly;

            string[] resourceNames = assembly.GetManifestResourceNames();


            string? resourcePath = resourceNames
                .FirstOrDefault(r => r.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase));

            if (resourcePath == null)
            {
                return false;
            }

            byte[] fontData;
            using (Stream? stream = assembly.GetManifestResourceStream(resourcePath))
            {
                if (stream == null)
                {
                    return false;
                }

                if (stream.Length <= 0)
                {
                    return false;
                }

                fontData = new byte[stream.Length];
                stream.ReadExactly(fontData);
            }

            IntPtr pData = Marshal.AllocHGlobal(fontData.Length);
            Marshal.Copy(fontData, 0, pData, fontData.Length);

            unsafe
            {
                ImFontConfig* nativeConfig = ImGuiNative.ImFontConfig_ImFontConfig();

                nativeConfig->FontDataOwnedByAtlas = 1;

                nativeConfig->FontData = (void*)pData;
                nativeConfig->FontDataSize = fontData.Length;
                nativeConfig->SizePixels = sizePixels;
                nativeConfig->PixelSnapH = 1;

                ImGui.GetIO().Fonts.AddFont(nativeConfig);
            }

            return true;
        }


        /// <summary>
        /// 把 OpenTK 纹理句柄转换为 ImGui 可用的指针
        /// </summary>
        /// <param name="glTextureId">OpenGL 纹理 ID</param>
        /// <returns>供 ImGui.Image 使用的 IntPtr</returns>
        public IntPtr GetTextureHandle(int glTextureId)
        {
            return (IntPtr)glTextureId;
        }

        /// <summary>
        /// 更新 ImGui 当前帧的状态, 包括输入与时间数据
        /// </summary>
        /// <param name="dt">距上一帧的时间增量, 以秒为单位</param>
        public void Update(float dt)
        {
            ImGui.SetCurrentContext(_context);
            if (_frameBegun)
            {
                ImGui.Render();
            }
            SetPerFrameImGuiData(dt);
            UpdateImGuiInput(_wnd);
            UpdateImGuiGamepad();

            _frameBegun = true;
            ImGui.NewFrame();
        }

        /// <summary>
        /// 设置每帧 ImGui 数据, 如显示尺寸、帧缓冲缩放系数与时间增量
        /// </summary>
        /// <param name="dt">时间增量, 以秒为单位</param>
        private void SetPerFrameImGuiData(float dt)
        {
            ImGuiIOPtr io = ImGui.GetIO();

            _windowWidth = _wnd.ClientSize.X;
            _windowHeight = _wnd.ClientSize.Y;

            // ImGui 坐标绝不包含原生标题栏与边框,
            // 鼠标坐标与 DisplaySize 都是客户区局部像素
            _windowWidth = _wnd.FramebufferSize.X;
            _windowHeight = _wnd.FramebufferSize.Y;
            int logicalWidth = _wnd.ClientSize.X;
            int logicalHeight = _wnd.ClientSize.Y;

            if (logicalWidth > 0 && logicalHeight > 0)
            {
                io.DisplaySize = new Vector2(logicalWidth, logicalHeight);
                _scaleFactor = new Vector2((float)_windowWidth / logicalWidth, (float)_windowHeight / logicalHeight);
            }
            else
            {
                io.DisplaySize = new Vector2(_windowWidth, _windowHeight);
                _scaleFactor = Vector2.One;
            }

            io.DisplayFramebufferScale = _scaleFactor;
            io.DeltaTime = Math.Max(dt, 0.0001F);
        }

        /// <summary>
        /// 从当前 GameWindow 的输入更新 ImGui 输入状态, 处理鼠标、键盘、剪贴板与光标状态
        /// </summary>
        /// <param name="wnd">提供输入状态的 GameWindow</param>
        private void UpdateImGuiInput(GameWindow wnd)
        {
            ImGuiIOPtr io = ImGui.GetIO();
            MouseState mouse = wnd.MouseState;
            KeyboardState keyboard = wnd.KeyboardState;

            io.MouseDown[0] = mouse[MouseButton.Left];
            io.MouseDown[1] = mouse[MouseButton.Right];
            io.MouseDown[2] = mouse[MouseButton.Middle];
            io.MouseDown[3] = mouse[MouseButton.Button4];
            io.MouseDown[4] = mouse[MouseButton.Button5];

            Vector2i screenPoint = new Vector2i((int)mouse.X, (int)mouse.Y);
            Vector2i point = screenPoint;
            io.MousePos = new Vector2(point.X, point.Y);
            io.AddFocusEvent(wnd.IsFocused);

            io.MouseWheel = (mouse.Scroll.Y - _previousScroll.Y) * MouseScrollScale;
            io.MouseWheelH = (mouse.Scroll.X - _previousScroll.X) * MouseScrollScale;
            _previousScroll = mouse.Scroll;

            foreach (Keys key in KeysToCheck)
            {
                ImGuiKey imGuiKey = TranslateKey(key);
                io.AddKeyEvent(imGuiKey, keyboard.IsKeyDown(key));
            }

            io.AddKeyEvent(ImGuiKey.ModCtrl, keyboard.IsKeyDown(Keys.LeftControl) || keyboard.IsKeyDown(Keys.RightControl));
            io.AddKeyEvent(ImGuiKey.ModAlt, keyboard.IsKeyDown(Keys.LeftAlt) || keyboard.IsKeyDown(Keys.RightAlt));
            io.AddKeyEvent(ImGuiKey.ModShift, keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift));
            io.AddKeyEvent(ImGuiKey.ModSuper, keyboard.IsKeyDown(Keys.LeftSuper) || keyboard.IsKeyDown(Keys.RightSuper));

            if (io.KeyCtrl && !io.KeyAlt)
            {
                if (wnd.KeyboardState.IsKeyPressed(Keys.C))
                {
                    wnd.ClipboardString = ImGui.GetClipboardText();
                }

                if (wnd.KeyboardState.IsKeyPressed(Keys.V))
                {
                    ImGui.SetClipboardText(wnd.ClipboardString);
                }
            }

            UpdateCursor(wnd, io);
        }

        /// <summary>
        /// 按 CursorMode 策略应用或恢复窗口光标
        /// </summary>
        /// <param name="wnd">要设置光标的窗口</param>
        /// <param name="io">当前 ImGui IO 指针</param>
        private void UpdateCursor(GameWindow wnd, ImGuiIOPtr io)
        {
            if (io.ConfigFlags.HasFlag(ImGuiConfigFlags.NoMouseCursorChange))
            {
                RestoreCursorIfOverridden(wnd);
                return;
            }

            if (CursorMode == CursorManagementMode.Disabled)
            {
                ReleaseCursorOverrideWithoutRestoring();
                return;
            }

            if (wnd.CursorState == CursorState.Grabbed && CursorMode != CursorManagementMode.Always)
            {
                ReleaseCursorOverrideWithoutRestoring();
                return;
            }

            bool shouldApplyImGuiCursor = CursorMode == CursorManagementMode.Always
                || (CursorMode == CursorManagementMode.WhenImGuiCapturesMouse && io.WantCaptureMouse);

            if (!shouldApplyImGuiCursor)
            {
                RestoreCursorIfOverridden(wnd);
                return;
            }

            ImGuiMouseCursor imguiCursor = ImGui.GetMouseCursor();

            if (!_cursorOverridden)
            {
                _cursorOverridden = true;
                _cursorStateBeforeOverride = wnd.CursorState;
                _cursorBeforeOverride = wnd.Cursor;
            }

            if (io.MouseDrawCursor || imguiCursor == ImGuiMouseCursor.None)
            {
                wnd.CursorState = CursorState.Hidden;
                return;
            }

            wnd.CursorState = CursorState.Normal;
            wnd.Cursor = ToOpenTkCursor(imguiCursor);
        }

        /// <summary>
        /// 放弃光标覆盖记录, 但不恢复覆盖前的光标状态
        /// </summary>
        private void ReleaseCursorOverrideWithoutRestoring()
        {
            _cursorOverridden = false;
            _cursorBeforeOverride = null;
        }

        /// <summary>
        /// 若此前覆盖过光标, 则把窗口光标恢复为覆盖前的状态
        /// </summary>
        /// <param name="wnd">要恢复光标的窗口</param>
        private void RestoreCursorIfOverridden(GameWindow wnd)
        {
            if (!_cursorOverridden)
            {
                return;
            }

            wnd.CursorState = _cursorStateBeforeOverride;

            if (_cursorBeforeOverride != null)
            {
                wnd.Cursor = _cursorBeforeOverride;
            }

            _cursorOverridden = false;
            _cursorBeforeOverride = null;
        }

        /// <summary>
        /// 把 ImGui 光标类型映射为 OpenTK 鼠标光标
        /// </summary>
        /// <param name="imguiCursor">ImGui 光标类型</param>
        /// <returns>对应的 OpenTK MouseCursor</returns>
        private static MouseCursor ToOpenTkCursor(ImGuiMouseCursor imguiCursor)
        {
            return imguiCursor switch
            {
                ImGuiMouseCursor.Arrow => MouseCursor.Default,
                ImGuiMouseCursor.TextInput => MouseCursor.IBeam,
                ImGuiMouseCursor.ResizeAll => MouseCursor.Crosshair,
                ImGuiMouseCursor.ResizeNS => MouseCursor.PointingHand,
                ImGuiMouseCursor.ResizeEW => MouseCursor.PointingHand,
                ImGuiMouseCursor.Hand => MouseCursor.PointingHand,
                _ => MouseCursor.Default
            };
        }

        /// <summary>
        /// 把 0 号手柄的按键与轴状态映射为 ImGui 手柄输入
        /// </summary>
        private void UpdateImGuiGamepad()
        {
            ImGuiIOPtr io = ImGui.GetIO();
            if ((io.ConfigFlags & ImGuiConfigFlags.NavEnableGamepad) == 0) return;

            if (_wnd.JoystickStates[0] == null) return;
            JoystickState state = _wnd.JoystickStates[0];

            void MapButton(int buttonIndex, ImGuiKey key)
            {
                if (state.IsButtonDown(buttonIndex)) io.AddKeyEvent(key, true);
                else io.AddKeyEvent(key, false);
            }

            MapButton(0, ImGuiKey.GamepadFaceDown);  // A
            MapButton(1, ImGuiKey.GamepadFaceRight); // B
            MapButton(2, ImGuiKey.GamepadFaceLeft);  // X
            MapButton(3, ImGuiKey.GamepadFaceUp);    // Y
            MapButton(4, ImGuiKey.GamepadL1);        // LB
            MapButton(5, ImGuiKey.GamepadR1);        // RB
            MapButton(6, ImGuiKey.GamepadBack);      // Back
            MapButton(7, ImGuiKey.GamepadStart);     // Start
            MapButton(8, ImGuiKey.GamepadL3);        // L3
            MapButton(9, ImGuiKey.GamepadR3);        // R3
            MapButton(10, ImGuiKey.GamepadDpadUp);   // 上
            MapButton(11, ImGuiKey.GamepadDpadRight);// 右
            MapButton(12, ImGuiKey.GamepadDpadDown); // 下
            MapButton(13, ImGuiKey.GamepadDpadLeft); // 左

            float leftTrigger = state.GetAxis(4);
            float rightTrigger = state.GetAxis(5);

            io.AddKeyAnalogEvent(ImGuiKey.GamepadL2, leftTrigger > -1f, leftTrigger);
            io.AddKeyAnalogEvent(ImGuiKey.GamepadR2, rightTrigger > -1f, rightTrigger);

            io.AddKeyAnalogEvent(ImGuiKey.GamepadLStickLeft, state.GetAxis(0) < -0.1f, -state.GetAxis(0));
            io.AddKeyAnalogEvent(ImGuiKey.GamepadLStickRight, state.GetAxis(0) > 0.1f, state.GetAxis(0));
            io.AddKeyAnalogEvent(ImGuiKey.GamepadLStickUp, state.GetAxis(1) < -0.1f, -state.GetAxis(1));
            io.AddKeyAnalogEvent(ImGuiKey.GamepadLStickDown, state.GetAxis(1) > 0.1f, state.GetAxis(1));
        }

        /// <summary>
        /// 把 OpenTK 键盘按键翻译为 ImGuiKey
        /// </summary>
        /// <param name="key">OpenTK 按键</param>
        /// <returns>对应的 ImGuiKey, 未映射时返回 ImGuiKey.None</returns>
        private static ImGuiKey TranslateKey(Keys key)
        {
            if (key >= Keys.D0 && key <= Keys.D9) return ImGuiKey._0 + (key - Keys.D0);
            if (key >= Keys.A && key <= Keys.Z) return ImGuiKey.A + (key - Keys.A);
            if (key >= Keys.KeyPad0 && key <= Keys.KeyPad9) return ImGuiKey.Keypad0 + (key - Keys.KeyPad0);

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
                Keys.Apostrophe => ImGuiKey.Apostrophe,
                Keys.Comma => ImGuiKey.Comma,
                Keys.Minus => ImGuiKey.Minus,
                Keys.Period => ImGuiKey.Period,
                Keys.Slash => ImGuiKey.Slash,
                Keys.Semicolon => ImGuiKey.Semicolon,
                Keys.Equal => ImGuiKey.Equal,
                Keys.LeftBracket => ImGuiKey.LeftBracket,
                Keys.Backslash => ImGuiKey.Backslash,
                Keys.RightBracket => ImGuiKey.RightBracket,
                Keys.GraveAccent => ImGuiKey.GraveAccent,
                Keys.CapsLock => ImGuiKey.CapsLock,
                Keys.ScrollLock => ImGuiKey.ScrollLock,
                Keys.NumLock => ImGuiKey.NumLock,
                Keys.PrintScreen => ImGuiKey.PrintScreen,
                Keys.Pause => ImGuiKey.Pause,
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
                Keys.Menu => ImGuiKey.Menu,
                Keys.LeftControl => ImGuiKey.LeftCtrl,
                Keys.RightControl => ImGuiKey.RightCtrl,
                Keys.LeftShift => ImGuiKey.LeftShift,
                Keys.RightShift => ImGuiKey.RightShift,
                Keys.LeftAlt => ImGuiKey.LeftAlt,
                Keys.RightAlt => ImGuiKey.RightAlt,
                Keys.LeftSuper => ImGuiKey.LeftSuper,
                Keys.RightSuper => ImGuiKey.RightSuper,
                Keys.KeyPadEnter => ImGuiKey.KeypadEnter,
                _ => ImGuiKey.None
            };
        }

        /// <summary>
        /// 向 ImGui 添加一个 Unicode 字符输入
        /// </summary>
        /// <param name="keyChar">Unicode 字符编码</param>
        public void PressChar(uint keyChar)
        {
            ImGui.GetIO().AddInputCharacter(keyChar);
        }

        /// <summary>
        /// 把当前 ImGui 帧渲染到 OpenGL 上下文
        /// </summary>
        public void Render()
        {
            ImGui.SetCurrentContext(_context);
            if (_frameBegun)
            {
                _frameBegun = false;
                ImGui.Render();
                RenderImDrawData(ImGui.GetDrawData());
            }
        }

        /// <summary>
        /// 使用 OpenGL 渲染 ImGui 绘制数据, 处理缓冲扩容、状态管理与绘制命令执行
        /// </summary>
        /// <param name="draw_data">要渲染的 ImGui 绘制数据</param>
        private void RenderImDrawData(ImDrawDataPtr draw_data)
        {
            unsafe
            {
                if (draw_data.NativePtr == null) return;
            }

            if (draw_data.CmdListsCount == 0 || _windowWidth <= 0 || _windowHeight <= 0) return;

            int lastPolygonMode = 0;
            int lastBlendSrc = 0;
            int lastBlendDst = 0;
            int lastBlendEquationRgb = 0;
            int lastBlendEquationAlpha = 0;
            bool lastEnableBlend = false;
            bool lastEnableCullFace = false;
            bool lastEnableDepthTest = false;
            bool lastEnableScissorTest = false;

            if (AutoSaveRestoreState)
            {
                GL.GetInteger(GetPName.Viewport, _lastViewport);
                GL.GetInteger(GetPName.ScissorBox, _lastScissor);
                GL.GetInteger(GetPName.PolygonMode, out lastPolygonMode);
                GL.GetInteger(GetPName.BlendSrc, out lastBlendSrc);
                GL.GetInteger(GetPName.BlendDst, out lastBlendDst);
                GL.GetInteger(GetPName.BlendEquationRgb, out lastBlendEquationRgb);
                GL.GetInteger(GetPName.BlendEquationAlpha, out lastBlendEquationAlpha);
                lastEnableBlend = GL.IsEnabled(EnableCap.Blend);
                lastEnableCullFace = GL.IsEnabled(EnableCap.CullFace);
                lastEnableDepthTest = GL.IsEnabled(EnableCap.DepthTest);
                lastEnableScissorTest = GL.IsEnabled(EnableCap.ScissorTest);
            }

            GL.Enable(EnableCap.Blend);
            GL.BlendEquation(BlendEquationMode.FuncAdd); 
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.DepthTest);
            GL.Enable(EnableCap.ScissorTest);
            GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);

            GL.Viewport(0, 0, _windowWidth, _windowHeight);

            GL.UseProgram(_shader);
            GL.Uniform1(_shaderFontTextureLocation, 0);

            float L = draw_data.DisplayPos.X;
            float R = draw_data.DisplayPos.X + draw_data.DisplaySize.X;
            float T = draw_data.DisplayPos.Y;
            float B = draw_data.DisplayPos.Y + draw_data.DisplaySize.Y;

            Matrix4 ortho = Matrix4.CreateOrthographicOffCenter(L, R, B, T, -1.0f, 1.0f);
            GL.UniformMatrix4(_shaderProjectionMatrixLocation, false, ref ortho);

            GL.BindVertexArray(_vertexArray);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);

            draw_data.ScaleClipRects(ImGui.GetIO().DisplayFramebufferScale);

            for (int n = 0; n < draw_data.CmdListsCount; n++)
            {
                ImDrawListPtr cmd_list = draw_data.CmdLists[n];

                int vertexSize = cmd_list.VtxBuffer.Size * Unsafe.SizeOf<ImDrawVert>();
                if (vertexSize > _vertexBufferSize)
                {
                    int newSize = (int)Math.Max(_vertexBufferSize * 1.5f, vertexSize);
                    GL.BufferData(BufferTarget.ArrayBuffer, newSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
                    _vertexBufferSize = newSize;
                }

                int indexSize = cmd_list.IdxBuffer.Size * sizeof(ushort);
                if (indexSize > _indexBufferSize)
                {
                    int newSize = (int)Math.Max(_indexBufferSize * 1.5f, indexSize);
                    GL.BufferData(BufferTarget.ElementArrayBuffer, newSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
                    _indexBufferSize = newSize;
                }

                GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, vertexSize, cmd_list.VtxBuffer.Data);
                GL.BufferSubData(BufferTarget.ElementArrayBuffer, IntPtr.Zero, indexSize, cmd_list.IdxBuffer.Data);

                GL.EnableVertexAttribArray(0);
                GL.EnableVertexAttribArray(1);
                GL.EnableVertexAttribArray(2);
                GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, Unsafe.SizeOf<ImDrawVert>(), 0);
                GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, Unsafe.SizeOf<ImDrawVert>(), 8);
                GL.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, Unsafe.SizeOf<ImDrawVert>(), 16);

                int lastTextureId = -1;

                for (int cmd_i = 0; cmd_i < cmd_list.CmdBuffer.Size; cmd_i++)
                {
                    ImDrawCmdPtr pcmd = cmd_list.CmdBuffer[cmd_i];
                    if (pcmd.UserCallback != IntPtr.Zero)
                    {
                        throw new NotImplementedException();
                    }
                    else
                    {
                        if ((int)pcmd.TextureId != lastTextureId)
                        {
                            GL.ActiveTexture(TextureUnit.Texture0);
                            GL.BindTexture(TextureTarget.Texture2D, (int)pcmd.TextureId);
                            GL.ActiveTexture(TextureUnit.Texture0);
                            lastTextureId = (int)pcmd.TextureId;
                        }

                        System.Numerics.Vector4 clip = pcmd.ClipRect;
                        GL.Scissor((int)clip.X, _windowHeight - (int)clip.W, (int)(clip.Z - clip.X), (int)(clip.W - clip.Y));

                        GL.DrawElementsBaseVertex(PrimitiveType.Triangles, (int)pcmd.ElemCount,
                            DrawElementsType.UnsignedShort, (IntPtr)(pcmd.IdxOffset * sizeof(ushort)), (int)pcmd.VtxOffset);
                    }
                }
            }

            GL.BindVertexArray(0);

            if (AutoSaveRestoreState)
            {
                if (lastEnableScissorTest) GL.Enable(EnableCap.ScissorTest); else GL.Disable(EnableCap.ScissorTest);
                if (lastEnableCullFace) GL.Enable(EnableCap.CullFace); else GL.Disable(EnableCap.CullFace);
                if (lastEnableDepthTest) GL.Enable(EnableCap.DepthTest); else GL.Disable(EnableCap.DepthTest);
                if (lastEnableBlend) GL.Enable(EnableCap.Blend); else GL.Disable(EnableCap.Blend);

                GL.PolygonMode(TriangleFace.FrontAndBack, (PolygonMode)lastPolygonMode);
                GL.BlendEquationSeparate((BlendEquationMode)lastBlendEquationRgb, (BlendEquationMode)lastBlendEquationAlpha);
                GL.BlendFunc((BlendingFactor)lastBlendSrc, (BlendingFactor)lastBlendDst);
                GL.Viewport(_lastViewport[0], _lastViewport[1], _lastViewport[2], _lastViewport[3]);
                GL.Scissor(_lastScissor[0], _lastScissor[1], _lastScissor[2], _lastScissor[3]);
            }
        }

        /// <summary>
        /// 从顶点与片元着色器源码编译并链接一个新的 OpenGL 着色器程序
        /// </summary>
        /// <param name="name">程序名称, 用于日志</param>
        /// <param name="vertexSource">顶点着色器源码</param>
        /// <param name="fragmentSource">片元着色器源码</param>
        /// <returns>OpenGL 程序句柄</returns>
        private int CreateProgram(string name, string vertexSource, string fragmentSource)
        {
            int program = GL.CreateProgram();
            int vs = CompileShader(name, ShaderType.VertexShader, vertexSource);
            int fs = CompileShader(name, ShaderType.FragmentShader, fragmentSource);

            GL.AttachShader(program, vs);
            GL.AttachShader(program, fs);
            GL.LinkProgram(program);

            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int success);
            if (success == 0)
            {
                string info = GL.GetProgramInfoLog(program);
                Console.Error.WriteLine($"Error [ImGuiController]: GL.LinkProgram for {name} failed: {info}");
            }

            GL.DeleteShader(vs);
            GL.DeleteShader(fs);
            return program;
        }

        /// <summary>
        /// 从源码编译一个 OpenGL 着色器
        /// </summary>
        /// <param name="name">着色器名称, 用于日志</param>
        /// <param name="type">着色器类型</param>
        /// <param name="source">着色器源码</param>
        /// <returns>OpenGL 着色器句柄</returns>
        private int CompileShader(string name, ShaderType type, string source)
        {
            int shader = GL.CreateShader(type);
            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);

            GL.GetShader(shader, ShaderParameter.CompileStatus, out int success);
            if (success == 0)
            {
                string info = GL.GetShaderInfoLog(shader);
                Console.Error.WriteLine($"Error [ImGuiController]: GL.CompileShader for {name} failed: {info}");
            }

            return shader;
        }

        /// <summary>
        /// 检查 OpenGL 错误, 并在 DEBUG 构建下把错误输出到控制台
        /// </summary>
        /// <param name="title">错误信息的前缀标题</param>
        [Conditional("DEBUG")]
        public static void CheckGLError(string title)
        {
            OpenTK.Graphics.OpenGL4.ErrorCode error;
            while ((error = GL.GetError()) != OpenTK.Graphics.OpenGL4.ErrorCode.NoError)
            {
                Console.WriteLine($"Error [ImGuiController]: {title}: {error}");
            }
        }

        /// <summary>
        /// 释放全部 OpenGL 资源并把 ImGui 从窗口上解绑
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var previousContext = ImGui.GetCurrentContext();
            ImGui.SetCurrentContext(_context);
            RestoreCursorIfOverridden(_wnd);

            GL.DeleteVertexArray(_vertexArray);
            GL.DeleteBuffer(_vertexBuffer);
            GL.DeleteBuffer(_indexBuffer);
            GL.DeleteTexture(_fontTexture);
            GL.DeleteProgram(_shader);

            ImGui.DestroyContext(_context);
            ImGui.SetCurrentContext(previousContext == _context ? IntPtr.Zero : previousContext);

            _wnd.TextInput -= _textInputHandler;
            GC.SuppressFinalize(this);
        }
    }
}
