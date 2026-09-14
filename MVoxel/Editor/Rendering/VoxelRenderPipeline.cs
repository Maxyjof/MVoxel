using System.Numerics;
using OpenTK.Graphics.OpenGL4;
using MVoxel.Core;
using MVoxel.Rendering;
using MVoxel.Voxels;

namespace MVoxel.Editor.Rendering;

/// <summary>
/// GPU 体素视口渲染管线，管理 RGBA8 与 R8UI 区域工作集
/// 工作集是覆盖稀疏世界的区块式 3D 纹理窗口，同时管理 HDR 双缓冲累积目标和 ImGui 使用的 LDR 显示纹理
/// 
/// 所有通道都使用全屏片元写入 FBO 颜色附件
/// 这是针对当前驱动栈的验证结果，因为 compute imageStore 在该驱动上会静默丢失
/// SSBO 参数块和 3D 纹理采样器在片元阶段可以稳定工作，因此不改变光追核心
/// 
/// 窗口优先请求 OpenGL 4.5
/// 驱动不支持时 <see cref="IsAvailable"/> 为 false，编辑器回退到 CPU 预览
/// </summary>
internal sealed class VoxelRenderPipeline : IDisposable
{
    private const int MaximumRegionExtent = 512;
    private const int MaximumEmissiveLights = 512;
    private const int VoxelColorUnit = 5;
    private const int VoxelFlagUnit = 6;
    private const int AuxSamplerUnit = 7;

    private int _sceneProgram;
    private int _traceProgram;
    private int _postProgram;
    private int _quadVao;
    private int _paramsBuffer;
    private int _lightsBuffer;
    private int _colorsTexture;
    private int _flagsTexture;
    private int _displayTexture;
    private int _displayFbo;
    private readonly int[] _accumTextures = new int[2];
    private readonly int[] _accumFbos = new int[2];
    private int _accumWrite;
    private int _displayWidth;
    private int _displayHeight;
    private int _accumWidth;
    private int _accumHeight;
    private ulong _frameCounter;

    private int _uploadedVersion = -1;
    private int _regionMinX, _regionMinY, _regionMinZ;
    private int _regionSizeX, _regionSizeY, _regionSizeZ;
    private int _emissiveLightCount;
    private float[] _lightData = Array.Empty<float>();
    private int _maxSteps;
    private int _sceneColorSampler = -1;
    private int _sceneFlagSampler = -1;
    private int _traceColorSampler = -1;
    private int _traceFlagSampler = -1;
    private int _tracePreviousAccumSampler = -1;
    private int _postAccumSampler = -1;

    internal VoxelRenderPipeline()
    {
        var major = GL.GetInteger(GetPName.MajorVersion);
        var minor = GL.GetInteger(GetPName.MinorVersion);
        IsAvailable = major > 4 || (major == 4 && minor >= 5);
        if (!IsAvailable) return;

        _quadVao = GL.GenVertexArray();
        GL.BindVertexArray(_quadVao);
        GL.BindVertexArray(0);
        _paramsBuffer = GL.GenBuffer();
        _lightsBuffer = GL.GenBuffer();
        if (string.Equals(Environment.GetEnvironmentVariable("MVOXEL_UI_SHADER_PROBE"), "1", StringComparison.Ordinal))
            ProbeCommonShader();
        _sceneProgram = CompileProgram(VoxelGpuShaders.SceneFragment, "scene");
        _traceProgram = CompileProgram(VoxelGpuShaders.TraceFragment, "trace");
        _postProgram = CompileProgram(VoxelGpuShaders.PostFragment, "post");
        CacheSamplerLocations();
        IsAvailable = _sceneProgram != 0 && _traceProgram != 0 && _postProgram != 0;
    }

