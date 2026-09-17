using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering;

[ExecuteAlways]
[RequireComponent(typeof(RectTransform))]
public class WebUI : MaskableGraphic
{
    [Header("Base Corner Settings")]
    [Tooltip("圆角半径（像素）")]
    [Range(0f, 100f)] public float cornerRadius = 0f;

    [Header("Render Stacks (Interpreter Blocks)")]
    public List<FillData> fills = new List<FillData>();
    public List<StrokeData> strokes = new List<StrokeData>();
    public List<EffectData> effects = new List<EffectData>();

    [SerializeField] private Texture m_MainTex;

    private Material m_StencilMaterialInstance;
    private Material m_DefaultMaterialInstance;
    private Mesh m_Mesh;

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
    private readonly Vector4[] m_FillsRawBuffer = new Vector4[4 * 4];   // 4 个 Fill，每个占 4 个 Vector4
    private readonly Vector4[] m_StrokesRawBuffer = new Vector4[4 * 4]; // 4 个 Stroke，每个占 4 个 Vector4
    private readonly Vector4[] m_EffectsRawBuffer = new Vector4[4 * 3]; // 4 个 Effect，每个占 3 个 Vector4

    public override Texture mainTexture => m_MainTex == null ? s_WhiteTexture : m_MainTex;

    public Texture texture
    {
        get => m_MainTex;
        set
        {
            if (m_MainTex != value)
            {
                m_MainTex = value;
                SetVerticesDirty();
            }
        }
    }

    public override Material material
    {
        get
        {
            if (m_DefaultMaterialInstance != null) return m_DefaultMaterialInstance;
            return base.material;
        }
    }

    protected override void OnEnable()
    {
        base.OnEnable();
        UpdateMaterialProperties();
    }

    protected override void OnDisable()
    {
        ReleaseMaterials();
        if (m_Mesh != null)
        {
            if (Application.isPlaying) Destroy(m_Mesh);
            else DestroyImmediate(m_Mesh);
            m_Mesh = null;
        }
        base.OnDisable();
    }

    void OnValidate()
    {
        UpdateMaterialProperties();
    }

    protected override void OnRectTransformDimensionsChange()
    {
        base.OnRectTransformDimensionsChange();
        UpdateMaterialProperties();
    }

    private void ReleaseMaterials()
    {
        if (m_StencilMaterialInstance != null)
        {
            if (Application.isPlaying) Destroy(m_StencilMaterialInstance);
            else DestroyImmediate(m_StencilMaterialInstance);
            m_StencilMaterialInstance = null;
        }
        if (m_DefaultMaterialInstance != null)
        {
            if (Application.isPlaying) Destroy(m_DefaultMaterialInstance);
            else DestroyImmediate(m_DefaultMaterialInstance);
            m_DefaultMaterialInstance = null;
        }
    }

    protected override void UpdateMaterial()
    {
        base.UpdateMaterial();
        UpdateMaterialProperties();
    }

    public void UpdateMaterialProperties()
    {
        Shader stencilShader = Shader.Find("UI/WebUIStencil");
        Shader mainShader = Shader.Find("UI/WebUI"); // 对应 Interpreter Shader
        if (stencilShader == null || mainShader == null) return;

        if (m_StencilMaterialInstance == null || m_StencilMaterialInstance.shader != stencilShader)
        {
            m_StencilMaterialInstance = new Material(stencilShader);
            m_StencilMaterialInstance.name = "UI_WebUI_Stencil_Instance";
        }

        if (m_DefaultMaterialInstance == null || m_DefaultMaterialInstance.shader != mainShader)
        {
            m_DefaultMaterialInstance = new Material(mainShader);
            m_DefaultMaterialInstance.name = "UI_WebUI_Interpreter_Instance";
        }

        Rect pixelRect = rectTransform.rect;
        Vector2 size = pixelRect.size;

        // 基础参数同步
        m_StencilMaterialInstance.SetVector(RectSizeID, size);
        m_StencilMaterialInstance.SetFloat(CornerRadiusID, cornerRadius);

        m_DefaultMaterialInstance.SetVector(RectSizeID, size);
        m_DefaultMaterialInstance.SetFloat(CornerRadiusID, cornerRadius);
        m_DefaultMaterialInstance.mainTexture = mainTexture;

        // 打包提交堆栈数据并切换 Keywords
        UploadInterpreterData(m_DefaultMaterialInstance);

        if (canvasRenderer != null)
        {
            canvasRenderer.materialCount = 2;
            canvasRenderer.SetMaterial(m_DefaultMaterialInstance, 0);
            canvasRenderer.SetMaterial(m_StencilMaterialInstance, 1);
        }
    }

