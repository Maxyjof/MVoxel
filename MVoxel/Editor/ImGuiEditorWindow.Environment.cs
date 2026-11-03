using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using MVoxel.Scene;

namespace MVoxel.Editor;

internal sealed partial class ImGuiEditorWindow
{
    private static readonly JsonSerializerOptions EnvironmentJsonOptions = new(JsonSerializerDefaults.Web) { IncludeFields = true };

    private void DrawEnvironmentWindow()
    {
        if (!_showEnvironmentWindow) return;
        var open = _showEnvironmentWindow;
        ImGui.SetNextWindowSize(new Vector2(520F, 620F), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("环境##EnvironmentWindow", ref open, PanelWindowFlags))
        {
            ImGui.TextDisabled("当前场景唯一环境配置");
            ImGui.SameLine();
            if (ImGui.SmallButton("恢复默认"))
            {
                _sceneDocument?.Environment = new SceneEnvironmentSettings();
                _sceneDocument?.Environment.Normalize();
                if (_sceneDocument is not null)
                {
                    _sceneDocument.Environment.ApplyTo(_renderSettings);
                    MarkSceneDocumentDirty();
                    _pipeline?.ResetAccumulation();
                }
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("将当前场景环境恢复为默认值，保存场景后生效");
            ImGui.Separator();
            if (_sceneDocument is null)
            {
                ImGui.TextDisabled("当前没有打开的场景");
            }
            else
            {
                var environment = _sceneDocument.Environment;
                var before = JsonSerializer.Serialize(environment, EnvironmentJsonOptions);
                DrawSkyAndGround(environment);
                DrawEnvironmentFog(environment);
                DrawEnvironmentWater(environment);
                DrawEnvironmentWeather(environment);
                environment.Normalize();
                environment.ApplyTo(_renderSettings);
                var after = JsonSerializer.Serialize(environment, EnvironmentJsonOptions);
                if (!string.Equals(before, after, StringComparison.Ordinal))
                {
                    MarkSceneDocumentDirty();
                    _pipeline?.ResetAccumulation();
                }
            }
        }
        ImGui.End();
        _showEnvironmentWindow = open;
    }

    private static void DrawEnvironmentWeather(SceneEnvironmentSettings environment)
    {
        if (!ImGui.CollapsingHeader("天气", ImGuiTreeNodeFlags.DefaultOpen)) return;
        SliderFloat("风强度", ref environment.WeatherWindStrength, 0F, 4F, "%.2f");
        SliderFloat("风向", ref environment.WeatherWindDirection, 0F, 360F, "%.0f°");
        SliderFloat("降雨强度", ref environment.WeatherRainIntensity, 0F, 1F, "%.2f");
        SliderFloat("降雪强度", ref environment.WeatherSnowIntensity, 0F, 1F, "%.2f");
        SliderFloat("雷暴强度", ref environment.WeatherStormIntensity, 0F, 1F, "%.2f");
        SliderFloat("热浪强度", ref environment.WeatherHeatHaze, 0F, 1F, "%.2f");
        SliderFloat("闪电频率", ref environment.WeatherLightningFrequency, 0F, 4F, "%.2f");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("闪电需要雷暴强度大于零；天气为屏幕空间近似效果。");
    }

    private static void DrawSkyAndGround(SceneEnvironmentSettings environment)
    {
        if (!ImGui.CollapsingHeader("天空与地面", ImGuiTreeNodeFlags.DefaultOpen)) return;
        ImGui.Checkbox("显示地面平面", ref environment.ShowGround);
        DrawColorSlider("背景颜色", ref environment.SceneBackgroundColor);
        DrawColorSlider("地面颜色", ref environment.GroundColor);
        SliderFloat("地面过渡", ref environment.GroundFade, 0F, 4096F, "%.0f 格");
        DrawColorSlider("天顶色", ref environment.SkyTop);
        DrawColorSlider("地平线色", ref environment.SkyHorizon);
        SliderFloat("天空环境光", ref environment.SkyAmbient, 0F, 2F, "%.2f");
        SliderFloat("暖色补光", ref environment.AmbientTint, 0F, 1F, "%.2f");
        SliderFloat("近处淡入距离", ref environment.NearFade, 0F, 512F, "%.0f 格");
    }

    private static void DrawEnvironmentFog(SceneEnvironmentSettings environment)
    {
        if (!ImGui.CollapsingHeader("雾气", ImGuiTreeNodeFlags.DefaultOpen)) return;
        DrawColorSlider("雾颜色", ref environment.FogColor);
        SliderFloat("雾浓度", ref environment.FogDensity, 0F, .05F, "%.4f");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("距离雾按相机到表面的距离混合雾色，作用于实时渲染和路径追踪");
        SliderFloat("雾起始距离", ref environment.FogStart, 0F, 100000F, "%.0f 格");
        SliderFloat("天空混合", ref environment.FogSkyBlend, 0F, 1F, "%.2f");
        SliderFloat("最大不透明度", ref environment.FogMaxOpacity, 0F, 1F, "%.2f");
    }

    private static void DrawEnvironmentWater(SceneEnvironmentSettings environment)
    {
        if (!ImGui.CollapsingHeader("水面", ImGuiTreeNodeFlags.DefaultOpen)) return;
        if (ImGui.Checkbox("显示水面", ref environment.ShowWater) && environment.ShowWater)
            environment.WaterAnimationEnabled = true;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("无限水平水面；波浪改变表面法线，不改变轮廓或产生透明折射");
        SliderFloat("水面高度", ref environment.WaterHeight, -100000F, 100000F, "%.1f 格");
        DrawColorSlider("水面颜色", ref environment.WaterColor);
        SliderFloat("波浪大小", ref environment.WaterWaveSize, 0F, 4F, "%.2f");
        SliderFloat("波长", ref environment.WaterWaveLength, .1F, 1024F, "%.1f 格");
        SliderFloat("水面粗糙度", ref environment.WaterRoughness, .01F, 1F, "%.2f");
        ImGui.Checkbox("水面动画", ref environment.WaterAnimationEnabled);
        SliderFloat("动画速度", ref environment.WaterAnimationSpeed, -8F, 8F, "%.2f");
        var flow = environment.WaterFlowDirection;
        BeginInspectorField("流向", "水面细节纹理的二维流动方向");
        if (ImGui.DragFloat2("##EnvironmentWaterFlow", ref flow, .01F, -1F, 1F, "%.2f")) environment.WaterFlowDirection = flow;
        SliderFloat("细节强度", ref environment.WaterDetailStrength, 0F, 2F, "%.2f");
        SliderFloat("高光强度", ref environment.WaterSpecularStrength, 0F, 4F, "%.2f");
    }
}