    internal bool IsAvailable { get; private set; }
    internal IntPtr DisplayTextureId => (IntPtr)_displayTexture;
    internal int AccumulatedSamples { get; private set; }
    internal string? ShaderLog => _lastShaderLog;
    internal VoxelRenderParameters.Vector3I RegionMin => new(_regionMinX, _regionMinY, _regionMinZ);
    internal VoxelRenderParameters.Vector3I RegionSize => new(_regionSizeX, _regionSizeY, _regionSizeZ);
    internal int StepBudget => _maxSteps;
    internal int EmissiveLightCount => _emissiveLightCount;
    internal bool Truncated { get; private set; }
    private string? _lastShaderLog;

    private static void ProbeCommonShader()
    {
        var output = Environment.GetEnvironmentVariable("MVOXEL_UI_CAPTURE_DIR");
        var preambles = new[]
        {
            "",
            "layout(std430, binding = 0) readonly buffer MVParams { float U[]; };",
            "layout(std430, binding = 0) readonly buffer MVParams { float U[]; };\nlayout(std430, binding = 1) readonly buffer MVLights { vec4 MVLight[]; };",
            "layout(std430, binding = 0) readonly buffer MVParams { float U[]; };\nlayout(std430, binding = 1) readonly buffer MVLights { vec4 MVLight[]; };\nuniform sampler3D uColors;\nuniform usampler3D uFlags;",
            "layout(std430, binding = 0) readonly buffer MVParams { float U[]; };\nlayout(std430, binding = 1) readonly buffer MVLights { vec4 MVLight[]; };\nuniform sampler3D uColors;\nuniform usampler3D uFlags;\nconst float PI = 3.141592653589793;",
        };
        for (var index = 0; index < preambles.Length; index++)
            ProbeShaderSource($"shader-preamble-{index:00}",
                $"#version 450\n{preambles[index]}\nlayout(location = 0) out vec4 FragColor; void main(){{ FragColor = vec4(1.0); }}\n", output);

        var common = VoxelGpuShaders.BuildCommon();
        var markers = new[]
        {
            "// ImGui 在四边形顶边采样纹理第 0 行",
            "// ===== 色彩空间 =====",
            "// ===== 区域采样 =====",
            "// ===== Amanatides 与 Woo 体素遍历 =====",
            "// ===== 地面平面(y = 0) =====",
            "// ===== MagicaVoxel 风格角点 AO =====",
            "// ===== 相机 =====",
            "// ===== BSDF 组件 =====",
            "// ===== 色调曲线与呈现 =====",
        };

        for (var index = 0; index <= markers.Length; index++)
        {
            var end = index == markers.Length ? common.Length : common.IndexOf(markers[index], StringComparison.Ordinal);
            if (end < 0) continue;
            ProbeShaderSource($"shader-common-{index:00}",
                "#version 450\n" + common[..end] +
                "\nlayout(location = 0) out vec4 FragColor; void main(){ FragColor = vec4(1.0); }\n", output);
        }
    }

