using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace WebUI
{
    [ExecuteAlways]
    [RequireComponent(typeof(RectTransform), typeof(CanvasRenderer))]
    public class WebUI : MaskableGraphic
    {
        private const int MaxStyleLayers = 4;

        [Header("Base Corner Settings")] [Tooltip("圆角半径（像素）")] [Range(0f, 100f)]
        public float cornerRadius = 0f;

        [Header("Render Stacks (Interpreter Blocks)")]
        public List<FillData> fills = new List<FillData>();

        public List<StrokeData> strokes = new List<StrokeData>();
        public List<EffectData> effects = new List<EffectData>();

        [SerializeField] private Texture m_MainTex;

        private Material m_DefaultMaterialInstance;

        // Shader 属性 PropertyIDs
        private static readonly int RectSizeID = Shader.PropertyToID("_RectSize");
        private static readonly int CornerRadiusID = Shader.PropertyToID("_CornerRadius");

        // Interpreter 数据 Array IDs
        private static readonly int FillCountID = Shader.PropertyToID("_FillCount");
        private static readonly int FillsRawID = Shader.PropertyToID("_FillsRaw");

        private static readonly int StrokeCountID = Shader.PropertyToID("_StrokeCount");
        private static readonly int StrokesRawID = Shader.PropertyToID("_StrokesRaw");

        private static readonly int EffectCountID = Shader.PropertyToID("_EffectCount");
        private static readonly int EffectsRawID = Shader.PropertyToID("_EffectsRaw");

        // 数据打包缓存 Raw Buffers (以 Vector4 形式安全传输到 Shader)
        private readonly Vector4[] m_FillsRawBuffer = new Vector4[MaxStyleLayers * 4];
        private readonly Vector4[] m_StrokesRawBuffer = new Vector4[MaxStyleLayers * 4];
        private readonly Vector4[] m_EffectsRawBuffer = new Vector4[MaxStyleLayers * 3];

        public override Texture mainTexture => m_MainTex == null ? s_WhiteTexture : m_MainTex;

        public Texture texture
        {
            get => m_MainTex;
            set
            {
                if (m_MainTex != value)
                {
                    m_MainTex = value;
                    SetMaterialDirty();
                }
            }
        }

        public override Material material
        {
            get
            {
                // Each WebUI owns its interpreter data. Never modify a shared UI material.
                if (m_DefaultMaterialInstance == null)
                {
                    var shader = Shader.Find("UI/WebUI");
                    if (shader == null) return base.defaultMaterial;
                    m_DefaultMaterialInstance = new Material(shader)
                    {
                        name = "UI_WebUI_Interpreter_Instance",
                        hideFlags = HideFlags.HideAndDontSave
                    };
                }
                return m_DefaultMaterialInstance;
            }
        }

        public override Material materialForRendering
        {
            get
            {
                var source = material;
                ApplyMaterialProperties(source);

                // Keep Graphic -> IMaterialModifier -> MaskableGraphic/Mask intact.
                // Unity owns stencil depth, read/write masks and the pop (unmask) draw.
                var modified = base.materialForRendering;
                if (modified != source) ApplyMaterialProperties(modified);

                // StencilMaterial caches copies; size/style edits must reach both the
                // visible mask and its pop material, without overwriting stencil state.
                for (int i = 0; i < canvasRenderer.popMaterialCount; i++)
                    ApplyMaterialProperties(canvasRenderer.GetPopMaterial(i));

                return modified;
            }
        }

        protected override void OnDisable()
        {
            // Clear the renderer and release Unity's stencil reference before its source.
            base.OnDisable();
            ReleaseMaterials();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            // Also covers a material queried while the component was inactive.
            ReleaseMaterials();
        }

        protected override void OnRectTransformDimensionsChange()
        {
            base.OnRectTransformDimensionsChange();
            SetMaterialDirty();
        }

        private void ReleaseMaterials()
        {
            if (m_DefaultMaterialInstance != null)
            {
                if (Application.isPlaying) Destroy(m_DefaultMaterialInstance);
                else DestroyImmediate(m_DefaultMaterialInstance);
                m_DefaultMaterialInstance = null;
            }
        }

        /// <summary>Queue style changes for Unity's normal Canvas rebuild.</summary>
        public void UpdateMaterialProperties()
        {
            // Strokes and shadows affect both mesh bounds and material parameters.
            SetVerticesDirty();
            SetMaterialDirty();
        }

        private void ApplyMaterialProperties(Material target)
        {
            if (target == null || !target.HasProperty(RectSizeID)) return;
            target.SetVector(RectSizeID, rectTransform.rect.size);
            target.SetFloat(CornerRadiusID, cornerRadius);
            target.mainTexture = mainTexture;
            UploadInterpreterData(target);
        }

        private void UploadInterpreterData(Material mat)
        {
            // 1. Fill 填充层打包
            int fillCount = Mathf.Min(fills.Count, MaxStyleLayers);
            Array.Clear(m_FillsRawBuffer, 0, m_FillsRawBuffer.Length);
            for (int i = 0; i < fillCount; i++)
            {
                var f = fills[i];
                int baseIdx = i * 4;
                m_FillsRawBuffer[baseIdx] = new Vector4((int)f.type, 0, 0, 0);
                m_FillsRawBuffer[baseIdx + 1] = f.colorA;
                m_FillsRawBuffer[baseIdx + 2] = f.colorB;
                m_FillsRawBuffer[baseIdx + 3] = f.paramsX;
            }

            mat.SetInt(FillCountID, fillCount);
            mat.SetVectorArray(FillsRawID, m_FillsRawBuffer);

            if (fillCount > 1) mat.EnableKeyword("_ENABLE_MULTIPLE_FILLS");
            else mat.DisableKeyword("_ENABLE_MULTIPLE_FILLS");

            // 2. Stroke 描边层打包
            int strokeCount = Mathf.Min(strokes.Count, MaxStyleLayers);
            Array.Clear(m_StrokesRawBuffer, 0, m_StrokesRawBuffer.Length);
            for (int i = 0; i < strokeCount; i++)
            {
                var s = strokes[i];
                int baseIdx = i * 4;
                m_StrokesRawBuffer[baseIdx] = new Vector4((int)s.position, s.width, (int)s.type, 0);
                m_StrokesRawBuffer[baseIdx + 1] = s.color;
                m_StrokesRawBuffer[baseIdx + 2] = s.colorB;
                m_StrokesRawBuffer[baseIdx + 3] = s.paramsX;
            }

            mat.SetInt(StrokeCountID, strokeCount);
            mat.SetVectorArray(StrokesRawID, m_StrokesRawBuffer);

            if (strokeCount > 0) mat.EnableKeyword("_ENABLE_STROKES");
            else mat.DisableKeyword("_ENABLE_STROKES");

            // 3. Effect 特效层打包
            int effectCount = Mathf.Min(effects.Count, MaxStyleLayers);
            Array.Clear(m_EffectsRawBuffer, 0, m_EffectsRawBuffer.Length);
            for (int i = 0; i < effectCount; i++)
            {
                var eff = effects[i];
                int baseIdx = i * 3;
                m_EffectsRawBuffer[baseIdx] = new Vector4((int)eff.type, eff.offset.x, eff.offset.y, 0);
                m_EffectsRawBuffer[baseIdx + 1] = eff.color;
                m_EffectsRawBuffer[baseIdx + 2] = new Vector4(eff.blurRadius, eff.spread, 0, 0);
            }

            mat.SetInt(EffectCountID, effectCount);
            mat.SetVectorArray(EffectsRawID, m_EffectsRawBuffer);

            if (effectCount > 0) mat.EnableKeyword("_ENABLE_EFFECTS");
            else mat.DisableKeyword("_ENABLE_EFFECTS");
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            Rect rect = rectTransform.rect;
            bool useWebUI = material.HasProperty(RectSizeID);
            AddCorner(vh, rect, new Vector2(-1f, -1f), useWebUI);
            AddCorner(vh, rect, new Vector2(-1f, 1f), useWebUI);
            AddCorner(vh, rect, new Vector2(1f, 1f), useWebUI);
            AddCorner(vh, rect, new Vector2(1f, -1f), useWebUI);
            vh.AddTriangle(0, 1, 2);
            vh.AddTriangle(2, 3, 0);
        }

        private void AddCorner(VertexHelper vh, Rect rect, Vector2 direction, bool useWebUI)
        {
            // Expand in the graphic's local space so Canvas batching scales the padding with the body.
            float padding = useWebUI ? GetCornerPadding(direction) : 0f;
            Vector2 centeredPosition = Vector2.Scale(rect.size * 0.5f, direction) + direction * padding;
            Vector2 uv = useWebUI ? centeredPosition : (direction + Vector2.one) * 0.5f;
            vh.AddVert(rect.center + centeredPosition, color, uv);
        }

        private float GetCornerPadding(Vector2 direction)
        {
            float padding = 0f;
            int strokeCount = Mathf.Min(strokes.Count, MaxStyleLayers);
            for (int i = 0; i < strokeCount; i++)
            {
                var stroke = strokes[i];
                float outerWidth = stroke.position switch
                {
                    StrokePosition.Outer => stroke.width,
                    StrokePosition.Center => stroke.width * 0.5f,
                    _ => 0f
                };
                padding = Mathf.Max(padding, outerWidth);
            }

            int effectCount = Mathf.Min(effects.Count, MaxStyleLayers);
            for (int i = 0; i < effectCount; i++)
            {
                var effect = effects[i];
                if (effect.type != EffectType.DropShadow) continue;

                float offset = Mathf.Max(0f, Vector2.Dot(effect.offset, direction));
                float shadowExtent = offset + effect.blurRadius * 1.5f + effect.spread;
                padding = Mathf.Max(padding, shadowExtent);
            }

            return padding + 2f; // Antialiasing margin in local units.
        }
    }
}
