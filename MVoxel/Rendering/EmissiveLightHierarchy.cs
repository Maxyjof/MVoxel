using System.Numerics;

namespace MVoxel.Rendering;

/// <summary>连续面区间的平衡光源树；保留面键排序，反向 PDF 无需额外散列表。</summary>
internal static class EmissiveLightHierarchy
{
    internal readonly record struct Node(int Left, int Right, int Start, int End,
        Vector3 Minimum, Vector3 Maximum, float Power);

    internal static Node[] Build(Vector3[] positions, float[] powers)
    {
        if (positions.Length != powers.Length) throw new ArgumentException("光源位置和功率数量不一致");
        if (positions.Length == 0) return [];
        var nodes = new List<Node>();
        BuildRange(0, positions.Length);
        return nodes.ToArray();

        int BuildRange(int start, int end)
        {
            var index = nodes.Count;
            nodes.Add(default);
            int left = -1, right = -1;
            Vector3 minimum, maximum;
            double power;
            if (end - start <= 8)
            {
                minimum = new Vector3(float.PositiveInfinity);
                maximum = new Vector3(float.NegativeInfinity);
                power = 0;
                for (var i = start; i < end; i++)
                {
                    minimum = Vector3.Min(minimum, positions[i]);
                    maximum = Vector3.Max(maximum, positions[i] + Vector3.One);
                    power += powers[i];
                }
            }
            else
            {
                var mid = start + (end - start) / 2;
                left = BuildRange(start, mid);
                right = BuildRange(mid, end);
                minimum = Vector3.Min(nodes[left].Minimum, nodes[right].Minimum);
                maximum = Vector3.Max(nodes[left].Maximum, nodes[right].Maximum);
                power = (double)nodes[left].Power + nodes[right].Power;
            }
            nodes[index] = new Node(left, right, start, end, minimum, maximum,
                (float)Math.Min(power, float.MaxValue));
            return index;
        }
    }

    /// <summary>面集合与顺序未变时沿拓扑自底向上更新功率和包围盒。</summary>
    internal static Node[] Refit(Node[] topology, Vector3[] positions, float[] powers)
    {
        var nodes = (Node[])topology.Clone();
        for (var i = nodes.Length - 1; i >= 0; i--)
        {
            var node = nodes[i];
            var minimum = new Vector3(float.PositiveInfinity);
            var maximum = new Vector3(float.NegativeInfinity);
            double power = 0;
            if (node.Left >= 0)
            {
                minimum = Vector3.Min(nodes[node.Left].Minimum, nodes[node.Right].Minimum);
                maximum = Vector3.Max(nodes[node.Left].Maximum, nodes[node.Right].Maximum);
                power = (double)nodes[node.Left].Power + nodes[node.Right].Power;
            }
            else for (var face = node.Start; face < node.End; face++)
            {
                minimum = Vector3.Min(minimum, positions[face]);
                maximum = Vector3.Max(maximum, positions[face] + Vector3.One);
                power += powers[face];
            }
            nodes[i] = node with { Minimum = minimum, Maximum = maximum,
                Power = (float)Math.Min(power, float.MaxValue) };
        }
        return nodes;
    }
}