    private static void ProbeShaderSource(string label, string source, string? output)
    {
        var shader = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(shader, DriverSafeShaderSource(source));
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out var status);
        var log = GL.GetShaderInfoLog(shader);
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, $"{label}.frag"), source);
            File.WriteAllText(Path.Combine(output, $"{label}.log"), $"status={status}\n{log}");
        }

        GL.DeleteShader(shader);
    }

    /// <summary>诊断模式下检查指定渲染阶段的 OpenGL 错误</summary>
    private static void CheckStage(string stage)
    {
        if (!UiDiagnostics.Enabled) return;
        var error = GL.GetError();
        if (error != ErrorCode.NoError)
        {
            throw new InvalidOperationException($"GL error after {stage}: {error}");
        }
    }

    internal bool RenderSceneView(RenderSettings settings, in VoxelRenderParameters.FrameState frameState,
        int widthPixels, int heightPixels)
    {
        if (!IsAvailable) return false;
        EnsureDisplay(widthPixels, heightPixels);
        var parameters = VoxelRenderParameters.Build(settings, frameState with
        {
            FrameIndex = (int)(_frameCounter & 0x3FFFFFFF),
            PathMode = false,
        }, _frameCounter & 0xFFFF);
        UploadParameters(parameters);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _displayFbo);
        GL.Viewport(0, 0, widthPixels, heightPixels);
        GL.UseProgram(_sceneProgram);
        BindVoxelSamplers(_sceneProgram);
        GL.BindVertexArray(_quadVao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        GL.BindVertexArray(0);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.UseProgram(0);
        CheckStage("scene pass");
        _frameCounter++;
        return true;
    }

    internal void ResetAccumulation()
    {
        AccumulatedSamples = 0;
        foreach (var fbo in _accumFbos)
        {
            if (fbo == 0) continue;
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.ClearColor(0F, 0F, 0F, 1F);
            GL.Clear(ClearBufferMask.ColorBufferBit);
        }
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _accumWrite = 0;
    }

    /// <summary>
    /// 执行一次渐进式累积，将每帧新增样本与上一轮结果混合到双缓冲目标
    /// 随后通过呈现通道把 HDR 累积结果映射到 LDR 显示纹理
    /// </summary>
    internal bool RenderPathView(RenderSettings settings, in VoxelRenderParameters.FrameState frameState,
        int displayWidthPixels, int displayHeightPixels)
    {
        if (!IsAvailable) return false;
        var internalWidth = Math.Max(16, (int)(displayWidthPixels * Math.Clamp(settings.ResolutionScale, 0.25F, 1F)));
        var internalHeight = Math.Max(16, (int)(displayHeightPixels * Math.Clamp(settings.ResolutionScale, 0.25F, 1F)));
        EnsureDisplay(displayWidthPixels, displayHeightPixels);
        EnsureAccum(internalWidth, internalHeight);

        if (AccumulatedSamples < settings.PathMaxAccumulatedSamples)
        {
            var parameters = VoxelRenderParameters.Build(settings, frameState with
            {
                FrameIndex = (int)(_frameCounter & 0x3FFFFFFF),
                ViewWidthPixels = internalWidth,
                ViewHeightPixels = internalHeight,
                PathMode = true,
                AccumulatedSamples = AccumulatedSamples,
            }, _frameCounter & 0xFFFF);
            UploadParameters(parameters);

            var read = 1 - _accumWrite;
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _accumFbos[_accumWrite]);
            GL.Viewport(0, 0, internalWidth, internalHeight);
            GL.UseProgram(_traceProgram);
            BindVoxelSamplers(_traceProgram);
            GL.ActiveTexture(TextureUnit.Texture0 + AuxSamplerUnit);
            GL.BindTexture(TextureTarget.Texture2D, _accumTextures[read]);
            GL.ActiveTexture(TextureUnit.Texture0);
            if (_tracePreviousAccumSampler >= 0) GL.Uniform1(_tracePreviousAccumSampler, AuxSamplerUnit);
            GL.BindVertexArray(_quadVao);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            GL.BindVertexArray(0);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _accumWrite = read; // 下一帧读取本帧刚写入的结果
            AccumulatedSamples += Math.Clamp(settings.PathSamplesPerFrame, 1, 8);
            CheckStage("trace pass");
        }

        Present(settings, frameState, displayWidthPixels, displayHeightPixels);
        _frameCounter++;
        return true;
    }

    /// <summary>只执行呈现通道，使用当前后期参数重新映射已有 HDR 累积结果</summary>
    internal bool Present(RenderSettings settings, in VoxelRenderParameters.FrameState frameState,
        int displayWidthPixels, int displayHeightPixels)
    {
        if (!IsAvailable || _accumTextures[0] == 0) return false;
        EnsureDisplay(displayWidthPixels, displayHeightPixels);
        var parameters = VoxelRenderParameters.Build(settings, frameState with
        {
            FrameIndex = (int)(_frameCounter & 0x3FFFFFFF),
            PathMode = true,
        }, _frameCounter & 0xFFFF);
        UploadParameters(parameters);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _displayFbo);
        GL.Viewport(0, 0, displayWidthPixels, displayHeightPixels);
        GL.UseProgram(_postProgram);
        GL.ActiveTexture(TextureUnit.Texture0 + AuxSamplerUnit);
        // 路径通道渲染后会交换 _accumWrite
        // 最新累积结果位于索引 1 - _accumWrite 的缓冲
        GL.BindTexture(TextureTarget.Texture2D, _accumTextures[1 - _accumWrite]);
        GL.ActiveTexture(TextureUnit.Texture0);
        if (_postAccumSampler >= 0) GL.Uniform1(_postAccumSampler, AuxSamplerUnit);
        GL.BindVertexArray(_quadVao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        GL.BindVertexArray(0);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.UseProgram(0);
        CheckStage("present pass");
        return true;
    }

    /// <summary>
    /// 世界内容变化时刷新 GPU 工作集
    /// 场景超过区域上限时返回 false，让编辑器显示范围提示
    /// </summary>
    internal bool SyncWorld(VoxelWorld world)
    {
        if (!IsAvailable || world is null) return false;
        if (world.ContentVersion == _uploadedVersion) return !Truncated;

        if (!world.TryGetBounds(out var min, out var max))
        {
            min = max = VoxelCoord64.Zero;
        }

        // 预留边界，保证 AO 与阴影邻域查询仍在工作集窗口内
        const int margin = 2;
        var extentX = (int)Math.Clamp(max.X - min.X + 2L * margin + 1, 8, MaximumRegionExtent);
        var extentY = (int)Math.Clamp(max.Y - min.Y + 2L * margin + 1, 8, MaximumRegionExtent);
        var extentZ = (int)Math.Clamp(max.Z - min.Z + 2L * margin + 1, 8, MaximumRegionExtent);
        Truncated = max.X - min.X + 1 > MaximumRegionExtent ||
                    max.Y - min.Y + 1 > MaximumRegionExtent ||
                    max.Z - min.Z + 1 > MaximumRegionExtent;

        var sizeX = NextPowerOfTwo(extentX);
        var sizeY = NextPowerOfTwo(extentY);
        var sizeZ = NextPowerOfTwo(extentZ);

        _regionMinX = (int)(min.X - margin);
        _regionMinY = (int)(min.Y - margin);
        _regionMinZ = (int)(min.Z - margin);
        var needsReallocation = sizeX != _regionSizeX || sizeY != _regionSizeY || sizeZ != _regionSizeZ
                                || _colorsTexture == 0;
        _regionSizeX = sizeX;
        _regionSizeY = sizeY;
        _regionSizeZ = sizeZ;

        var colors = new byte[sizeX * sizeY * sizeZ * 4];
        var flags = new byte[sizeX * sizeY * sizeZ];
        var lights = new List<(Vector3 Center, float Intensity, Rgba32 Color)>();
        foreach (var (coordinate, value) in world.EnumerateOccupied())
        {
            var x = (int)(coordinate.X - _regionMinX);
            var y = (int)(coordinate.Y - _regionMinY);
            var z = (int)(coordinate.Z - _regionMinZ);
            if ((uint)x >= sizeX || (uint)y >= sizeY || (uint)z >= sizeZ) continue;
            var cell = x + sizeX * (y + sizeY * z);
            var offset = cell * 4;
            colors[offset] = value.Color.R;
            colors[offset + 1] = value.Color.G;
            colors[offset + 2] = value.Color.B;
            colors[offset + 3] = value.Color.A;
            flags[cell] = (byte)((value.Flags | 1) & 0xFF);
            if (VoxelFlags.Has(value.Flags, VoxelFlags.Emissive) && lights.Count < MaximumEmissiveLights)
            {
                lights.Add((new Vector3(coordinate.X + 0.5F, coordinate.Y + 0.5F, coordinate.Z + 0.5F), 1F, value.Color));
            }
        }

        _emissiveLightCount = lights.Count;
        var lightData = new float[Math.Max(1, lights.Count) * 8];
        for (var index = 0; index < lights.Count; index++)
        {
            var (center, intensity, color) = lights[index];
            var base8 = index * 8;
            lightData[base8] = center.X;
            lightData[base8 + 1] = center.Y;
            lightData[base8 + 2] = center.Z;
            lightData[base8 + 3] = intensity;
            lightData[base8 + 4] = color.R / 255F;
            lightData[base8 + 5] = color.G / 255F;
            lightData[base8 + 6] = color.B / 255F;
            lightData[base8 + 7] = 0F;
        }

        _lightData = lightData;
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _lightsBuffer);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, Math.Max(16, lightData.Length * 4), lightData, BufferUsageHint.DynamicDraw);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);

        if (needsReallocation)
        {
            if (_colorsTexture != 0)
            {
                GL.DeleteTexture(_colorsTexture);
                GL.DeleteTexture(_flagsTexture);
            }

            _colorsTexture = GL.GenTexture();
            _flagsTexture = GL.GenTexture();
        }

        GL.BindTexture(TextureTarget.Texture3D, _colorsTexture);
        if (needsReallocation)
        {
            GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.Rgba, sizeX, sizeY, sizeZ, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, colors);
        }
        else
        {
            GL.TexSubImage3D(TextureTarget.Texture3D, 0, 0, 0, 0, sizeX, sizeY, sizeZ,
                PixelFormat.Rgba, PixelType.UnsignedByte, colors);
        }
        ConfigureVolumeSampler();
        GL.BindTexture(TextureTarget.Texture3D, _flagsTexture);
        if (needsReallocation)
        {
            GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R8ui, sizeX, sizeY, sizeZ, 0,
                PixelFormat.RedInteger, PixelType.UnsignedByte, flags);
        }
        else
        {
            GL.TexSubImage3D(TextureTarget.Texture3D, 0, 0, 0, 0, sizeX, sizeY, sizeZ,
                PixelFormat.RedInteger, PixelType.UnsignedByte, flags);
        }
        ConfigureVolumeSampler();
        GL.BindTexture(TextureTarget.Texture3D, 0);

        var diagonal = MathF.Sqrt(sizeX * sizeX + sizeY * sizeY + sizeZ * sizeZ);
        _maxSteps = (int)Math.Clamp(diagonal * 3F + 64F, 128F, 400_000F);
        _uploadedVersion = world.ContentVersion;
        CheckStage("SyncWorld upload");
        return !Truncated;
    }

    internal bool SaveDisplayPng(string path)
    {
        if (!IsAvailable || _displayTexture == 0 || _displayWidth <= 0) return false;
        var framebuffer = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, framebuffer);
        GL.FramebufferTexture2D(FramebufferTarget.ReadFramebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _displayTexture, 0);
        var status = (int)GL.CheckFramebufferStatus(FramebufferTarget.ReadFramebuffer);
        if (status != (int)FramebufferStatus.FramebufferComplete)
        {
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
            GL.DeleteFramebuffer(framebuffer);
            return false;
        }

        var width = _displayWidth;
        var height = _displayHeight;
        var pixels = new byte[width * height * 4];
        GL.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        GL.DeleteFramebuffer(framebuffer);
        WritePng(path, width, height, pixels);
        return true;
    }

    private static void WritePng(string path, int width, int height, byte[] rgbaTopDown)
    {
        // 显示纹理第 0 行对应画面顶部，ImGui 在四边形顶边使用 v=0 采样，因此复制行时不翻转
        // System.Drawing 这里只负责 PNG 编码，不参与界面绘制，也不引入 WinForms
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var region = new System.Drawing.Rectangle(0, 0, width, height);
        var data = bitmap.LockBits(region, System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[width * 4];
            for (var targetRow = 0; targetRow < height; targetRow++)
            {
                var sourceRow = targetRow;
                for (var x = 0; x < width; x++)
                {
                    var sourceOffset = (sourceRow * width + x) * 4;
                    var targetOffset = x * 4;
                    row[targetOffset] = rgbaTopDown[sourceOffset + 2];
                    row[targetOffset + 1] = rgbaTopDown[sourceOffset + 1];
                    row[targetOffset + 2] = rgbaTopDown[sourceOffset];
                    row[targetOffset + 3] = rgbaTopDown[sourceOffset + 3];
                }

                System.Runtime.InteropServices.Marshal.Copy(row, 0, data.Scan0 + targetRow * data.Stride, row.Length);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    public void Dispose()
    {
        if (_sceneProgram != 0) GL.DeleteProgram(_sceneProgram);
        if (_traceProgram != 0) GL.DeleteProgram(_traceProgram);
        if (_postProgram != 0) GL.DeleteProgram(_postProgram);
        if (_quadVao != 0) GL.DeleteVertexArray(_quadVao);
        if (_paramsBuffer != 0) GL.DeleteBuffer(_paramsBuffer);
        if (_lightsBuffer != 0) GL.DeleteBuffer(_lightsBuffer);
        if (_colorsTexture != 0) GL.DeleteTexture(_colorsTexture);
        if (_flagsTexture != 0) GL.DeleteTexture(_flagsTexture);
        if (_displayTexture != 0) GL.DeleteTexture(_displayTexture);
        if (_displayFbo != 0) GL.DeleteFramebuffer(_displayFbo);
        for (var index = 0; index < 2; index++)
        {
            if (_accumTextures[index] != 0) GL.DeleteTexture(_accumTextures[index]);
            if (_accumFbos[index] != 0) GL.DeleteFramebuffer(_accumFbos[index]);
        }
    }

    // ---------- internals ----------

    private void EnsureDisplay(int width, int height)
    {
        if (_displayTexture != 0 && _displayWidth == width && _displayHeight == height) return;
        if (_displayTexture != 0) GL.DeleteTexture(_displayTexture);
        if (_displayFbo != 0) GL.DeleteFramebuffer(_displayFbo);
        _displayTexture = GL.GenTexture();
        _displayWidth = width;
        _displayHeight = height;
        GL.BindTexture(TextureTarget.Texture2D, _displayTexture);
        GL.TexStorage2D(TextureTarget2d.Texture2D, 1, SizedInternalFormat.Rgba8, width, height);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        _displayFbo = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _displayFbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _displayTexture, 0);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private void EnsureAccum(int width, int height)
    {
        if (_accumTextures[0] != 0 && _accumWidth == width && _accumHeight == height) return;
        for (var index = 0; index < 2; index++)
        {
            if (_accumTextures[index] != 0) GL.DeleteTexture(_accumTextures[index]);
            if (_accumFbos[index] != 0) GL.DeleteFramebuffer(_accumFbos[index]);
            _accumTextures[index] = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _accumTextures[index]);
            GL.TexStorage2D(TextureTarget2d.Texture2D, 1, SizedInternalFormat.Rgba16f, width, height);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            _accumFbos[index] = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _accumFbos[index]);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, _accumTextures[index], 0);
            GL.ClearColor(0F, 0F, 0F, 1F);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        _accumWidth = width;
        _accumHeight = height;
        _accumWrite = 0;
        AccumulatedSamples = 0;
    }

    private void BindVoxelSamplers(int program)
    {
        GL.ActiveTexture(TextureUnit.Texture0 + VoxelColorUnit);
        GL.BindTexture(TextureTarget.Texture3D, _colorsTexture);
        GL.ActiveTexture(TextureUnit.Texture0 + VoxelFlagUnit);
        GL.BindTexture(TextureTarget.Texture3D, _flagsTexture);
        GL.ActiveTexture(TextureUnit.Texture0);
        var colors = program == _sceneProgram ? _sceneColorSampler : _traceColorSampler;
        var flags = program == _sceneProgram ? _sceneFlagSampler : _traceFlagSampler;
        if (colors >= 0) GL.Uniform1(colors, VoxelColorUnit);
        if (flags >= 0) GL.Uniform1(flags, VoxelFlagUnit);
    }

    private void CacheSamplerLocations()
    {
        if (_sceneProgram != 0)
        {
            _sceneColorSampler = GL.GetUniformLocation(_sceneProgram, "uColors");
            _sceneFlagSampler = GL.GetUniformLocation(_sceneProgram, "uFlags");
        }

        if (_traceProgram != 0)
        {
            _traceColorSampler = GL.GetUniformLocation(_traceProgram, "uColors");
            _traceFlagSampler = GL.GetUniformLocation(_traceProgram, "uFlags");
            _tracePreviousAccumSampler = GL.GetUniformLocation(_traceProgram, "uPrevAccum");
        }

        if (_postProgram != 0)
        {
            _postAccumSampler = GL.GetUniformLocation(_postProgram, "uAccum");
        }
    }

    private void UploadParameters(float[] parameters)
    {
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _paramsBuffer);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, parameters.Length * 4, parameters, BufferUsageHint.DynamicDraw);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, _paramsBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, _lightsBuffer);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
    }

    private void ConfigureVolumeSampler()
    {
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
    }

    private int CompileProgram(string fragmentSource, string label)
    {
        var vertex = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(vertex, DriverSafeShaderSource(VoxelGpuShaders.Vertex));
        GL.CompileShader(vertex);
        var fragment = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(fragment, DriverSafeShaderSource(VoxelGpuShaders.Compose(fragmentSource)));
        GL.CompileShader(fragment);
        GL.GetShader(fragment, ShaderParameter.CompileStatus, out var fragmentStatus);
        GL.GetShader(vertex, ShaderParameter.CompileStatus, out var vertexStatus);
        if (fragmentStatus == 0 || vertexStatus == 0)
        {
            var failureShader = fragmentStatus == 0 ? fragment : vertex;
            _lastShaderLog = $"{label}: {GL.GetShaderInfoLog(failureShader)}";
            DumpOnFailure(label, VoxelGpuShaders.Compose(fragmentSource), GL.GetShaderInfoLog(failureShader));
            GL.DeleteShader(vertex);
            GL.DeleteShader(fragment);
            return 0;
        }

        var program = GL.CreateProgram();
        GL.AttachShader(program, vertex);
        GL.AttachShader(program, fragment);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var linkStatus);
        if (linkStatus == 0)
        {
            _lastShaderLog = $"{label} link: {GL.GetProgramInfoLog(program)}";
            GL.DeleteProgram(program);
            GL.DeleteShader(vertex);
            GL.DeleteShader(fragment);
            return 0;
        }

        GL.DetachShader(program, vertex);
        GL.DetachShader(program, fragment);
        GL.DeleteShader(vertex);
        GL.DeleteShader(fragment);
        return program;
    }

    /// <summary>
    /// 某些 Windows 驱动栈通过 ANSI 路径接收 OpenTK 的 GLSL 字符串，
    /// 中文注释在进入驱动时会被截断，表现为文件末尾 EOF 语法错误。
    /// 注释不参与 shader 语义，因此只把送入驱动的副本规整为 ASCII；
    /// DumpOnFailure 仍保存完整原文，方便定位真实的 shader 内容
    /// </summary>
    private static string DriverSafeShaderSource(string source)
    {
        var safe = new char[source.Length];
        for (var index = 0; index < source.Length; index++)
        {
            var character = source[index];
            safe[index] = character <= 0x7F || character is '\r' or '\n' or '\t' ? character : ' ';
        }

        return new string(safe);
    }

    private void DumpOnFailure(string label, string source, string? log = null)
    {
        try
        {
            var dumpDir = Environment.GetEnvironmentVariable("MVOXEL_UI_CAPTURE_DIR");
            if (!string.IsNullOrEmpty(dumpDir))
            {
                Directory.CreateDirectory(dumpDir);
                File.WriteAllText(Path.Combine(dumpDir, $"shader-{label}.frag"), source);
                if (log is not null) File.WriteAllText(Path.Combine(dumpDir, $"shader-{label}.log"), log);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static int NextPowerOfTwo(int value)
    {
        var power = 1;
        while (power < value && power < MaximumRegionExtent) power <<= 1;
        return Math.Min(power, MaximumRegionExtent);
    }
}
