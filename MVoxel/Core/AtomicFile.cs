using System.Text;

namespace MVoxel.Core;

/// <summary>
/// Writes user-facing files without exposing a partially written target.
/// The temporary file stays beside the target so the final rename remains on
/// the same volume.
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void WriteText(string path, string contents, string? backupExtension = ".bak")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        var absolutePath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(absolutePath)
            ?? throw new ArgumentException("The path must include a directory.", nameof(path));
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(absolutePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            WriteAndFlush(temporaryPath, contents);
            VerifyContents(temporaryPath, contents);
            ReplaceTarget(temporaryPath, absolutePath, backupExtension);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

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

    private static void VerifyContents(string path, string expected)
    {
        var actual = File.ReadAllText(path, Utf8NoBom);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new IOException($"Atomic write verification failed for '{path}'.");
        }
    }

    private static void ReplaceTarget(string temporaryPath, string targetPath, string? backupExtension)
    {
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
