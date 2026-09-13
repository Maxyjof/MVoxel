// Derived from Bytesized.OpenTK.DearImGui (MIT), commit f3490eaf248687b25b61b7aaadefe21aa558e25b.
// See LICENSE.Bytesized.txt. MVoxel fixes: client/framebuffer sizing, per-window
// context ownership and Unicode routing, and large draw-list vertex offsets.
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
    /// Provides a production-ready controller for integrating ImGui.NET with OpenTK.
    /// Handles ImGui context creation, input mapping, font management, and OpenGL rendering.
    /// Supports mouse, keyboard, and gamepad input, as well as custom font loading and device resource management.
    /// </summary>
    internal sealed class ImGuiController : IDisposable
    {
        /// <summary>
        /// Controls how (or if) this controller is allowed to modify the window cursor.
        /// </summary>
        public enum CursorManagementMode
        {
            /// <summary>
            /// Never changes <see cref="GameWindow.CursorState"/> or <see cref="GameWindow.Cursor"/>.
            /// The application fully owns cursor behavior.
            /// </summary>
            Disabled = 0,

            /// <summary>
            /// Only changes cursor while ImGui is actively capturing the mouse.
            /// Restores the application cursor state once ImGui stops capturing.
            /// </summary>
            WhenImGuiCapturesMouse = 1,

            /// <summary>
            /// Always applies ImGui cursor changes (even if ImGui is not capturing).
            /// This is rarely desirable for games.
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
        /// Static constructor to populate the efficient key list once.
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
        /// Gets a value indicating whether ImGui wants to capture mouse input.
        /// </summary>
        public bool WantCaptureMouse => ImGui.GetIO().WantCaptureMouse;

        /// <summary>
        /// Gets a value indicating whether ImGui wants to capture keyboard input.
        /// </summary>
        public bool WantCaptureKeyboard => ImGui.GetIO().WantCaptureKeyboard;

        /// <summary>
        /// Gets or sets the scale factor for mouse wheel scrolling.
        /// Adjust this value if scrolling is too fast or too slow.
        /// </summary>
        public float MouseScrollScale { get; set; } = 1.0f;

        /// <summary>
        /// Gets or sets how this controller manages the window cursor.
        /// </summary>
        public CursorManagementMode CursorMode { get; set; } = CursorManagementMode.Disabled;

        /// <summary>
        /// Back-compat switch. Use <see cref="CursorMode"/> for new code.
        /// If set to true, cursor management becomes <see cref="CursorManagementMode.Always"/>.
        /// If set to false, cursor management becomes <see cref="CursorManagementMode.WhenImGuiCapturesMouse"/>.
        /// </summary>
        public bool ForceCursorState
        {
            get => CursorMode == CursorManagementMode.Always;
            set => CursorMode = value ? CursorManagementMode.Always : CursorManagementMode.WhenImGuiCapturesMouse;
        }

        /// <summary>
        /// Gets or sets whether the controller should automatically save and restore OpenGL state 
        /// (viewport, scissor, polygon mode, blend mode) during rendering.
        /// <para>Defaults to true. Set to false for a performance boost if you handle state management yourself.</para>
        /// </summary>
        public bool AutoSaveRestoreState { get; set; } = true;

        /// <summary>
        /// Initializes a new instance of the <see cref="ImGuiController"/> class.
        /// Sets up ImGui context, input events, and device resources.
        /// </summary>
        /// <param name="wnd">The OpenTK GameWindow to bind ImGui to.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="wnd"/> is null.</exception>
        public ImGuiController(GameWindow wnd)
        {
            _wnd = wnd ?? throw new ArgumentNullException(nameof(wnd));
            _windowWidth = wnd.ClientSize.X;
            _windowHeight = wnd.ClientSize.Y;

            _previousScroll = _wnd.MouseState.Scroll;

            _textInputHandler = e =>
            {
                // GLFW pumps all windows together. Route Unicode to its owner,
                // regardless of which window/context is currently rendering.
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
        /// Releases all OpenGL device resources and destroys the ImGui context.
        /// </summary>
        public void DestroyDeviceObjects()
        {
            Dispose();
        }

        /// <summary>
        /// Creates and initializes all required OpenGL resources for ImGui rendering.
        /// </summary>
        public void CreateDeviceResources()
        {
            _vertexArray = GL.GenVertexArray();
            _vertexBuffer = GL.GenBuffer();
            _indexBuffer = GL.GenBuffer();

            // Objects must be bound into existence before ObjectLabel (GL 4.3+);
            // Gen* only reserves the name.
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
        /// Recreates the font texture used by ImGui and uploads it to the GPU.
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
        /// Sets a debug label for an OpenGL object, useful for graphics debuggers.
        /// </summary>
        /// <param name="objLabelIdent">The OpenGL object label identifier.</param>
        /// <param name="glObject">The OpenGL object handle.</param>
        /// <param name="name">The label name.</param>
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
        /// Loads a custom font from file and updates the ImGui font atlas.
        /// </summary>
        /// <param name="path">Path to the .ttf or .otf font file.</param>
        /// <param name="size">Font size in pixels.</param>
        public void LoadCustomFont(string path, float size)
        {
            ImGuiIOPtr io = ImGui.GetIO();
            io.Fonts.Clear();
            io.Fonts.AddFontFromFileTTF(path, size);
            RecreateFontDeviceTexture();
        }

        /// <summary>
        /// Attempts to load a font embedded in the assembly as a resource.
        /// </summary>
        /// <param name="resourceName">The filename of the resource (e.g. "MyFont.ttf")</param>
        /// <param name="sizePixels">The font size.</param>
        /// <returns>True if loaded successfully, false otherwise.</returns>
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
        /// Returns an ImGui-compatible pointer for an OpenTK texture handle.
        /// </summary>
        /// <param name="glTextureId">The OpenGL texture ID.</param>
        /// <returns>An IntPtr for use with ImGui.Image.</returns>
        public IntPtr GetTextureHandle(int glTextureId)
        {
            return (IntPtr)glTextureId;
        }

        /// <summary>
        /// Updates ImGui state for the current frame, including input and timing.
        /// </summary>
        /// <param name="dt">Delta time in seconds since the last frame.</param>
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
        /// Sets per-frame ImGui data such as display size, framebuffer scale, and delta time.
        /// </summary>
        /// <param name="dt">Delta time in seconds.</param>
        private void SetPerFrameImGuiData(float dt)
        {
            ImGuiIOPtr io = ImGui.GetIO();

            _windowWidth = _wnd.ClientSize.X;
            _windowHeight = _wnd.ClientSize.Y;

            // Never include the native title bar/borders in ImGui coordinates.
            // Mouse coordinates and DisplaySize are both client-local pixels.
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
        /// Updates ImGui input state from the current GameWindow input.
        /// Handles mouse, keyboard, clipboard, and cursor state.
        /// </summary>
        /// <param name="wnd">The GameWindow providing input state.</param>
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

        private void ReleaseCursorOverrideWithoutRestoring()
        {
            _cursorOverridden = false;
            _cursorBeforeOverride = null;
        }

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
            MapButton(10, ImGuiKey.GamepadDpadUp);   // Up
            MapButton(11, ImGuiKey.GamepadDpadRight);// Right
            MapButton(12, ImGuiKey.GamepadDpadDown); // Down
            MapButton(13, ImGuiKey.GamepadDpadLeft); // Left

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
        /// Translates OpenTK keyboard keys to ImGuiKey values.
        /// </summary>
        /// <param name="key">The OpenTK key.</param>
        /// <returns>The corresponding ImGuiKey, or ImGuiKey.None if not mapped.</returns>
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
        /// Adds a Unicode character input to ImGui.
        /// </summary>
        /// <param name="keyChar">The Unicode character code.</param>
        public void PressChar(uint keyChar)
        {
            ImGui.GetIO().AddInputCharacter(keyChar);
        }

        /// <summary>
        /// Renders the current ImGui frame to the OpenGL context.
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
        /// Renders ImGui draw data using OpenGL.
        /// Handles buffer resizing, state management, and draw command execution.
        /// </summary>
        /// <param name="draw_data">The ImGui draw data to render.</param>
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
        /// Compiles and links a new OpenGL shader program from vertex and fragment source code.
        /// </summary>
        /// <param name="name">The program name (for logging).</param>
        /// <param name="vertexSource">Vertex shader source code.</param>
        /// <param name="fragmentSource">Fragment shader source code.</param>
        /// <returns>The OpenGL program handle.</returns>
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
        /// Compiles an OpenGL shader from source code.
        /// </summary>
        /// <param name="name">The shader name (for logging).</param>
        /// <param name="type">The shader type.</param>
        /// <param name="source">The shader source code.</param>
        /// <returns>The OpenGL shader handle.</returns>
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
        /// Checks for OpenGL errors and logs them to the console in DEBUG builds.
        /// </summary>
        /// <param name="title">A title to prefix error messages.</param>
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
        /// Disposes all OpenGL resources and detaches ImGui from the window.
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
