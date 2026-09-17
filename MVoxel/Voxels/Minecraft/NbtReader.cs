using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace MVoxel.Voxels.Minecraft;

/// <summary>
/// NBT 标签类型，与 Minecraft Java 版规范一一对应
/// </summary>
public enum NbtTagType : byte
{
    /// <summary>
    /// 复合结构结束标记
    /// </summary>
    End = 0,

    /// <summary>
    /// 单字节整数
    /// </summary>
    Byte = 1,

    /// <summary>
    /// 双字节整数
    /// </summary>
    Short = 2,

    /// <summary>
    /// 四字节整数
    /// </summary>
    Int = 3,

    /// <summary>
    /// 八字节整数
    /// </summary>
    Long = 4,

    /// <summary>
    /// 四字节浮点数
    /// </summary>
    Float = 5,

    /// <summary>
    /// 八字节浮点数
    /// </summary>
    Double = 6,

    /// <summary>
    /// 字节数组
    /// </summary>
    ByteArray = 7,

    /// <summary>
    /// UTF8 字符串
    /// </summary>
    String = 8,

    /// <summary>
    /// 同种类型元素的列表
    /// </summary>
    List = 9,

    /// <summary>
    /// 命名的值域集合
    /// </summary>
    Compound = 10,

    /// <summary>
    /// 四字节整数数组
    /// </summary>
    IntArray = 11,

    /// <summary>
    /// 八字节整数数组
    /// </summary>
    LongArray = 12,
}

/// <summary>
/// NBT 解析后的节点基类，所有具体值类型由此派生
/// </summary>
public abstract class NbtNode
{
    /// <summary>
    /// 节点的标签类型
    /// </summary>
    public abstract NbtTagType TagType { get; }
}

/// <summary>
/// NBT 字节节点
/// </summary>
public sealed class NbtByte : NbtNode
{
    /// <summary>
    /// 构造字节节点
    /// </summary>
    /// <param name="value">字节值</param>
    public NbtByte(byte value)
    {
        Value = value;
    }

    /// <inheritdoc />
    public override NbtTagType TagType => NbtTagType.Byte;

    /// <summary>
    /// 字节值
    /// </summary>
    public byte Value { get; }
}

/// <summary>
/// NBT 短整数节点
/// </summary>
public sealed class NbtShort : NbtNode
{
    /// <summary>
    /// 构造短整数节点
    /// </summary>
    /// <param name="value">短整数值</param>
    public NbtShort(short value)
    {
        Value = value;
    }

    /// <inheritdoc />
    public override NbtTagType TagType => NbtTagType.Short;

    /// <summary>
    /// 短整数值
    /// </summary>
    public short Value { get; }
}

/// <summary>
/// NBT 整数节点
/// </summary>
public sealed class NbtInt : NbtNode
{
    /// <summary>
    /// 构造整数节点
    /// </summary>
    /// <param name="value">整数值</param>
    public NbtInt(int value)
    {
        Value = value;
    }

    /// <inheritdoc />
    public override NbtTagType TagType => NbtTagType.Int;

    /// <summary>
    /// 整数值
    /// </summary>
    public int Value { get; }
}

/// <summary>
/// NBT 长整数节点
/// </summary>
public sealed class NbtLong : NbtNode
{
    /// <summary>
    /// 构造长整数节点
    /// </summary>
    /// <param name="value">长整数值</param>
    public NbtLong(long value)
    {
        Value = value;
    }

    /// <inheritdoc />
    public override NbtTagType TagType => NbtTagType.Long;

    /// <summary>
    /// 长整数值
    /// </summary>
    public long Value { get; }
}

/// <summary>
/// NBT 单精度浮点节点
/// </summary>
public sealed class NbtFloat : NbtNode
{
    /// <summary>
    /// 构造浮点节点
    /// </summary>
    /// <param name="value">浮点值</param>
    public NbtFloat(float value)
    {
        Value = value;
    }

    /// <inheritdoc />
    public override NbtTagType TagType => NbtTagType.Float;

    /// <summary>
    /// 浮点值
    /// </summary>
    public float Value { get; }
}

