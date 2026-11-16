Shader "UI/WebUI"
{
    Properties
    {
        _MainTex ("Sprite Texture", 2D) = "white" {}
        _RectSize ("Rect Size", Vector) = (100, 100, 0, 0)
        _CornerRadius ("Corner Radius", Float) = 0

        // Owned by MaskableGraphic / Mask; ordinary WebUI draws never write stencil.
        [HideInInspector] _StencilComp ("Stencil Comparison", Float) = 8
        [HideInInspector] _Stencil ("Stencil ID", Float) = 0
        [HideInInspector] _StencilOp ("Stencil Operation", Float) = 0
        [HideInInspector] _StencilWriteMask ("Stencil Write Mask", Float) = 0
        [HideInInspector] _StencilReadMask ("Stencil Read Mask", Float) = 255
        [HideInInspector] _ColorMask ("Color Mask", Float) = 15
        [HideInInspector] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
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
        // Match UI/Default: transparent UI uses Canvas ordering, not depth writes.
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
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP

            #include "WebUIInterpreter.cginc"

            struct appdata_t
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 uv : TEXCOORD0;
                float2 localPos : TEXCOORD1;
                half4 mask : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            float2 _RectSize;
            float _CornerRadius;
            float4 _ClipRect;
            float _UIMaskSoftnessX;
            float _UIMaskSoftnessY;

            int _FillCount;
            int _StrokeCount;
            int _EffectCount;

            v2f vert(appdata_t v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.localPos = v.texcoord;
                o.uv = (o.localPos / max(_RectSize, float2(0.0001, 0.0001))) + 0.5;
                o.color = v.color;
                float2 pixelSize = o.vertex.w;
                pixelSize /= abs(mul((float2x2)UNITY_MATRIX_P, _ScreenParams.xy));
                float4 clampedRect = clamp(_ClipRect, -2e10, 2e10);
                o.mask = half4(v.vertex.xy * 2 - clampedRect.xy - clampedRect.zw,
                    0.25 / (0.25 * half2(_UIMaskSoftnessX, _UIMaskSoftnessY) + abs(pixelSize.xy)));
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
                #ifdef UNITY_UI_CLIP_RECT
                half2 mask = saturate((_ClipRect.zw - _ClipRect.xy - abs(i.mask.xy)) * i.mask.zw);
                finalColor.a *= mask.x * mask.y;
                #endif
                #ifdef UNITY_UI_ALPHACLIP
                // The mask and its pop draw must use the same rounded body. Expanded
                // shadow/stroke vertices must never expose content outside that body.
                clip(min(bodyAlpha, finalColor.a) - 0.001);
                #endif
                return finalColor;
            }
            ENDCG
        }
    }
}
