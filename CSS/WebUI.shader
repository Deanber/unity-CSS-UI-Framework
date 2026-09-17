Shader "UI/WebUI"
{
    Properties
    {
        _MainTex ("Sprite Texture", 2D) = "white" {}
        _RectSize ("Rect Size", Vector) = (100, 100, 0, 0)
        _CornerRadius ("Corner Radius", Float) = 0

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
    }

    SubShader
    {
        Tags 
        { 
            "Queue"="Transparent" 
            "IgnoreProjector"="True" 
            "RenderType"="Transparent" 
            "PreviewType"="Plane" 
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "WebUI_Interpreter_Pass"
            
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #pragma shader_feature_local _ENABLE_MULTIPLE_FILLS
            #pragma shader_feature_local _ENABLE_STROKES
            #pragma shader_feature_local _ENABLE_EFFECTS

            #include "WebUIInterpreter.cginc"

            struct appdata_t
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 texcoord : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 uv : TEXCOORD0;
                float2 localPos : TEXCOORD1;
            };

            sampler2D _MainTex;
            float2 _RectSize;
            float _CornerRadius;

            int _FillCount;
            int _StrokeCount;
            int _EffectCount;

            v2f vert(appdata_t v)
            {
                v2f o;
                float2 dir = sign(v.texcoord - 0.5);
                float padding = CalculateMaxPadding(_StrokeCount, _EffectCount, dir);
                float4 expandedVertex = v.vertex;
                expandedVertex.xy += dir * padding;
                o.vertex = UnityObjectToClipPos(expandedVertex);
                o.localPos = dir * (_RectSize * 0.5 + padding);
                o.uv = (o.localPos / _RectSize) + 0.5;
                o.color = v.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 halfSize = _RectSize * 0.5;
                float dist = EvaluateSDF(i.localPos, halfSize, _CornerRadius);
                float aaWidth = fwidth(dist) * 0.5;
                float bodyAlpha = saturate(0.5 - dist / max(aaWidth, 0.0001));
                fixed4 finalColor = ExecuteFills(i.uv, i.localPos, _FillCount, _MainTex);
                finalColor.a *= bodyAlpha;
                #if _ENABLE_STROKES
                fixed4 strokeCol = ExecuteStrokes(i.uv, i.localPos, dist, aaWidth, _StrokeCount, _MainTex);
                finalColor.rgb = strokeCol.rgb * strokeCol.a + finalColor.rgb * (1.0 - strokeCol.a);
                finalColor.a = strokeCol.a + finalColor.a * (1.0 - strokeCol.a);
                #endif
                #if _ENABLE_EFFECTS
                fixed4 effectCol = ExecuteEffects(i.localPos, halfSize, _CornerRadius, _EffectCount);
                finalColor = lerp(effectCol, finalColor, finalColor.a);
                #endif
                finalColor.a *= i.color.a;
                return finalColor;
            }
            ENDCG
        }
    }
}