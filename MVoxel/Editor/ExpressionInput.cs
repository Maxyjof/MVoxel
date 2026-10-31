using System.Globalization;
using ImGuiNET;

namespace MVoxel.Editor;

/// <summary>可输入算术表达式的数值输入框；确认或失焦后应用。</summary>
internal static class ExpressionInput
{
    private static readonly Dictionary<uint, string> Buffers = new();
    private static bool Input(string label, double value, out double result)
    {
        var id = ImGui.GetID(label);
        if (!Buffers.TryGetValue(id, out var text)) text = value.ToString("G", CultureInfo.InvariantCulture);
        var submitted = ImGui.InputText(label, ref text, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        submitted |= ImGui.IsItemDeactivatedAfterEdit();
        var active = ImGui.IsItemActive();
        Buffers[id] = active ? text : value.ToString("G", CultureInfo.InvariantCulture);
        result = value;
        if (!submitted) return false;
        try
        {
            if (text.Length == 0 || text.Any(c => !char.IsDigit(c) && !".+-*/%() ".Contains(c))) return false;
            using var table = new System.Data.DataTable { Locale = CultureInfo.InvariantCulture };
            result = Convert.ToDouble(table.Compute(text, ""), CultureInfo.InvariantCulture);
            return double.IsFinite(result);
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
