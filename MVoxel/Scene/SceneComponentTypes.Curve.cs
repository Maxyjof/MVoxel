using System.Text.Json;
using System.Text.Json.Nodes;
using MVoxel.Core;
using MVoxel.Voxels;

namespace MVoxel.Scene;

/// <summary>
/// 曲线渲染器组件的创建与读写，以及生成体素的隐藏负载入口
/// </summary>
public static partial class SceneComponentTypes
{
    /// <summary>
    /// 判断组件记录是否为曲线渲染器组件
    /// </summary>
    /// <param name="component">待判断的组件记录</param>
    /// <returns>是否为曲线渲染器</returns>
    public static bool IsCurveRenderer(ComponentRecord? component) =>
        component is not null && string.Equals(component.TypeId, CurveRenderer, StringComparison.Ordinal);

    /// <summary>
    /// 创建曲线渲染器组件记录
    /// </summary>
    /// <param name="data">曲线数据</param>
    /// <returns>新的组件记录</returns>
    public static ComponentRecord CreateCurveRenderer(CurveRendererComponentData data)
    {
        var component = new ComponentRecord
        {
            TypeId = CurveRenderer,
            SchemaVersion = 1,
            Data = JsonSerializer.SerializeToElement(new JsonObject()),
        };
        CurveRendererJson.Write(component, data);
        return component;
    }

    /// <summary>
    /// 读取曲线渲染器数据，缺失或损坏的字段回落到默认值
    /// </summary>
    /// <param name="component">组件记录</param>
    /// <returns>规范化后的曲线数据</returns>
    public static CurveRendererComponentData ReadCurveRenderer(ComponentRecord component) =>
        CurveRendererJson.Read(component.Data);

    /// <summary>
    /// 写回曲线渲染器数据，保留生成体素负载与未知字段
    /// </summary>
    /// <param name="component">组件记录</param>
    /// <param name="data">新的曲线数据</param>
    public static void WriteCurveRenderer(ComponentRecord component, CurveRendererComponentData data)
    {
        if (!IsCurveRenderer(component))
            throw new ArgumentException("组件记录不是曲线渲染器组件", nameof(component));
        CurveRendererJson.Write(component, data);
    }

    /// <summary>
    /// 读取曲线组件用于渲染排序的字段，避免每次合成都解析全部控制点
    /// </summary>
    /// <param name="component">曲线组件记录</param>
    /// <param name="sortingLayer">排序图层</param>
    /// <param name="sortingOrder">排序顺序</param>
    public static void ReadCurveSorting(ComponentRecord component, out string sortingLayer, out int sortingOrder) =>
        CurveRendererJson.ReadSorting(component.Data, out sortingLayer, out sortingOrder);

    /// <summary>
    /// 判断对象是否用曲线渲染器生成体素
    /// </summary>
    /// <param name="sceneObject">场景对象</param>
    /// <returns>是否存在启用的曲线渲染器组件</returns>
    public static bool HasCurveRenderer(SceneObject? sceneObject) =>
        sceneObject is not null && sceneObject.Components.Any(item => IsCurveRenderer(item) && item.Enabled);
}
