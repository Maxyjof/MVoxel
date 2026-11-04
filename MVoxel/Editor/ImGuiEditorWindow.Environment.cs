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
        if (ImGui.Begin("环境配置##EnvironmentWindow", ref open, PanelWindowFlags))
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

        static void Tip(string text) { if (ImGui.IsItemHovered()) ImGui.SetTooltip(text); }

        ImGui.SeparatorText("基础");
        DrawColorSlider("雾颜色", ref environment.FogColor);
        Tip("雾的内散射基色，按 sRGB 显示值保存，与天空色由“天空混合”加权。");
        SliderFloat("雾浓度", ref environment.FogDensity, 0F, .05F, "%.4f");
        Tip("消光系数 σt（每格）。0 关闭雾；实时渲染与路径追踪共用同一密度。");
        SliderFloat("雾起始距离", ref environment.FogStart, 0F, 100000F, "%.0f 格");
        Tip("从相机起算的雾起始距离，近处保持清晰；路径追踪中只作用于主射线段。");
        SliderFloat("天空混合", ref environment.FogSkyBlend, 0F, 1F, "%.2f");
        Tip("0 完全用天空色作雾色，1 完全用上方雾颜色。");
        SliderFloat("最大不透明度", ref environment.FogMaxOpacity, 0F, 1F, "%.2f");
        Tip("雾的最大遮蔽量，保留一部分远景轮廓。");
        SliderFloat("内散射亮度", ref environment.FogInscatterStrength, 0F, 4F, "%.2f");
        Tip("雾被环境光照亮的亮度倍率；不改变雾对表面颜色的消光。");

        ImGui.SeparatorText("高度与分布");
        SliderFloat("雾层基准高度", ref environment.FogHeight, -512F, 1024F, "%.1f 格");
        Tip("世界 Y 高度，此处密度等于雾浓度；抬高雾层可只覆盖低洼区域。");
        SliderFloat("高度衰减", ref environment.FogHeightFalloff, 0F, .2F, "%.4f");
        Tip("密度按 exp(-b·(y-基准高度)) 衰减，0 为均匀雾；沿视线做解析积分，不增加采样开销。");
        SliderFloat("体积计算距离", ref environment.FogViewDistance, 0F, 8192F, "%.0f 格");
        Tip("0 表示跟随命中距离；非零时限制体积雾积分范围与天空雾的等效距离。");
        SliderFloat("密度起伏", ref environment.FogNoiseStrength, 0F, 1F, "%.2f");
        Tip("三维值噪声对密度的扰动幅度，营造不均匀雾团；解析积分与体积步进均生效。");
        SliderFloat("噪声频率", ref environment.FogNoiseScale, .001F, .5F, "%.3f");
        Tip("噪声的空间频率，数值越小雾团越大。");
        SliderFloat("噪声漂移", ref environment.FogNoiseSpeed, 0F, 4F, "%.2f");
        Tip("随风强度、风向与时间流动的速度；0 为静止雾。");

        ImGui.SeparatorText("散射与光照");
        SliderFloat("各向异性 g", ref environment.FogAnisotropy, -.95F, .95F, "%.2f");
        Tip("Henyey–Greenstein 相函数；正值越大，朝向太阳的前向散射光晕越集中。");
        SliderFloat("单次散射反照率", ref environment.FogAlbedo, 0F, 1F, "%.2f");
        Tip("1 为纯散射雾（明亮），0 为纯吸收雾（灰暗）。");
        SliderFloat("太阳内散射", ref environment.FogSunScatterStrength, 0F, 4F, "%.2f");
        Tip("太阳在雾中形成的光晕强度；路径追踪的散射点会被几何遮挡，形成体积光柱。");
        DrawColorSlider("太阳内散射色", ref environment.FogSunScatterColor);
        Tip("叠加在太阳颜色上的色比，可用来做暖色晨雾或冷色月雾。");
        SliderFloat("天空雾强度", ref environment.FogSkyFogStrength, 0F, 1F, "%.2f");
        Tip("天空与背景融入雾色的比例，控制远景地平线衔接；高度雾天然让天顶更清。");

        ImGui.SeparatorText("体积质量");
        SliderInt("体积采样步数（实时）", ref environment.FogVolumetricSteps, 0, 64);
        Tip("0 使用零噪声的解析高度雾积分；大于 0 时沿视线抖动步进并逐点查询太阳阴影，产生体积光柱，步数越高开销越大。路径追踪不使用此值，始终按参与介质逐段采样。");
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