    /// <summary>
    /// 将渲染堆栈序列化为 Vector4[] 打包写入 Shader 并调度变体关键字
    /// </summary>
    private void UploadInterpreterData(Material mat)
    {
        // 1. Fill 填充层打包
        int fillCount = Mathf.Min(fills.Count, 4);
        Array.Clear(m_FillsRawBuffer, 0, m_FillsRawBuffer.Length);
        for (int i = 0; i < fillCount; i++)
        {
            var f = fills[i];
            int baseIdx = i * 4;
            m_FillsRawBuffer[baseIdx]     = new Vector4((int)f.type, 0, 0, 0);
            m_FillsRawBuffer[baseIdx + 1] = f.colorA;
            m_FillsRawBuffer[baseIdx + 2] = f.colorB;
            m_FillsRawBuffer[baseIdx + 3] = f.paramsX;
        }
        mat.SetInt(FillCountID, fillCount);
        mat.SetVectorArray(FillsRawID, m_FillsRawBuffer);

        if (fillCount > 1) mat.EnableKeyword("_ENABLE_MULTIPLE_FILLS");
        else mat.DisableKeyword("_ENABLE_MULTIPLE_FILLS");

        // 2. Stroke 描边层打包
        int strokeCount = Mathf.Min(strokes.Count, 4);
        Array.Clear(m_StrokesRawBuffer, 0, m_StrokesRawBuffer.Length);
        for (int i = 0; i < strokeCount; i++)
        {
            var s = strokes[i];
            int baseIdx = i * 4;
            m_StrokesRawBuffer[baseIdx]     = new Vector4((int)s.position, s.width, (int)s.type, 0);
            m_StrokesRawBuffer[baseIdx + 1] = s.color;
            m_StrokesRawBuffer[baseIdx + 2] = s.colorB;
            m_StrokesRawBuffer[baseIdx + 3] = s.paramsX;
        }
        mat.SetInt(StrokeCountID, strokeCount);
        mat.SetVectorArray(StrokesRawID, m_StrokesRawBuffer);

        if (strokeCount > 0) mat.EnableKeyword("_ENABLE_STROKES");
        else mat.DisableKeyword("_ENABLE_STROKES");

        // 3. Effect 特效层打包
        int effectCount = Mathf.Min(effects.Count, 4);
        Array.Clear(m_EffectsRawBuffer, 0, m_EffectsRawBuffer.Length);
        for (int i = 0; i < effectCount; i++)
        {
            var eff = effects[i];
            int baseIdx = i * 3;
            m_EffectsRawBuffer[baseIdx]     = new Vector4((int)eff.type, eff.offset.x, eff.offset.y, 0);
            m_EffectsRawBuffer[baseIdx + 1] = eff.color;
            m_EffectsRawBuffer[baseIdx + 2] = new Vector4(eff.blurRadius, eff.spread, 0, 0);
        }
        mat.SetInt(EffectCountID, effectCount);
        mat.SetVectorArray(EffectsRawID, m_EffectsRawBuffer);

        if (effectCount > 0) mat.EnableKeyword("_ENABLE_EFFECTS");
        else mat.DisableKeyword("_ENABLE_EFFECTS");
    }

    protected override void UpdateGeometry()
    {
        if (canvasRenderer == null) return;

        if (m_Mesh == null)
        {
            m_Mesh = new Mesh();
            m_Mesh.name = "WebUI_DualSubmesh";
        }

        BuildDualSubmeshMesh(m_Mesh);
        canvasRenderer.SetMesh(m_Mesh);

        UpdateMaterial();
    }

    private void BuildDualSubmeshMesh(Mesh mesh)
    {
        mesh.Clear();
        Rect r = rectTransform.rect;

        Vector3[] verts = new Vector3[4]
        {
            new Vector3(r.xMin, r.yMin, 0f),
            new Vector3(r.xMin, r.yMax, 0f),
            new Vector3(r.xMax, r.yMax, 0f),
            new Vector3(r.xMax, r.yMin, 0f)
        };
        Vector2[] uvs = new Vector2[4]
        {
            new Vector2(0, 0),
            new Vector2(0, 1),
            new Vector2(1, 1),
            new Vector2(1, 0)
        };
        Color32[] colors = new Color32[4] { color, color, color, color };
        int[] triangles = new int[6] { 0, 1, 2, 2, 3, 0 };

        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetColors(colors);
        mesh.subMeshCount = 2;
        mesh.SetTriangles(triangles, 0);
        mesh.SetTriangles(triangles, 1);
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
    }

    public override Material GetModifiedMaterial(Material baseMaterial)
    {
        var toUse = m_StencilMaterialInstance;

        if (m_ShouldRecalculateStencil)
        {
            if (maskable)
            {
                var rootCanvas = MaskUtilities.FindRootSortOverrideCanvas(transform);
                m_StencilValue = MaskUtilities.GetStencilDepth(transform, rootCanvas);
            }
            else
                m_StencilValue = 0;

            m_ShouldRecalculateStencil = false;
        }

        if (m_StencilValue > 0 && !isMaskingGraphic)
        {
            var maskMat = StencilMaterial.Add(toUse, (1 << m_StencilValue) - 1, StencilOp.Keep, CompareFunction.Equal, ColorWriteMask.All, (1 << m_StencilValue) - 1, 0);
            StencilMaterial.Remove(m_MaskMaterial);
            m_MaskMaterial = maskMat;
            toUse = m_MaskMaterial;
        }
        return toUse;
    }
}