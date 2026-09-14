using System.Runtime.InteropServices;
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

        byte[] sourceBytes = System.Text.Encoding.UTF8.GetBytes(source);
        try
        {
            compilerApi.CompileOptionsSetTargetEnv(options, TargetEnv.Vulkan,
                (uint)(rayTracing ? EnvVersion.Vulkan12 : EnvVersion.Vulkan10));
            // 硬件 RT 扩展需要 SPIR-V 1.4，普通 Compute 保持基础目标版本
            compilerApi.CompileOptionsSetTargetSpirv(options,
                rayTracing ? SpirvVersion.Shaderc14 : SpirvVersion.Shaderc10);
            compilerApi.CompileOptionsSetOptimizationLevel(options, OptimizationLevel.Zero);
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
                    return MemoryMarshal.Cast<byte, uint>(byteSpan).ToArray();
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
}