/// <summary>
/// NBT 双精度浮点节点
/// </summary>
public sealed class NbtDouble : NbtNode
{
    /// <summary>
    /// 构造双精度节点
    /// </summary>
    /// <param name="value">双精度值</param>
    public NbtDouble(double value)
    {
        Value = value;
    }

    /// <inheritdoc />
    public override NbtTagType TagType => NbtTagType.Double;

    /// <summary>
    /// 双精度值
    /// </summary>
    public double Value { get; }
}

/// <summary>
/// NBT 字符串节点
/// </summary>
public sealed class NbtString : NbtNode
{
    /// <summary>
    /// 构造字符串节点
    /// </summary>
    /// <param name="value">字符串值</param>
    public NbtString(string value)
    {
        Value = value;
    }

    /// <inheritdoc />
    public override NbtTagType TagType => NbtTagType.String;

    /// <summary>
    /// 字符串值
    /// </summary>
    public string Value { get; }
}

/// <summary>
/// NBT 字节数组节点
/// </summary>
public sealed class NbtByteArray : NbtNode
{
    /// <summary>
    /// 构造字节数组节点
    /// </summary>
    /// <param name="value">字节数组</param>
    public NbtByteArray(byte[] value)
    {
        Value = value;
    }

    /// <inheritdoc />
    public override NbtTagType TagType => NbtTagType.ByteArray;

    /// <summary>
    /// 字节数组
    /// </summary>
    public byte[] Value { get; }
}

/// <summary>
/// NBT 整数数组节点
/// </summary>
public sealed class NbtIntArray : NbtNode
{
    /// <summary>
    /// 构造整数数组节点
    /// </summary>
    /// <param name="value">整数数组</param>
    public NbtIntArray(int[] value)
    {
        Value = value;
    }

    /// <inheritdoc />
    public override NbtTagType TagType => NbtTagType.IntArray;

    /// <summary>
    /// 整数数组
    /// </summary>
    public int[] Value { get; }
}

/// <summary>
/// NBT 长整数数组节点
/// </summary>
public sealed class NbtLongArray : NbtNode
{
    /// <summary>
    /// 构造长整数数组节点
    /// </summary>
    /// <param name="value">长整数数组</param>
    public NbtLongArray(long[] value)
    {
        Value = value;
    }

    /// <inheritdoc />
    public override NbtTagType TagType => NbtTagType.LongArray;

    /// <summary>
    /// 长整数数组
    /// </summary>
    public long[] Value { get; }
}

/// <summary>
/// NBT 列表节点，元素全部为同一种标签类型
/// </summary>
public sealed class NbtList : NbtNode
{
    /// <summary>
    /// 构造列表节点
    /// </summary>
    /// <param name="elementType">元素类型</param>
    /// <param name="items">元素集合</param>
    public NbtList(NbtTagType elementType, IReadOnlyList<NbtNode> items)
    {
        ElementType = elementType;
        Items = items;
    }

    /// <inheritdoc />
    public override NbtTagType TagType => NbtTagType.List;

    /// <summary>
    /// 列表元素类型
    /// </summary>
    public NbtTagType ElementType { get; }

    /// <summary>
    /// 列表元素只读集合
    /// </summary>
    public IReadOnlyList<NbtNode> Items { get; }

    /// <summary>
    /// 元素个数
    /// </summary>
    public int Count => Items.Count;
}

/// <summary>
/// NBT 复合节点，提供按名称的只读访问
/// </summary>
public sealed class NbtCompound : NbtNode
{
    private readonly IReadOnlyDictionary<string, NbtNode> _entries;

    /// <summary>
    /// 由已解析的字段字典构造复合节点
    /// </summary>
    /// <param name="entries">字段字典</param>
    internal NbtCompound(IReadOnlyDictionary<string, NbtNode> entries)
    {
        _entries = entries;
    }

    /// <inheritdoc />
    public override NbtTagType TagType => NbtTagType.Compound;

    /// <summary>
    /// 全部字段的只读字典
    /// </summary>
    public IReadOnlyDictionary<string, NbtNode> Entries => _entries;

