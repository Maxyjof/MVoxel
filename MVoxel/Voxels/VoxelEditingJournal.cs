using MVoxel.Core;
using System.Text.Json;

namespace MVoxel.Voxels;

/// <summary>
/// 体素编辑事务日志：把每笔描边命令的逐格前后差异追加写入恢复区，
/// 崩溃后在恢复区快照之上重放即可还原未落盘的编辑。
/// 撤销与重做也按方向写入转换记录，重放整份日志与实际操作序列逐格一致
/// </summary>
public sealed class VoxelEditingJournal
{
    /// <summary>日志条目数上限，超出时由调用方触发快照并清空日志</summary>
    public const int MaximumEntries = 4096;

    private readonly string _path;
    private int _entryCount;

    /// <summary>
    /// 创建事务日志
    /// </summary>
    /// <param name="journalFilePath">日志文件路径（恢复区目录内）</param>
    public VoxelEditingJournal(string journalFilePath)
    {
        _path = journalFilePath;
    }

    /// <summary>
    /// 当前未落盘的事务条数
    /// </summary>
    public int EntryCount => _entryCount;

    /// <summary>
    /// 追加一笔正向转换（执行或重做）
    /// </summary>
    /// <param name="name">命令显示名</param>
    /// <param name="edits">逐格前后差异</param>
    public void Append(string name, IReadOnlyList<(VoxelCoord64 Coordinate, VoxelValue Before, VoxelValue After)> edits)
    {
        AppendTransition(name, edits, inverted: false);
    }

    /// <summary>
    /// 追加一笔逆向转换（撤销），前后值互换后写入
    /// </summary>
    /// <param name="edits">被撤销命令的逐格差异</param>
    public void AppendInverse(string name, IReadOnlyList<(VoxelCoord64 Coordinate, VoxelValue Before, VoxelValue After)> edits)
    {
        AppendTransition(name, edits, inverted: true);
    }

    /// <summary>
    /// 清空日志：快照已捕获当前状态或上下文已切换时调用
    /// </summary>
    public void Clear()
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
            _entryCount = 0;
        }
        catch (IOException)
        {
            // 日志清理失败不影响编辑，只会在恢复时多重放几笔已含于快照的操作
        }
    }

    private void AppendTransition(string name,
        IReadOnlyList<(VoxelCoord64 Coordinate, VoxelValue Before, VoxelValue After)> edits, bool inverted)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            using var writer = new StreamWriter(_path, append: true);
            writer.WriteLine(JsonSerializer.Serialize(
                new JournalLine(name, edits.Select(edit => new JournalEdit(
                    edit.Coordinate.X, edit.Coordinate.Y, edit.Coordinate.Z,
                    ToRecord(edit.Before), ToRecord(edit.After))).ToArray(),
                inverted ? 1 : 0), JsonOptions));
            _entryCount++;
        }
        catch (IOException)
        {
            // 日志写入失败不影响编辑本身，恢复时只是少重放若干笔
        }
    }

    private static long[]? ToRecord(VoxelValue value) => value.Color.A == 0 && value.MaterialSlot == 0
        ? null
        : [value.Color.R, value.Color.G, value.Color.B, value.Color.A, value.MaterialSlot, value.Flags];

    private static VoxelValue FromRecord(long[]? record) => record is { Length: 6 }
        ? new VoxelValue(
            new Rgba32(
                (byte)Math.Clamp(record[0], 0, 255),
                (byte)Math.Clamp(record[1], 0, 255),
                (byte)Math.Clamp(record[2], 0, 255),
                (byte)Math.Clamp(record[3], 0, 255)),
            (ushort)Math.Clamp(record[4], 0, ushort.MaxValue),
            (byte)Math.Clamp(record[5], 0, 255))
        : default;

    /// <summary>
    /// 读取日志条目；损坏行受控跳过，绝不抛异常中断恢复
    /// </summary>
    /// <param name="journalFilePath">日志文件路径</param>
    /// <returns>按时间顺序的转换列表</returns>
    public static IReadOnlyList<JournalTransition> TryRead(string journalFilePath)
    {
        var transitions = new List<JournalTransition>();
        try
        {
            if (!File.Exists(journalFilePath)) return transitions;
            foreach (var line in File.ReadLines(journalFilePath))
            {
                try
                {
                    var lineData = JsonSerializer.Deserialize<JournalLine>(line, JsonOptions);
                    if (lineData is null || lineData.Edits.Length == 0) continue;
                    transitions.Add(new JournalTransition(
                        lineData.Inverted == 1,
                        lineData.Edits.Select(edit => (new VoxelCoord64(edit.X, edit.Y, edit.Z),
                            FromRecord(edit.Before), FromRecord(edit.After))).ToArray()));
                }
                catch (JsonException)
                {
                    // 单行损坏只影响该行，其余照常重放
                }
            }
        }
        catch (IOException)
        {
            // 日志不可读时按空日志处理
        }

        return transitions;
    }

    /// <summary>
    /// 把日志转换序列重放到世界：正向转换写 After 值，逆向转换写 Before 值
    /// </summary>
    /// <param name="world">目标世界（通常已载入恢复区快照）</param>
    /// <param name="transitions">按时间顺序的转换列表</param>
    /// <returns>重放的编辑格数</returns>
    public static int Replay(VoxelWorld world, IReadOnlyList<JournalTransition> transitions)
    {
        ArgumentNullException.ThrowIfNull(world);
        var replayed = 0;
        foreach (var transition in transitions)
        {
            foreach (var (coordinate, before, after) in transition.Edits)
            {
                var target = transition.Inverted ? before : after;
                if (target.Color.A == 0 && target.MaterialSlot == 0 && target.Flags == 0)
                {
                    if (world.Remove(coordinate)) replayed++;
                }
                else
                {
                    world.Set(coordinate, target);
                    replayed++;
                }
            }
        }

        return replayed;
    }

    /// <summary>
    /// 删除日志文件
    /// </summary>
    /// <param name="journalFilePath">日志文件路径</param>
    public static void Clear(string journalFilePath)
    {
        try
        {
            if (File.Exists(journalFilePath)) File.Delete(journalFilePath);
        }
        catch (IOException)
        {
            // 清理失败不影响编辑流程
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record JournalLine(string Name, JournalEdit[] Edits, int Inverted);

    private sealed record JournalEdit(long X, long Y, long Z, long[]? Before, long[]? After);
}

/// <summary>
/// 一条事务转换：正向（执行/重做）或逆向（撤销）的逐格差异
/// </summary>
/// <param name="Inverted">是否为逆向转换</param>
/// <param name="Edits">逐格差异，Before 或 After 为空值表示该格原本或此后为空</param>
public readonly record struct JournalTransition(bool Inverted,
    IReadOnlyList<(VoxelCoord64 Coordinate, VoxelValue Before, VoxelValue After)> Edits);
