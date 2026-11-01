using System.Text;

namespace MVoxel.Core;

/// <summary>
/// 用户文件的原子写入工具，任何时刻磁盘上都只有完整文件或完整备份
/// 写入流程固定为：同目录临时文件、落盘、回读校验、替换目标并保留 .bak 备份
/// 绝不允许清空原文件后直接重写
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// 不带 BOM 的 UTF8 编码，所有原子文本写入共用
    /// </summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 原子写入文本文件，成功后保留上一版备份
    /// </summary>
    /// <param name="path">目标文件路径</param>
    /// <param name="contents">完整文件内容</param>
    /// <param name="backupExtension">备份后缀，传 null 表示不保留备份</param>
    public static void WriteText(string path, string contents, string? backupExtension = ".bak")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        var absolutePath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(absolutePath)
            ?? throw new ArgumentException("The path must include a directory.", nameof(path));
        Directory.CreateDirectory(directory);

        // 临时文件与目标同目录，保证最后的 Move/Replace 发生在同一卷内
        var temporaryPath = NewTemporaryPath(absolutePath);

        try
        {
            WriteAndFlush(temporaryPath, contents);
            VerifyContents(temporaryPath, contents);
            ReplaceTarget(temporaryPath, absolutePath, backupExtension);
        }
        finally
        {
            DiscardTemporary(temporaryPath);
        }
    }

    /// <summary>
    /// 原子写入二进制文件（如 .vox 与 .palette），流程与文本版一致
    /// </summary>
    /// <param name="path">目标文件路径</param>
    /// <param name="contents">完整字节内容</param>
    /// <param name="backupExtension">备份后缀，传 null 表示不保留备份</param>
    public static void WriteBytes(string path, byte[] contents, string? backupExtension = ".bak")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        var absolutePath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(absolutePath)
            ?? throw new ArgumentException("The path must include a directory.", nameof(path));
        Directory.CreateDirectory(directory);

        var temporaryPath = NewTemporaryPath(absolutePath);

        try
        {
            WriteAndFlush(temporaryPath, contents);
            VerifyBytes(temporaryPath, contents);
            ReplaceTarget(temporaryPath, absolutePath, backupExtension);
        }
        finally
        {
            DiscardTemporary(temporaryPath);
        }
    }

    /// <summary>
    /// 为超大文件开一条流式原子写入通道：返回与目标同卷的临时文件路径，
    /// 调用方自行写入并做回读校验后交 <see cref="CommitTemporary"/> 替换目标；
    /// 中途放弃用 <see cref="DiscardTemporary"/> 清理，目标文件全程保持原样
    /// </summary>
    /// <param name="path">目标文件路径</param>
    /// <returns>临时文件路径</returns>
    public static string BeginTemporary(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var absolutePath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(absolutePath)
            ?? throw new ArgumentException("The path must include a directory.", nameof(path));
        Directory.CreateDirectory(directory);
        return NewTemporaryPath(absolutePath);
    }

    /// <summary>
    /// 把已完成写入与校验的临时文件替换为目标，语义与整字节原子写入一致
    /// </summary>
    /// <param name="temporaryPath">流式写入完成的临时文件</param>
    /// <param name="path">最终目标</param>
    /// <param name="backupExtension">备份后缀或 null 表示不保留备份</param>
    public static void CommitTemporary(string temporaryPath, string path, string? backupExtension = ".bak")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            ReplaceTarget(temporaryPath, Path.GetFullPath(path), backupExtension);
        }
        finally
        {
            DiscardTemporary(temporaryPath);
        }
    }

    /// <summary>
    /// 删除尚未提交的临时文件，文件不存在时静默返回
    /// </summary>
    /// <param name="temporaryPath">临时文件路径</param>
    public static void DiscardTemporary(string temporaryPath)
    {
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }
    }

    /// <summary>
    /// 生成与目标同目录同卷的临时文件路径
    /// </summary>
    /// <param name="absoluteTargetPath">已规范化的目标绝对路径</param>
    /// <returns>临时文件路径</returns>
    private static string NewTemporaryPath(string absoluteTargetPath)
    {
        return Path.Combine(
            Path.GetDirectoryName(absoluteTargetPath)!,
            $".{Path.GetFileName(absoluteTargetPath)}.{Guid.NewGuid():N}.tmp");
    }

    /// <summary>
    /// 写入文本并强制刷盘，WriteThrough 确保校验读到的就是落盘结果
    /// </summary>
    /// <param name="path">临时文件路径</param>
    /// <param name="contents">文件内容</param>
    private static void WriteAndFlush(string path, string contents)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            options: FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream, Utf8NoBom, bufferSize: 64 * 1024, leaveOpen: true);
        writer.Write(contents);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// 写入字节并强制刷盘
    /// </summary>
    /// <param name="path">临时文件路径</param>
    /// <param name="contents">字节内容</param>
    private static void WriteAndFlush(string path, byte[] contents)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            options: FileOptions.WriteThrough);
        stream.Write(contents);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// 回读比对文本，不一致说明磁盘结果不可信，必须中止替换
    /// </summary>
    /// <param name="path">临时文件路径</param>
    /// <param name="expected">期望内容</param>
    private static void VerifyContents(string path, string expected)
    {
        var actual = File.ReadAllText(path, Utf8NoBom);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new IOException($"Atomic write verification failed for '{path}'.");
        }
    }

    /// <summary>
    /// 回读比对字节
    /// </summary>
    /// <param name="path">临时文件路径</param>
    /// <param name="expected">期望内容</param>
    private static void VerifyBytes(string path, byte[] expected)
    {
        var actual = File.ReadAllBytes(path);
        if (!actual.AsSpan().SequenceEqual(expected))
        {
            throw new IOException($"Atomic write verification failed for '{path}'.");
        }
    }

    /// <summary>
    /// 用校验通过的临时文件替换目标；目标已存在时 File.Replace 顺带生成备份
    /// </summary>
    /// <param name="temporaryPath">已校验的临时文件</param>
    /// <param name="targetPath">最终目标</param>
    /// <param name="backupExtension">备份后缀或 null</param>
    private static void ReplaceTarget(string temporaryPath, string targetPath, string? backupExtension)
    {
        FileEditHistory.BeforeWrite(targetPath);
        if (!File.Exists(targetPath))
        {
            File.Move(temporaryPath, targetPath);
            return;
        }

        var backupPath = backupExtension is null
            ? null
            : targetPath + backupExtension;
        File.Replace(temporaryPath, targetPath, backupPath, ignoreMetadataErrors: true);
    }
}
