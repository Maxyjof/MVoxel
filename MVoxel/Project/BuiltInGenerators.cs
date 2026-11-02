namespace MVoxel.Project;

/// <summary>创建项目生成器资产时可选的内置脚本。</summary>
internal static class BuiltInGenerators
{
    internal sealed record Template(string Name, string Source);

    internal static readonly IReadOnlyList<Template> All =
    [
        new("噪声丘陵", """
generator.define({
    name: "噪声丘陵",
    bounds: { min: [-256, -256, -256], max: [256, 256, 256] },
    parameters: [
        { id: "ox", label: "中心 X", type: "int", default: 0, min: -224, max: 224 },
        { id: "oy", label: "底部 Y", type: "int", default: 0, min: -256, max: 224 },
        { id: "oz", label: "中心 Z", type: "int", default: 0, min: -224, max: 224 },
        { id: "size", label: "半宽", type: "int", default: 16, min: 1, max: 32 },
        { id: "height", label: "起伏高度", type: "int", default: 12, min: 0, max: 32 },
        { id: "scale", label: "地形尺度", type: "int", default: 8, min: 1, max: 32 },
        { id: "seed", label: "种子", type: "int", default: 1, min: 0, max: 1000000 },
        { id: "color", label: "颜色", type: "color", default: 0x62964c }
    ],
    run: function(ctx, p) {
        function hash(x, z) {
            var v = Math.sin(x * 127.1 + z * 311.7 + p.seed * 19.19) * 43758.5453;
            return v - Math.floor(v);
        }
        function noise(x, z) {
            var ix = Math.floor(x), iz = Math.floor(z), fx = x - ix, fz = z - iz;
            fx = fx * fx * (3 - 2 * fx); fz = fz * fz * (3 - 2 * fz);
            var a = hash(ix, iz) * (1 - fx) + hash(ix + 1, iz) * fx;
            var b = hash(ix, iz + 1) * (1 - fx) + hash(ix + 1, iz + 1) * fx;
            return a * (1 - fz) + b * fz;
        }
        for (var z = -p.size; z <= p.size; z++)
            for (var x = -p.size; x <= p.size; x++) {
                var y = Math.round(noise(x / p.scale, z / p.scale) * p.height);
                ctx.fillBox(x + p.ox, p.oy, z + p.oz, x + p.ox, y + p.oy, z + p.oz, p.color);
            }
    }
});
"""),
        new("颜色斑驳", """
generator.define({
    name: "颜色斑驳",
    bounds: { min: [-256, -256, -256], max: [256, 256, 256] },
    parameters: [
        { id: "ox", label: "中心 X", type: "int", default: 0, min: -240, max: 240 },
        { id: "oy", label: "中心 Y", type: "int", default: 0, min: -240, max: 240 },
        { id: "oz", label: "中心 Z", type: "int", default: 0, min: -240, max: 240 },
        { id: "size", label: "处理半径", type: "int", default: 8, min: 1, max: 16 },
        { id: "amount", label: "明暗幅度", type: "int", default: 20, min: 0, max: 100 },
        { id: "seed", label: "种子", type: "int", default: 1, min: 0, max: 1000000 }
    ],
    run: function(ctx, p) {
        function channel(v) { return Math.max(0, Math.min(255, Math.round(v))); }
        for (var z = -p.size; z <= p.size; z++)
            for (var y = -p.size; y <= p.size; y++)
                for (var x = -p.size; x <= p.size; x++) {
                    var voxel = ctx.getVoxel(x + p.ox, y + p.oy, z + p.oz);
                    if (voxel === null) continue;
                    var n = Math.sin(x * 127.1 + y * 269.5 + z * 311.7 + p.seed * 19.19) * 43758.5453;
                    var delta = ((n - Math.floor(n)) * 2 - 1) * p.amount;
                    var c = voxel.color, a = Math.floor(c / 16777216);
                    var r = channel((Math.floor(c / 65536) % 256) + delta);
                    var g = channel((Math.floor(c / 256) % 256) + delta);
                    var b = channel((c % 256) + delta);
                    voxel.color = a * 16777216 + r * 65536 + g * 256 + b;
                    ctx.setVoxelData(x + p.ox, y + p.oy, z + p.oz, voxel);
                }
    }
});
"""),
        new("按颜色替换", """
generator.define({
    name: "按颜色替换",
    bounds: { min: [-256, -256, -256], max: [256, 256, 256] },
    parameters: [
        { id: "ox", label: "中心 X", type: "int", default: 0, min: -240, max: 240 },
        { id: "oy", label: "中心 Y", type: "int", default: 0, min: -240, max: 240 },
        { id: "oz", label: "中心 Z", type: "int", default: 0, min: -240, max: 240 },
        { id: "size", label: "处理半径", type: "int", default: 8, min: 1, max: 16 },
        { id: "from", label: "原颜色", type: "color", default: 0xffffff },
        { id: "to", label: "新颜色", type: "color", default: 0x4aa3df }
    ],
    run: function(ctx, p) {
        for (var z = -p.size; z <= p.size; z++)
            for (var y = -p.size; y <= p.size; y++)
                for (var x = -p.size; x <= p.size; x++) {
                    var px = x + p.ox, py = y + p.oy, pz = z + p.oz;
                    var voxel = ctx.getVoxel(px, py, pz);
                    if (voxel === null || voxel.color % 16777216 !== p.from) continue;
                    voxel.color = Math.floor(voxel.color / 16777216) * 16777216 + p.to;
                    ctx.setVoxelData(px, py, pz, voxel);
                }
    }
});
"""),
        new("保留表面", """
generator.define({
    name: "保留表面",
    bounds: { min: [-256, -256, -256], max: [256, 256, 256] },
    parameters: [
        { id: "ox", label: "中心 X", type: "int", default: 0, min: -240, max: 240 },
        { id: "oy", label: "中心 Y", type: "int", default: 0, min: -240, max: 240 },
        { id: "oz", label: "中心 Z", type: "int", default: 0, min: -240, max: 240 },
        { id: "size", label: "处理半径", type: "int", default: 8, min: 1, max: 16 }
    ],
    run: function(ctx, p) {
        var side = p.size * 2 + 1, occupied = [];
        function index(x, y, z) { return (z * side + y) * side + x; }
        for (var z = 0; z < side; z++)
            for (var y = 0; y < side; y++)
                for (var x = 0; x < side; x++)
                    occupied[index(x, y, z)] = ctx.getVoxel(x - p.size + p.ox, y - p.size + p.oy, z - p.size + p.oz) !== null;
        for (var z = 1; z < side - 1; z++)
            for (var y = 1; y < side - 1; y++)
                for (var x = 1; x < side - 1; x++)
                    if (occupied[index(x, y, z)] && occupied[index(x - 1, y, z)] &&
                        occupied[index(x + 1, y, z)] && occupied[index(x, y - 1, z)] &&
                        occupied[index(x, y + 1, z)] && occupied[index(x, y, z - 1)] &&
                        occupied[index(x, y, z + 1)])
                        ctx.eraseVoxel(x - p.size + p.ox, y - p.size + p.oy, z - p.size + p.oz);
    }
});
""")
    ];
}
