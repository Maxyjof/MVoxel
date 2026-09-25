using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MVoxel.Core;
using Silk.NET.Shaderc;

namespace MVoxel.Editor.Rendering.Vulkan;

/// <summary>
/// Vulkan GLSL 到 SPIR-V 的运行时编译器
/// </summary>
internal static unsafe class VulkanShaderCompiler
{
    /// <summary>
    /// 把 GLSL 源码编译为 Vulkan 可加载的 SPIR-V 字节码
    /// </summary>
    internal static uint[] Compile(string source, ShaderKind shaderKind, string sourceName, bool rayTracing = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        byte[] sourceBytes = Encoding.UTF8.GetBytes(source);
        var cachePath = GetCachePath(sourceBytes, sourceName, shaderKind, rayTracing);
        if (TryReadCache(cachePath, out var cached))
        {
            MVoxelLog.Info($"Vulkan Shader SPIR-V 缓存命中：{sourceName}");
            return cached;
        }

        var watch = Stopwatch.StartNew();
        Shaderc compilerApi = Shaderc.GetApi();
        Compiler* compiler = compilerApi.CompilerInitialize();
        if (compiler is null)
        {
            throw new InvalidOperationException("无法创建 shaderc 编译器");
        }

        CompileOptions* options = compilerApi.CompileOptionsInitialize();
        if (options is null)
        {
            compilerApi.CompilerRelease(compiler);
            throw new InvalidOperationException("无法创建 shaderc 编译选项");
        }

        try
        {
            compilerApi.CompileOptionsSetTargetEnv(options, TargetEnv.Vulkan,
                (uint)(rayTracing ? EnvVersion.Vulkan12 : EnvVersion.Vulkan10));
            // 硬件 RT 扩展需要 SPIR-V 1.4，普通 Compute 保持基础目标版本
            compilerApi.CompileOptionsSetTargetSpirv(options,
                rayTracing ? SpirvVersion.Shaderc14 : SpirvVersion.Shaderc10);
            compilerApi.CompileOptionsSetOptimizationLevel(options, OptimizationLevel.Performance);
            fixed (byte* sourcePointer = sourceBytes)
            {
                CompilationResult* result = compilerApi.CompileIntoSpv(
                    compiler,
                    sourcePointer,
                    (nuint)sourceBytes.Length,
                    shaderKind,
                    sourceName,
                    "main",
                    options);
                if (result is null)
                {
                    throw new InvalidOperationException($"shaderc 没有返回编译结果：{sourceName}");
                }

                try
                {
                    CompilationStatus status = compilerApi.ResultGetCompilationStatus(result);
                    if (status != CompilationStatus.Success)
                    {
                        string message = compilerApi.ResultGetErrorMessageS(result) ?? "未知 shaderc 错误";
                        throw new InvalidOperationException($"Vulkan Shader 编译失败 {sourceName}：{message}");
                    }

                    nuint byteLength = compilerApi.ResultGetLength(result);
                    byte* resultBytes = compilerApi.ResultGetBytes(result);
                    if (resultBytes is null || byteLength == 0 || byteLength % sizeof(uint) != 0)
                    {
                        throw new InvalidOperationException($"Vulkan Shader 编译结果无效：{sourceName}");
                    }

                    var byteSpan = new ReadOnlySpan<byte>(resultBytes, checked((int)byteLength));
                    var words = MemoryMarshal.Cast<byte, uint>(byteSpan).ToArray();
                    TryWriteCache(cachePath, words);
                    MVoxelLog.Info($"Vulkan Shader 编译完成：{sourceName}，{watch.ElapsedMilliseconds}ms");
                    return words;
                }
                finally
                {
                    compilerApi.ResultRelease(result);
                }
            }
        }
        finally
        {
            compilerApi.CompileOptionsRelease(options);
            compilerApi.CompilerRelease(compiler);
        }
    }

    private static string GetCachePath(byte[] sourceBytes, string sourceName, ShaderKind shaderKind, bool rayTracing)
    {
        var sourceHash = Convert.ToHexString(SHA256.HashData(sourceBytes));
        var compilerVersion = typeof(Shaderc).Assembly.GetName().Version?.ToString() ?? "unknown";
        var identity = $"MVoxel-SPIRV-v1|{compilerVersion}|{shaderKind}|{rayTracing}|{sourceName}|{sourceHash}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(AppPaths.ShaderCacheDirectory, key + ".spv");
    }

    private static bool TryReadCache(string path, out uint[] words)
    {
        words = [];
        try
        {
            if (!File.Exists(path)) return false;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < sizeof(uint) || bytes.Length % sizeof(uint) != 0 ||
                MemoryMarshal.Read<uint>(bytes) != 0x07230203U)
                return false;

            words = MemoryMarshal.Cast<byte, uint>(bytes).ToArray();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryWriteCache(string path, uint[] words)
    {
        try
        {
            AtomicFile.WriteBytes(path, MemoryMarshal.AsBytes(words.AsSpan()).ToArray(), backupExtension: null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MVoxelLog.Warn($"无法保存 Vulkan Shader 缓存：{exception.Message}");
        }
    }
}
