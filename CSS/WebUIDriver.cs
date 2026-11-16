using System;
using UnityEngine;

namespace WebUI
{
    public enum FillType
    {
        Solid = 0,
        LinearGradient = 1,
        RadialGradient = 2,
        Rotate = 3,
        Texture = 4
    }

    public enum StrokePosition
    {
        Center = 0,
        Inner = 1,
        Outer = 2
    }

    public enum EffectType
    {
        DropShadow = 0,
        InnerShadow = 1,
        OuterGlow = 2
    }

    [Serializable]
    public struct FillData
    {
        public FillType type;
        public Color colorA;
        public Color colorB;
        public Vector4 paramsX; // (Angle, Scale, OffsetX, OffsetY)
    }

    [Serializable]
    public struct StrokeData
    {
        public StrokePosition position;
        public float width;

        [Tooltip("描边着色类型。Solid 时只用 color，与旧数据完全兼容")]
        public FillType type;

        [Tooltip("Solid 的颜色 / 渐变的起始色")] public Color color;

        [Tooltip("渐变的结束色，Solid 时忽略")] public Color colorB;

        [Tooltip("渐变参数，语义同 FillData.paramsX")] public Vector4 paramsX;
    }

    [Serializable]
    public struct EffectData
    {
        public EffectType type;
        public Color color;
        public Vector2 offset;
        public float blurRadius;
        public float spread;
    }
}
