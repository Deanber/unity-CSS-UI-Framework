#ifndef WEB_UI_INTERPRETER_INCLUDED
#define WEB_UI_INTERPRETER_INCLUDED

#include "UnityCG.cginc"

// ==========================================
// 1. C# SetVectorArray 对应的全局数据 Buffers
// ==========================================
float4 _FillsRaw[16];   // 4 * 4
float4 _StrokesRaw[16]; // 4 * 4
float4 _EffectsRaw[12]; // 4 * 3

struct FillData
{
    int type;
    fixed4 colorA;
    fixed4 colorB;
    float4 paramsX;
};

struct StrokeData
{
    int position;
    float width;
    int type;
    fixed4 color;
    fixed4 colorB;
    float4 paramsX;
};

struct EffectData
{
    int type;
    float2 offset;
    fixed4 color;
    float blurRadius;
    float spread;
};

// ==========================================
// 2. 解包 Helper 函数
// ==========================================
FillData GetFillData(int idx)
{
    FillData f;
    int b = idx * 4;
    f.type = (int)_FillsRaw[b].x;
    f.colorA = _FillsRaw[b + 1];
    f.colorB = _FillsRaw[b + 2];
    f.paramsX = _FillsRaw[b + 3];
    return f;
}

StrokeData GetStrokeData(int idx)
{
    StrokeData s;
    int b = idx * 4;
    s.position = (int)_StrokesRaw[b].x;
    s.width = _StrokesRaw[b].y;
    s.type = (int)_StrokesRaw[b].z;
    s.color = _StrokesRaw[b + 1];
    s.colorB = _StrokesRaw[b + 2];
    s.paramsX = _StrokesRaw[b + 3];
    return s;
}

// 描边的着色载荷与 Fill 同构，转成 FillData 后可直接复用 CalculateFill
FillData StrokeToFill(StrokeData s)
{
    FillData f;
    f.type = s.type;
    f.colorA = s.color;
    f.colorB = s.colorB;
    f.paramsX = s.paramsX;
    return f;
}

EffectData GetEffectData(int idx)
{
    EffectData e;
    int b = idx * 3;
    e.type = (int)_EffectsRaw[b].x;
    e.offset = _EffectsRaw[b].yz;
    e.color = _EffectsRaw[b + 1];
    e.blurRadius = _EffectsRaw[b + 2].x;
    e.spread = _EffectsRaw[b + 2].y;
    return e;
}

// ==========================================
// 3. 动态计算顶点 Shader 外扩 Padding
// ==========================================
float CalculateMaxPadding(int strokeCount, int effectCount, float2 dir)
{
    float maxPad = 0.0;

    // 1. 遍历 Stroke 外部轮廓扩充
    for (int i = 0; i < strokeCount; i++)
    {
        StrokeData s = GetStrokeData(i);
        float outerWidth = 0.0;
        if (s.position == 2) // Outer
            outerWidth = s.width;
        else if (s.position == 0) // Center
            outerWidth = s.width * 0.5;

        maxPad = max(maxPad, outerWidth);
    }

    // 2. 遍历 Effect (Shadow) 动态方向投影扩充
    for (int j = 0; j < effectCount; j++)
    {
        EffectData eff = GetEffectData(j);
        if (eff.type == 0) // Drop Shadow
        {
            // 沿当前顶点象限方向计算 Offset 投影量，保证非对称阴影扩展完全精准
            float offsetAlongDir = max(0.0, dot(eff.offset, dir));
            float shadowExtent = offsetAlongDir + eff.blurRadius * 1.5 + eff.spread;
            maxPad = max(maxPad, shadowExtent);
        }
    }

    return maxPad + 2.0; // 额外提供 2 像素 AA 抗锯齿余量
}

// ==========================================
// 4. SDF 基础计算
// ==========================================
float EvaluateSDF(float2 localPos, float2 halfSize, float cornerRadius)
{
    float r = min(cornerRadius, min(halfSize.x, halfSize.y));
    float2 q = abs(localPos) - (halfSize - r);
    return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - r;
}

