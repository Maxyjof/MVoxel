namespace MVoxel.Core;

/// <summary>
/// 场景、资产和组件使用的稳定身份标识，刻意与显示名、层级位置无关
/// 重命名或移动对象不会改变 StableId，序列化引用因此始终可解析
/// </summary>
public readonly record struct StableId(Guid Value)
{
    /// <summary>
    /// 空标识，表示"没有引用"
    /// </summary>
    public static StableId Empty => new(Guid.Empty);

    /// <summary>
    /// 生成一个新的全局唯一标识
    /// </summary>
    /// <returns>随机 Guid 包装的 StableId</returns>
    public static StableId New() => new(Guid.NewGuid());

    /// <summary>
    /// 解析字符串标识，格式非法时抛异常
    /// </summary>
    /// <param name="text">Guid 文本</param>
    /// <returns>解析后的标识</returns>
    public static StableId Parse(string text) => new(Guid.Parse(text));

    /// <summary>
    /// 尝试解析字符串标识，失败时返回空标识而不抛异常
    /// </summary>
    /// <param name="text">待解析文本，可为 null</param>
    /// <param name="id">解析结果</param>
    /// <returns>是否解析成功</returns>
    public static bool TryParse(string? text, out StableId id)
    {
        if (Guid.TryParse(text, out var value))
        {
            id = new StableId(value);
            return true;
        }

        id = Empty;
        return false;
    }

    /// <summary>
    /// 序列化为标准 Guid 文本
    /// </summary>
    /// <returns>带连字符的 Guid 字符串</returns>
    public override string ToString() => Value.ToString("D");
}
