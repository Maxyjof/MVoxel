using System.Security.Cryptography;

namespace MVoxel.Core;

/// <summary>按实际写入路径保存磁盘前后版本；大资产流式复制，不装入内存。</summary>
internal sealed class FileEditHistory(Func<string, bool> accepts)
{
    private readonly Func<string, bool> _accepts = accepts;
    [ThreadStatic] private static FileEditHistory? _current;
    private readonly Dictionary<string, Version> _before = new(StringComparer.OrdinalIgnoreCase);
    internal IDisposable Activate()
    {
        var previous = _current;
        _current = this;
        return new Scope(() => _current = previous);
    }
    internal static IDisposable Suspend()
    {
        var previous = _current;
        _current = null;
        return new Scope(() => _current = previous);
    }
    internal static void BeforeWrite(string path)
    {
        path = Path.GetFullPath(path);
        if (_current is { } current && current._accepts(path) && !current._before.ContainsKey(path))
            current._before.Add(path, Version.Capture(path));
    }
    internal static void Delete(string path) { BeforeWrite(path); File.Delete(path); }
    internal static void Move(string source, string target)
    {
        BeforeWrite(source); BeforeWrite(target); File.Move(source, target);
    }
    internal static void CreateDirectory(string path)
    {
        BeforeWrite(path); Directory.CreateDirectory(path);
    }
    internal IEditorCommand? Complete()
    {
        var entries = new List<Entry>();
        try
        {
            foreach (var (path, before) in _before) entries.Add(new Entry(path, before, Version.Capture(path)));
        }
        catch { foreach (var entry in entries) entry.After.Dispose(); throw; }
        _before.Clear();
        entries.RemoveAll(entry =>
        {
            if (entry.Before.Hash != entry.After.Hash || entry.Before.Directory != entry.After.Directory) return false;
            entry.Before.Dispose(); entry.After.Dispose(); return true;
        });
        return entries.Count == 0 ? null : new FileCommand(entries);
    }
    internal void Reset()
    {
        foreach (var version in _before.Values) version.Dispose();
        _before.Clear();
    }
    private sealed class Scope(Action dispose) : IDisposable { public void Dispose() => dispose(); }
    private sealed record Entry(string Path, Version Before, Version After);
    private sealed record Version(string? Backup, string? Hash, bool Directory) : IDisposable
    {
        internal static Version Capture(string path)
        {
            if (!File.Exists(path)) return new(null, null, System.IO.Directory.Exists(path));
            var backup = Path.Combine(Path.GetTempPath(), "MVoxel-History", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(path, backup);
            using var stream = File.OpenRead(backup);
            return new(backup, Convert.ToHexString(SHA256.HashData(stream)), false);
        }
        internal bool Matches(string path)
        {
            if (Hash is null) return !File.Exists(path) && System.IO.Directory.Exists(path) == Directory;
            if (!File.Exists(path)) return false;
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)) == Hash;
        }
        internal void Apply(string path)
        {
            if (Directory) { System.IO.Directory.CreateDirectory(path); return; }
            if (Backup is null)
            {
                if (File.Exists(path)) File.Delete(path);
                // 不递归删除目录：外部新增内容必须保留。
                else if (System.IO.Directory.Exists(path) && !System.IO.Directory.EnumerateFileSystemEntries(path).Any())
                    System.IO.Directory.Delete(path);
                return;
            }
            var temporary = AtomicFile.BeginTemporary(path);
            try { File.Copy(Backup, temporary); AtomicFile.CommitTemporary(temporary, path); }
            finally { AtomicFile.DiscardTemporary(temporary); }
        }
        public void Dispose()
        {
            try { if (Backup is not null && File.Exists(Backup)) File.Delete(Backup); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { MVoxelLog.Error("无法清理撤销历史的临时副本", exception); }
        }
    }
    private sealed class FileCommand(List<Entry> entries) : IEditorCommand, IDisposable, IHistoryWarning
    {
        public string Name => "资产与文件修改";
        public string? GetWarning(bool forward)
        {
            var affected = entries.Where(entry => (forward ? entry.Before : entry.After).Hash is not null).ToArray();
            if (affected.Length == 0) return null;
            return $"此操作会删除或覆盖 {affected.Length} 个文件（包含资产及其元数据）。\n" +
                string.Join("\n", affected.Take(8).Select(entry => entry.Path)) +
                (affected.Length > 8 ? "\n……" : "");
        }
        public void Validate(bool forward)
        {
            foreach (var entry in entries)
            {
                // 撤销新建目录时保留了后来加入的内容，重做可以复用该目录。
                if (entry.Before.Hash is null && entry.After.Hash is null && Directory.Exists(entry.Path)) continue;
                if (!(forward ? entry.Before : entry.After).Matches(entry.Path))
                    throw new IOException($"文件已有后续修改，未覆盖：{entry.Path}");
            }
        }
        public void Execute() => Apply(true);
        public void Undo() => Apply(false);
        private void Apply(bool forward)
        {
            using var suspended = Suspend();
            Validate(forward);
            var completed = new List<Entry>();
            try
            {
                foreach (var entry in forward ? entries : entries.AsEnumerable().Reverse())
                {
                    (forward ? entry.After : entry.Before).Apply(entry.Path);
                    completed.Add(entry);
                }
            }
            catch
            {
                foreach (var entry in completed.AsEnumerable().Reverse())
                    (forward ? entry.Before : entry.After).Apply(entry.Path);
                throw;
            }
        }
        public void Dispose()
        {
            foreach (var entry in entries) { entry.Before.Dispose(); entry.After.Dispose(); }
        }
    }
}
