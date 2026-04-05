// SPDX-License-Identifier: MIT
// SemanticController.cs — 语义级可编辑 3DGS 运行时控制器
// 放置于 package/Runtime/ 目录下，自动归入 GaussianSplatting 程序集

using System;
using System.Collections.Generic;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// 语义层控制器：挂载于与 GaussianSplatRenderer 同一 GameObject 上，
    /// 提供语义层可见性控制、着色控制和 NLI 命令接口。
    /// 
    /// 数据流:
    ///   PLY int layer → Asset layerInfo → GPU _SplatLayerData buffer
    ///   SemanticController → _SemanticVisibility / _SemanticTints buffers → Compute Shader
    /// </summary>
    [RequireComponent(typeof(GaussianSplatRenderer))]
    [ExecuteInEditMode]
    public class SemanticController : MonoBehaviour
    {
        // ═══════════════════════════════════════════════════════════
        //  常量
        // ═══════════════════════════════════════════════════════════
        
        /// <summary>最大语义层数（与 GPU 侧 MAX_SEMANTIC_LAYERS 一致）</summary>
        public const int kMaxSemanticLayers = 32;
        
        // ═══════════════════════════════════════════════════════════
        //  序列化字段
        // ═══════════════════════════════════════════════════════════
        
        [Tooltip("语义名称列表，索引对应 layer ID。可在 Inspector 中手动填写或从 metadata.json 导入。")]
        [SerializeField] string[] m_SemanticNames = Array.Empty<string>();
        
        // ═══════════════════════════════════════════════════════════
        //  运行时状态
        // ═══════════════════════════════════════════════════════════
        
        GaussianSplatRenderer m_Renderer;
        
        /// <summary>per-semantic 可见性 (1=可见, 0=隐藏)</summary>
        uint[] m_Visibility;
        
        /// <summary>per-semantic 着色 (RGBA, 默认白色=不改变)</summary>
        Vector4[] m_Tints;
        
        /// <summary>GPU 缓冲</summary>
        GraphicsBuffer m_GpuVisibility;
        GraphicsBuffer m_GpuTints;
        
        /// <summary>脏标记，仅在状态变化时上传 GPU</summary>
        bool m_VisibilityDirty = true;
        bool m_TintsDirty = true;
        
        /// <summary>实际语义层数（从 Asset layerInfo 获取）</summary>
        int m_SemanticCount;
        
        // ═══════════════════════════════════════════════════════════
        //  公开属性
        // ═══════════════════════════════════════════════════════════
        
        /// <summary>语义层数量</summary>
        public int semanticCount => m_SemanticCount;
        
        /// <summary>GPU 可见性缓冲（供 GaussianSplatRenderer 绑定到 Compute Shader）</summary>
        public GraphicsBuffer gpuVisibility => m_GpuVisibility;
        
        /// <summary>GPU 着色缓冲</summary>
        public GraphicsBuffer gpuTints => m_GpuTints;
        
        /// <summary>是否有脏数据需要上传</summary>
        public bool isDirty => m_VisibilityDirty || m_TintsDirty;
        
        /// <summary>语义名称数组</summary>
        public string[] semanticNames => m_SemanticNames;
        
        // ═══════════════════════════════════════════════════════════
        //  生命周期
        // ═══════════════════════════════════════════════════════════
        
        void OnEnable()
        {
            m_Renderer = GetComponent<GaussianSplatRenderer>();
        }
        
        void OnDisable()
        {
            DisposeGpuBuffers();
        }
        
        void OnDestroy()
        {
            DisposeGpuBuffers();
        }
        
        // ═══════════════════════════════════════════════════════════
        //  初始化 / 销毁
        // ═══════════════════════════════════════════════════════════
        
        /// <summary>
        /// 由 GaussianSplatRenderer.UpdateRessources() 调用，
        /// 在 Asset 数据加载完成后初始化语义缓冲。
        /// </summary>
        public void InitializeFromAsset(GaussianSplatAsset asset)
        {
            DisposeGpuBuffers();
            
            if (asset == null)
            {
                m_SemanticCount = 0;
                return;
            }
            
            // 从 Asset layerInfo 获取语义层数
            // ★ 使用 maxLayerId + 1 而非 Count，确保稀疏 layerID（如 {0,2,7}）也能正确索引。
            //   Count=3 时 semanticCount=3，shader 中 sid=7 >= 3 会跳过语义逻辑，该层永远无法控制。
            //   maxId+1=8 则 Inspector 显示 0..7，空位无 splat 映射，无害但可控。
            var layerInfo = asset.layerInfo;
            int maxLayerId = 0;
            foreach (var kv in layerInfo)
                maxLayerId = Mathf.Max(maxLayerId, kv.Key);
            m_SemanticCount = Mathf.Clamp(maxLayerId + 1, 1, kMaxSemanticLayers);
            
            // 初始化 CPU 数组
            m_Visibility = new uint[kMaxSemanticLayers];
            m_Tints = new Vector4[kMaxSemanticLayers];
            
            for (int i = 0; i < kMaxSemanticLayers; i++)
            {
                m_Visibility[i] = 1; // 默认全部可见
                m_Tints[i] = new Vector4(1, 1, 1, 1); // 默认白色（不改变颜色）
            }
            
            // 创建 GPU 缓冲
            // 固定大小 kMaxSemanticLayers，确保 GPU 侧访问永不越界
            m_GpuVisibility = new GraphicsBuffer(GraphicsBuffer.Target.Structured, kMaxSemanticLayers, sizeof(uint))
                { name = "SemanticVisibility" };
            m_GpuTints = new GraphicsBuffer(GraphicsBuffer.Target.Structured, kMaxSemanticLayers, 4 * sizeof(float))
                { name = "SemanticTints" };
            
            // 标记需要上传
            m_VisibilityDirty = true;
            m_TintsDirty = true;
            
            // 自动同步语义名称数组大小
            if (m_SemanticNames == null || m_SemanticNames.Length < m_SemanticCount)
            {
                var newNames = new string[m_SemanticCount];
                if (m_SemanticNames != null)
                    Array.Copy(m_SemanticNames, newNames, Mathf.Min(m_SemanticNames.Length, m_SemanticCount));
                for (int i = m_SemanticNames?.Length ?? 0; i < m_SemanticCount; i++)
                    newNames[i] = $"Semantic_{i}";
                m_SemanticNames = newNames;
            }
        }
        
        /// <summary>
        /// 如果有脏数据，执行 GPU 上传。
        /// 由 GaussianSplatRenderer 在每帧 CalcViewData 之前调用。
        /// </summary>
        public void UploadIfDirty()
        {
            if (m_GpuVisibility == null || m_GpuTints == null)
                return;
            
            if (m_VisibilityDirty)
            {
                m_GpuVisibility.SetData(m_Visibility);
                m_VisibilityDirty = false;
            }
            
            if (m_TintsDirty)
            {
                m_GpuTints.SetData(m_Tints);
                m_TintsDirty = false;
            }
        }
        
        void DisposeGpuBuffers()
        {
            m_GpuVisibility?.Dispose();
            m_GpuVisibility = null;
            m_GpuTints?.Dispose();
            m_GpuTints = null;
        }
        
        // ═══════════════════════════════════════════════════════════
        //  可见性控制 API
        // ═══════════════════════════════════════════════════════════
        
        /// <summary>设置指定语义层的可见性</summary>
        public void SetVisibility(int semanticId, bool visible)
        {
            if (semanticId < 0 || semanticId >= kMaxSemanticLayers || m_Visibility == null)
                return;
            
            uint val = visible ? 1u : 0u;
            if (m_Visibility[semanticId] != val)
            {
                m_Visibility[semanticId] = val;
                m_VisibilityDirty = true;
            }
        }
        
        /// <summary>获取指定语义层的可见性</summary>
        public bool GetVisibility(int semanticId)
        {
            if (semanticId < 0 || semanticId >= kMaxSemanticLayers || m_Visibility == null)
                return true;
            return m_Visibility[semanticId] != 0;
        }
        
        /// <summary>设置所有语义层的可见性</summary>
        public void SetAllVisibility(bool visible)
        {
            if (m_Visibility == null) return;
            uint val = visible ? 1u : 0u;
            for (int i = 0; i < kMaxSemanticLayers; i++)
                m_Visibility[i] = val;
            m_VisibilityDirty = true;
        }
        
        /// <summary>仅显示指定语义层，隐藏其余</summary>
        public void IsolateLayer(int semanticId)
        {
            if (m_Visibility == null) return;
            for (int i = 0; i < kMaxSemanticLayers; i++)
                m_Visibility[i] = (i == semanticId) ? 1u : 0u;
            m_VisibilityDirty = true;
        }
        
        // ═══════════════════════════════════════════════════════════
        //  着色控制 API
        // ═══════════════════════════════════════════════════════════
        
        /// <summary>设置指定语义层的着色颜色</summary>
        public void SetTint(int semanticId, Color tint)
        {
            if (semanticId < 0 || semanticId >= kMaxSemanticLayers || m_Tints == null)
                return;
            
            Vector4 v = new Vector4(tint.r, tint.g, tint.b, tint.a);
            if (m_Tints[semanticId] != v)
            {
                m_Tints[semanticId] = v;
                m_TintsDirty = true;
            }
        }
        
        /// <summary>获取指定语义层的着色颜色</summary>
        public Color GetTint(int semanticId)
        {
            if (semanticId < 0 || semanticId >= kMaxSemanticLayers || m_Tints == null)
                return Color.white;
            var v = m_Tints[semanticId];
            return new Color(v.x, v.y, v.z, v.w);
        }
        
        /// <summary>重置所有着色为白色</summary>
        public void ResetAllTints()
        {
            if (m_Tints == null) return;
            for (int i = 0; i < kMaxSemanticLayers; i++)
                m_Tints[i] = new Vector4(1, 1, 1, 1);
            m_TintsDirty = true;
        }
        
        // ═══════════════════════════════════════════════════════════
        //  语义名称 API
        // ═══════════════════════════════════════════════════════════
        
        /// <summary>获取语义层名称</summary>
        public string GetSemanticName(int semanticId)
        {
            if (m_SemanticNames == null || semanticId < 0 || semanticId >= m_SemanticNames.Length)
                return $"Layer_{semanticId}";
            return string.IsNullOrEmpty(m_SemanticNames[semanticId])
                ? $"Layer_{semanticId}"
                : m_SemanticNames[semanticId];
        }
        
        /// <summary>设置语义层名称</summary>
        public void SetSemanticName(int semanticId, string name)
        {
            if (m_SemanticNames == null || semanticId < 0 || semanticId >= m_SemanticNames.Length)
                return;
            m_SemanticNames[semanticId] = name;
        }
        
        /// <summary>
        /// 从字典批量设置语义名称（用于从 metadata.json 导入）。
        /// key = semantic ID, value = 语义名称
        /// </summary>
        public void SetSemanticNamesFromDictionary(Dictionary<int, string> nameDict)
        {
            if (nameDict == null) return;
            foreach (var kv in nameDict)
            {
                SetSemanticName(kv.Key, kv.Value);
            }
        }
        
        // ═══════════════════════════════════════════════════════════
        //  NLI 命令接口（供语音/文本指令调用）
        // ═══════════════════════════════════════════════════════════
        
        /// <summary>
        /// 按名称查找语义 ID。
        /// 支持模糊匹配（Contains, 不区分大小写）。
        /// </summary>
        public int FindSemanticIdByName(string name)
        {
            if (string.IsNullOrEmpty(name) || m_SemanticNames == null)
                return -1;
            
            // 精确匹配优先
            for (int i = 0; i < m_SemanticNames.Length; i++)
            {
                if (string.Equals(m_SemanticNames[i], name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            
            // 模糊匹配
            for (int i = 0; i < m_SemanticNames.Length; i++)
            {
                if (!string.IsNullOrEmpty(m_SemanticNames[i]) &&
                    m_SemanticNames[i].IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    return i;
            }
            
            return -1;
        }
        
        /// <summary>
        /// NLI 命令：按名称隐藏语义层
        /// 例如: ExecuteCommand("hide", "chair") 
        /// </summary>
        public bool ExecuteCommand(string action, string targetName)
        {
            int id = FindSemanticIdByName(targetName);
            if (id < 0)
            {
                Debug.LogWarning($"[SemanticController] Semantic layer '{targetName}' not found.");
                return false;
            }
            
            switch (action.ToLowerInvariant())
            {
                case "hide":
                    SetVisibility(id, false);
                    return true;
                case "show":
                    SetVisibility(id, true);
                    return true;
                case "isolate":
                    IsolateLayer(id);
                    return true;
                case "highlight":
                    ResetAllTints();
                    SetTint(id, new Color(1f, 0.3f, 0.3f, 1f)); // 红色高亮
                    return true;
                case "tint_red":
                    SetTint(id, new Color(1f, 0.3f, 0.3f, 1f));
                    return true;
                case "tint_green":
                    SetTint(id, new Color(0.3f, 1f, 0.3f, 1f));
                    return true;
                case "tint_blue":
                    SetTint(id, new Color(0.3f, 0.3f, 1f, 1f));
                    return true;
                case "reset":
                    SetVisibility(id, true);
                    SetTint(id, Color.white);
                    return true;
                default:
                    Debug.LogWarning($"[SemanticController] Unknown action: '{action}'");
                    return false;
            }
        }
        
        /// <summary>重置所有语义状态（全部可见 + 白色着色）</summary>
        public void ResetAll()
        {
            SetAllVisibility(true);
            ResetAllTints();
        }
    }
}
