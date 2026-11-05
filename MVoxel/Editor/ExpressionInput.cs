using System.Globalization;
using ImGuiNET;

namespace MVoxel.Editor;

/// <summary>可输入算术表达式的数值输入框，输入合法内容即时应用，结束编辑后显示最终值</summary>
internal static class ExpressionInput
{
    private static readonly Dictionary<uint, string> Buffers = new();

    private static bool Input(string label, double value, out double result)
    {
        var id = ImGui.GetID(label);
        if (!Buffers.TryGetValue(id, out var text)) text = value.ToString("G", CultureInfo.InvariantCulture);
        var changed = ImGui.InputText(label, ref text, 128, ImGuiInputTextFlags.AutoSelectAll);
        var active = ImGui.IsItemActive();
        var parsed = TryEvaluate(text, out var number);
        result = value;
        var applied = changed && parsed && Math.Abs(number - value) > .000001D;
        if (applied) result = number;
        //编辑中保留用户原文，其余情况按最终值格式化，避免显示值与实际数据不一致
        Buffers[id] = active ? text : (parsed ? number : result).ToString("G", CultureInfo.InvariantCulture);
        return applied;
    }

    /// <summary>
    /// 解析数字或四则运算表达式，失败时不改变当前值
    /// </summary>
    /// <param name="text">输入文本</param>
    /// <param name="value">解析结果</param>
    /// <returns>是否解析成功</returns>
    private static bool TryEvaluate(string text, out double value)
    {
        value = 0D;
        if (text.Length == 0 || text.Any(c => !char.IsDigit(c) && !".+-*/%() ".Contains(c))) return false;
        try
        {
            using var table = new System.Data.DataTable { Locale = CultureInfo.InvariantCulture };
            value = Convert.ToDouble(table.Compute(text, ""), CultureInfo.InvariantCulture);
            return double.IsFinite(value);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or System.Data.EvaluateException or System.Data.SyntaxErrorException or DivideByZeroException)
        { return false; }
    }

    internal static bool Int(string label, ref int value)
    {
        if (!Input(label, value, out var number) || number < int.MinValue || number > int.MaxValue) return false;
        var converted = (int)Math.Truncate(number);
        if (value == converted) return false;
        value = converted; return true;
    }

    internal static bool Float(string label, ref float value)
    {
        if (!Input(label, value, out var number) || number < -float.MaxValue || number > float.MaxValue) return false;
        var converted = (float)number;
        if (value == converted) return false;
        value = converted; return true;
    }
}