    /// <summary>
    /// 判断是否存在指定名称的字段
    /// </summary>
    /// <param name="name">字段名</param>
    /// <returns>存在时为真</returns>
    public bool Contains(string name) => _entries.ContainsKey(name);

    /// <summary>
    /// 取出指定名称的节点
    /// </summary>
    /// <param name="name">字段名</param>
    /// <param name="node">命中时输出节点</param>
    /// <returns>是否存在</returns>
    public bool TryGet(string name, out NbtNode node) => _entries.TryGetValue(name, out node!);

    /// <summary>
    /// 取出指定名称的节点，缺失时抛异常
    /// </summary>
    /// <param name="name">字段名</param>
    /// <returns>节点</returns>
    public NbtNode Get(string name)
    {
        if (!_entries.TryGetValue(name, out var node))
        {
            throw new InvalidDataException($"NBT compound 缺少字段 {name}");
        }

        return node;
    }

    /// <summary>
    /// 按类型取出指定名称的字段
    /// </summary>
    /// <typeparam name="T">期望的 NbtNode 派生类型或基元类型</typeparam>
    /// <param name="name">字段名</param>
    /// <param name="value">命中且类型匹配时输出值</param>
    /// <returns>是否存在且类型匹配</returns>
    public bool TryGet<T>(string name, out T value)
    {
        if (_entries.TryGetValue(name, out var node) && TryCast(node, out value))
        {
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>
    /// 取出字符串字段
    /// </summary>
    /// <param name="name">字段名</param>
    /// <param name="value">命中时输出字符串</param>
    /// <returns>是否存在</returns>
    public bool TryGetString(string name, out string value) => TryGet(name, out value);

    /// <summary>
    /// 取出字符串字段，类型不符或缺失时抛异常
    /// </summary>
    /// <param name="name">字段名</param>
    /// <returns>字符串值</returns>
    public string GetString(string name) =>
        Get(name) is NbtString text ? text.Value : throw new InvalidDataException($"字段 {name} 不是字符串");

    /// <summary>
    /// 取出复合字段
    /// </summary>
    /// <param name="name">字段名</param>
    /// <param name="value">命中时输出复合节点</param>
    /// <returns>是否存在</returns>
    public bool TryGetCompound(string name, out NbtCompound value) => TryGet(name, out value);

    /// <summary>
    /// 取出复合字段，类型不符或缺失时抛异常
    /// </summary>
    /// <param name="name">字段名</param>
    /// <returns>复合节点</returns>
    public NbtCompound GetCompound(string name) =>
        Get(name) is NbtCompound compound ? compound : throw new InvalidDataException($"字段 {name} 不是 compound");

    /// <summary>
    /// 取出列表字段
    /// </summary>
    /// <param name="name">字段名</param>
    /// <param name="value">命中时输出列表节点</param>
    /// <returns>是否存在</returns>
    public bool TryGetList(string name, out NbtList value) => TryGet(name, out value);

    /// <summary>
    /// 取出列表字段，类型不符或缺失时抛异常
    /// </summary>
    /// <param name="name">字段名</param>
    /// <returns>列表节点</returns>
    public NbtList GetList(string name) =>
        Get(name) is NbtList list ? list : throw new InvalidDataException($"字段 {name} 不是 list");

    /// <summary>
    /// 取出长整数数组字段
    /// </summary>
    /// <param name="name">字段名</param>
    /// <param name="value">命中时输出长整数数组</param>
    /// <returns>是否存在</returns>
    public bool TryGetLongArray(string name, out long[] value) => TryGet(name, out value);

    /// <summary>
    /// 取出长整数数组字段，类型不符或缺失时抛异常
    /// </summary>
    /// <param name="name">字段名</param>
    /// <returns>长整数数组</returns>
    public long[] GetLongArray(string name) =>
        Get(name) is NbtLongArray array ? array.Value : throw new InvalidDataException($"字段 {name} 不是 long 数组");

    /// <summary>
    /// 取出整数数组字段
    /// </summary>
    /// <param name="name">字段名</param>
    /// <param name="value">命中时输出整数数组</param>
    /// <returns>是否存在</returns>
    public bool TryGetIntArray(string name, out int[] value) => TryGet(name, out value);

    /// <summary>
    /// 取出整数数组字段，类型不符或缺失时抛异常
    /// </summary>
    /// <param name="name">字段名</param>
    /// <returns>整数数组</returns>
    public int[] GetIntArray(string name) =>
        Get(name) is NbtIntArray array ? array.Value : throw new InvalidDataException($"字段 {name} 不是 int 数组");

    /// <summary>
    /// 取出字节数组字段
    /// </summary>
    /// <param name="name">字段名</param>
    /// <param name="value">命中时输出字节数组</param>
    /// <returns>是否存在</returns>
    public bool TryGetByteArray(string name, out byte[] value) => TryGet(name, out value);

    /// <summary>
    /// 取出字节数组字段，类型不符或缺失时抛异常
    /// </summary>
    /// <param name="name">字段名</param>
    /// <returns>字节数组</returns>
    public byte[] GetByteArray(string name) =>
        Get(name) is NbtByteArray array ? array.Value : throw new InvalidDataException($"字段 {name} 不是 byte 数组");

    /// <summary>
    /// 取出整数字段
    /// </summary>
    /// <param name="name">字段名</param>
    /// <param name="value">命中时输出整数</param>
    /// <returns>是否存在</returns>
    public bool TryGetInt(string name, out int value) => TryGet(name, out value);

    /// <summary>
    /// 取出整数字段，类型不符或缺失时抛异常
    /// </summary>
    /// <param name="name">字段名</param>
    /// <returns>整数值</returns>
    public int GetInt(string name) =>
        Get(name) is NbtInt number ? number.Value : throw new InvalidDataException($"字段 {name} 不是 int");

    /// <summary>
    /// 取出长整数字段
    /// </summary>
    /// <param name="name">字段名</param>
    /// <param name="value">命中时输出长整数</param>
    /// <returns>是否存在</returns>
    public bool TryGetLong(string name, out long value) => TryGet(name, out value);

    /// <summary>
    /// 取出长整数字段，类型不符或缺失时抛异常
    /// </summary>
    /// <param name="name">字段名</param>
    /// <returns>长整数值</returns>
    public long GetLong(string name) =>
        Get(name) is NbtLong number ? number.Value : throw new InvalidDataException($"字段 {name} 不是 long");

    /// <summary>
    /// 取出字节字段
    /// </summary>
    /// <param name="name">字段名</param>
    /// <param name="value">命中时输出字节</param>
    /// <returns>是否存在</returns>
    public bool TryGetByte(string name, out byte value) => TryGet(name, out value);

    /// <summary>
    /// 取出字节字段，类型不符或缺失时抛异常
    /// </summary>
    /// <param name="name">字段名</param>
    /// <returns>字节值</returns>
    public byte GetByte(string name) =>
        Get(name) is NbtByte number ? number.Value : throw new InvalidDataException($"字段 {name} 不是 byte");

    private static bool TryCast<T>(NbtNode node, out T value)
    {
        if (typeof(T) == typeof(string) && node is NbtString text)
        {
            value = (T)(object)text.Value;
            return true;
        }

        if (typeof(T) == typeof(NbtCompound) && node is NbtCompound compound)
        {
            value = (T)(object)compound;
            return true;
        }

        if (typeof(T) == typeof(NbtList) && node is NbtList list)
        {
            value = (T)(object)list;
            return true;
        }

        if (typeof(T) == typeof(long[]) && node is NbtLongArray longArray)
        {
            value = (T)(object)longArray.Value;
            return true;
        }

        if (typeof(T) == typeof(int[]) && node is NbtIntArray intArray)
        {
            value = (T)(object)intArray.Value;
            return true;
        }

        if (typeof(T) == typeof(byte[]) && node is NbtByteArray byteArray)
        {
            value = (T)(object)byteArray.Value;
            return true;
        }

        if (typeof(T) == typeof(int) && node is NbtInt integer)
        {
            value = (T)(object)integer.Value;
            return true;
        }

        if (typeof(T) == typeof(long) && node is NbtLong longInteger)
        {
            value = (T)(object)longInteger.Value;
            return true;
        }

        if (typeof(T) == typeof(byte) && node is NbtByte byteValue)
        {
            value = (T)(object)byteValue.Value;
            return true;
        }

        if (typeof(T) == typeof(short) && node is NbtShort shortValue)
        {
            value = (T)(object)shortValue.Value;
            return true;
        }

        if (typeof(T) == typeof(float) && node is NbtFloat floatValue)
        {
            value = (T)(object)floatValue.Value;
            return true;
        }

        if (typeof(T) == typeof(double) && node is NbtDouble doubleValue)
        {
            value = (T)(object)doubleValue.Value;
            return true;
        }

        value = default!;
        return false;
    }
}

/// <summary>
/// 顺序游标式 NBT 解码器，所有读取都先做边界检查
/// </summary>
internal ref struct NbtDecoder
{
    private readonly ReadOnlySpan<byte> _data;
    private int _offset;

    /// <summary>
    /// 绑定待解码字节
    /// </summary>
    /// <param name="data">完整 NBT 字节</param>
    public NbtDecoder(ReadOnlySpan<byte> data)
    {
        _data = data;
        _offset = 0;
    }

    /// <summary>
    /// 解码根 compound，要求首字节为 Compound 且带根名
    /// </summary>
    /// <returns>根复合节点</returns>
    public NbtCompound ReadRoot()
    {
        var rootType = (NbtTagType)ReadByte();
        if (rootType != NbtTagType.Compound)
        {
            throw new InvalidDataException("NBT 根节点必须是 Compound");
        }

        ReadString();
        return ReadCompound();
    }

    private void Ensure(int count)
    {
        if (_offset + count > _data.Length)
        {
            throw new InvalidDataException("NBT 数据在预期位置被截断");
        }
    }

    private byte ReadByte()
    {
        Ensure(1);
        return _data[_offset++];
    }

    private ushort ReadUInt16()
    {
        Ensure(2);
        var value = BinaryPrimitives.ReadUInt16BigEndian(_data.Slice(_offset, 2));
        _offset += 2;
        return value;
    }

    private int ReadInt32()
    {
        Ensure(4);
        var value = BinaryPrimitives.ReadInt32BigEndian(_data.Slice(_offset, 4));
        _offset += 4;
        return value;
    }

    private long ReadInt64()
    {
        Ensure(8);
        var value = BinaryPrimitives.ReadInt64BigEndian(_data.Slice(_offset, 8));
        _offset += 8;
        return value;
    }

    private float ReadSingle()
    {
        Ensure(4);
        var value = BinaryPrimitives.ReadSingleBigEndian(_data.Slice(_offset, 4));
        _offset += 4;
        return value;
    }

    private double ReadDouble()
    {
        Ensure(8);
        var value = BinaryPrimitives.ReadDoubleBigEndian(_data.Slice(_offset, 8));
        _offset += 8;
        return value;
    }

    private string ReadString()
    {
        var length = ReadUInt16();
        Ensure(length);
        var text = Encoding.UTF8.GetString(_data.Slice(_offset, length));
        _offset += length;
        return text;
    }

    private byte[] ReadByteArray(int count)
    {
        if (count < 0)
        {
            throw new InvalidDataException("NBT 数组长度为负");
        }

        Ensure(count);
        var result = _data.Slice(_offset, count).ToArray();
        _offset += count;
        return result;
    }

    private int[] ReadInt32Array(int count)
    {
        if (count < 0)
        {
            throw new InvalidDataException("NBT 数组长度为负");
        }

        if ((long)count * 4 > _data.Length - _offset)
        {
            throw new InvalidDataException("NBT IntArray 越界");
        }

        var result = new int[count];
        for (var index = 0; index < count; index++)
        {
            result[index] = ReadInt32();
        }

        return result;
    }

    private long[] ReadInt64Array(int count)
    {
        if (count < 0)
        {
            throw new InvalidDataException("NBT 数组长度为负");
        }

        if ((long)count * 8 > _data.Length - _offset)
        {
            throw new InvalidDataException("NBT LongArray 越界");
        }

        var result = new long[count];
        for (var index = 0; index < count; index++)
        {
            result[index] = ReadInt64();
        }

        return result;
    }

    private NbtNode ReadPayload(NbtTagType type)
    {
        switch (type)
        {
            case NbtTagType.Byte:
                return new NbtByte(ReadByte());
            case NbtTagType.Short:
                return new NbtShort((short)ReadUInt16());
            case NbtTagType.Int:
                return new NbtInt(ReadInt32());
            case NbtTagType.Long:
                return new NbtLong(ReadInt64());
            case NbtTagType.Float:
                return new NbtFloat(ReadSingle());
            case NbtTagType.Double:
                return new NbtDouble(ReadDouble());
            case NbtTagType.ByteArray:
                return new NbtByteArray(ReadByteArray(ReadInt32()));
            case NbtTagType.String:
                return new NbtString(ReadString());
            case NbtTagType.List:
                return ReadList();
            case NbtTagType.Compound:
                return ReadCompound();
            case NbtTagType.IntArray:
                return new NbtIntArray(ReadInt32Array(ReadInt32()));
            case NbtTagType.LongArray:
                return new NbtLongArray(ReadInt64Array(ReadInt32()));
            default:
                throw new InvalidDataException($"不支持的 NBT 标签类型 {(byte)type}");
        }
    }

    private NbtCompound ReadCompound()
    {
        var entries = new Dictionary<string, NbtNode>(StringComparer.Ordinal);
        while (true)
        {
            var type = (NbtTagType)ReadByte();
            if (type == NbtTagType.End)
            {
                break;
            }

            if (!Enum.IsDefined(type))
            {
                throw new InvalidDataException($"不支持的 NBT 标签类型 {(byte)type}");
            }

            var name = ReadString();
            entries[name] = ReadPayload(type);
        }

        return new NbtCompound(entries);
    }

    private NbtList ReadList()
    {
        var elementType = (NbtTagType)ReadByte();
        if (elementType != NbtTagType.End && !Enum.IsDefined(elementType))
        {
            throw new InvalidDataException($"不支持的 NBT 列表元素类型 {(byte)elementType}");
        }

        var count = ReadInt32();
        if (count < 0)
        {
            throw new InvalidDataException("NBT 列表元素个数为负");
        }

        var items = new List<NbtNode>(count);
        for (var index = 0; index < count; index++)
        {
            items.Add(ReadPayload(elementType));
        }

        return new NbtList(elementType, items);
    }
}

/// <summary>
/// NBT 只读解析入口，支持直接解析与自动识别压缩格式
/// </summary>
public static class NbtReader
{
    /// <summary>
    /// 解析未压缩的 NBT 字节
    /// </summary>
    /// <param name="data">完整 NBT 字节</param>
    /// <returns>根复合节点</returns>
    public static NbtCompound Read(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            throw new InvalidDataException("NBT 数据为空");
        }

        return new NbtDecoder(data).ReadRoot();
    }

    /// <summary>
    /// 解析压缩过的 NBT 文件字节，按魔数自动选择 GZip 或 ZLib
    /// </summary>
    /// <param name="fileBytes">磁盘上的文件字节</param>
    /// <returns>根复合节点</returns>
    public static NbtCompound ReadCompressed(byte[] fileBytes)
    {
        ArgumentNullException.ThrowIfNull(fileBytes);
        var decompressed = Decompress(fileBytes);
        return Read(decompressed);
    }

    private static byte[] Decompress(byte[] fileBytes)
    {
        var isGzip = fileBytes.Length >= 2 && fileBytes[0] == 0x1F && fileBytes[1] == 0x8B;
        using var input = new MemoryStream(fileBytes, writable: false);
        using var output = new MemoryStream();
        Stream decompressor = isGzip
            ? new GZipStream(input, CompressionMode.Decompress)
            : new ZLibStream(input, CompressionMode.Decompress);
        using (decompressor)
        {
            CopyWithLimit(decompressor, output, maxBytes: 64 * 1024 * 1024);
        }

        return output.ToArray();
    }

    private static void CopyWithLimit(Stream source, Stream destination, long maxBytes)
    {
        var buffer = new byte[8192];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new InvalidDataException("解压后的 NBT 数据超出上限");
            }

            destination.Write(buffer, 0, read);
        }
    }
}