// ==========================================
// 5. 执行填充逻辑
// ==========================================
fixed4 CalculateFill(float2 uv, float2 localPos, FillData data, sampler2D mainTex)
{
    fixed4 outCol = fixed4(0, 0, 0, 0);
    [branch]
    if (data.type == 0) // Solid
    {
        outCol = data.colorA;
    }
    else if (data.type == 1) // Linear Gradient
    {
        float angle = data.paramsX.x * 0.01745329;
        float2 dir = float2(cos(angle), sin(angle));
        float t = dot(uv - 0.5, dir) + 0.5;
        outCol = lerp(data.colorA, data.colorB, saturate(t));
    }
    else if (data.type == 2) // Standard Radial Gradient
    {
        float2 center = float2(0.5, 0.5);
        float dist = length(uv - center);
        float innerRadius = max(data.paramsX.x, 0.0);
        float outerRadius = max(data.paramsX.y, innerRadius + 0.0001);
        float t = saturate((dist - innerRadius) / (outerRadius - innerRadius));
        outCol = lerp(data.colorA, data.colorB, t);
    }
    else if (data.type == 3) // Angular / Conic Gradient
    {
        float startAngle = data.paramsX.x * 0.01745329;
        float2 dir = uv - 0.5;
        float angle = atan2(dir.y, dir.x) - startAngle;
        float twoPi = 6.28318530718;
        float t = frac(angle / twoPi);
        outCol = lerp(data.colorA, data.colorB, saturate(t));
    }
    else if (data.type == 4) // Texture
    {
        outCol = tex2D(mainTex, uv) * data.colorA;
    }
    return outCol;
}

fixed4 ExecuteFills(float2 uv, float2 localPos, int fillCount, sampler2D mainTex)
{
    fixed4 compositeColor = fixed4(0, 0, 0, 0);
    for (int i = 0; i < fillCount; i++)
    {
        FillData fill = GetFillData(i);
        fixed4 layer = CalculateFill(uv, localPos, fill, mainTex);
        compositeColor.rgb = layer.rgb * layer.a + compositeColor.rgb * (1.0 - layer.a);
        compositeColor.a = layer.a + compositeColor.a * (1.0 - layer.a);
    }
    return compositeColor;
}

// ==========================================
// 6. 执行描边逻辑
// ==========================================
fixed4 ExecuteStrokes(float2 uv, float2 localPos, float dist, float aaWidth, int strokeCount, sampler2D mainTex)
{
    fixed4 strokeColorAcc = fixed4(0, 0, 0, 0);
    for (int i = 0; i < strokeCount; i++)
    {
        StrokeData s = GetStrokeData(i);
        float strokeDist = 0.0;
        
        [branch]
        if (s.position == 1) // Inner
            strokeDist = abs(dist + s.width * 0.5) - s.width * 0.5;
        else if (s.position == 2) // Outer
            strokeDist = abs(dist - s.width * 0.5) - s.width * 0.5;
        else // Center
            strokeDist = abs(dist) - s.width * 0.5;
        
        float alpha = saturate(0.5 - strokeDist / max(aaWidth, 0.0001));
        // 与 Fill 共用同一套着色逻辑（纯色 / 线性 / 径向 / 锥形 / 贴图）
        fixed4 layer = CalculateFill(uv, localPos, StrokeToFill(s), mainTex);
        layer.a *= alpha;
        
        strokeColorAcc.rgb = layer.rgb * layer.a + strokeColorAcc.rgb * (1.0 - layer.a);
        strokeColorAcc.a = layer.a + strokeColorAcc.a * (1.0 - layer.a);
    }
    return strokeColorAcc;
}

// ==========================================
// 7. 执行特效逻辑 (包含 Spread 扩展)
// ==========================================
fixed4 ExecuteEffects(float2 localPos, float2 halfSize, float cornerRadius, int effectCount)
{
    fixed4 effectColorAcc = fixed4(0, 0, 0, 0);
    for (int i = 0; i < effectCount; i++)
    {
        EffectData eff = GetEffectData(i);
        
        [branch]
        if (eff.type == 0) // Drop Shadow
        {
            float2 shadowPos = localPos - eff.offset;
            // 加入 spread 调整 SDF 半径
            float shadowDist = EvaluateSDF(shadowPos, halfSize + eff.spread, cornerRadius);
            
            float shadowAlpha = saturate(1.0 - (shadowDist / max(eff.blurRadius, 0.0001)));
            shadowAlpha = smoothstep(0.0, 1.0, shadowAlpha);
            
            fixed4 layer = eff.color;
            layer.a *= shadowAlpha;
            
            effectColorAcc.rgb = layer.rgb * layer.a + effectColorAcc.rgb * (1.0 - layer.a);
            effectColorAcc.a = layer.a + effectColorAcc.a * (1.0 - layer.a);
        }
    }
    return effectColorAcc;
}

#endif