using System.Diagnostics;
using System.Globalization;
using MVoxel.Core;

namespace MVoxel.Editor;

/// <summary>在独立进程中把帧序列编码到临时文件，成功后才提交最终视频</summary>
internal static class VideoEncoder
{
    internal static async Task EncodeAsync(string framesDirectory, string outputPath, int startFrame,
        int frameCount, int frameRate, CancellationToken cancellation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startFrame);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameCount);
        if (frameRate is < 1 or > 240) throw new ArgumentOutOfRangeException(nameof(frameRate));
        var encoder = Path.Combine(AppContext.BaseDirectory, "mvoxel_video_encoder.exe");
        if (!File.Exists(encoder)) throw new FileNotFoundException("缺少视频编码程序，请重新编译或修复 MVoxel", encoder);
        var finalPath = Path.GetFullPath(outputPath);
        if (File.Exists(finalPath)) throw new IOException("目标视频已存在，请选择其他文件名");
        var directory = Path.GetDirectoryName(finalPath) ?? throw new IOException("视频输出路径无效");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileNameWithoutExtension(finalPath)}-{Guid.NewGuid():N}.partial.mp4");
        var start = new ProcessStartInfo(encoder)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.GetFullPath(framesDirectory));
        start.ArgumentList.Add(temporaryPath);
        start.ArgumentList.Add(startFrame.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(frameCount.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(frameRate.ToString(CultureInfo.InvariantCulture));
        //目标路径在编码期间被别人占用时保留临时成片，交给用户自行改名，避免白跑一遍编码
        var keepTemporary = false;
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("无法启动视频编码程序");
            var errors = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                try { await errors.ConfigureAwait(false); } catch (Exception) { }
                throw;
            }
            var detail = (await errors.ConfigureAwait(false)).Trim();
            if (process.ExitCode != 0)
                throw new IOException(string.IsNullOrWhiteSpace(detail)
                    ? $"视频编码失败（退出代码 {process.ExitCode}）"
                    : $"视频编码失败（退出代码 {process.ExitCode}）：{detail}");
            cancellation.ThrowIfCancellationRequested();
            //只有确认临时成片存在且非空才提交，绝不把空文件或半成品当结果
            var produced = new FileInfo(temporaryPath);
            if (!produced.Exists) throw new IOException("视频编码没有生成输出文件");
            if (produced.Length <= 0) throw new IOException("视频编码生成的文件为空");
            try
            {
                //不覆盖已有用户文件
                File.Move(temporaryPath, finalPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                keepTemporary = true;
                throw new IOException($"目标视频在编码期间被占用，成片已保留在：{temporaryPath}");
            }
            catch (UnauthorizedAccessException)
            {
                keepTemporary = true;
                throw new IOException($"无法写入目标视频，成片已保留在：{temporaryPath}");
            }
        }
        catch
        {
            //失败或取消时不留下半成品；完整帧序列保留在帧目录，可再次导出
            if (!keepTemporary) TryDeleteFile(temporaryPath);
            throw;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MVoxelLog.Error($"删除半成品视频失败：{exception.Message}");
        }
    }
}
