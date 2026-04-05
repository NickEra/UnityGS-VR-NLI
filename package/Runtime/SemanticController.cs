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
        
        [Tooltip("Semantic name list. Index corresponds to layer ID. Can be set manually or imported from semantic_dict.json.")]
        [SerializeField] string[] m_SemanticNames = Array.Empty<string>();
        
        [Tooltip("Semantic dictionary JSON file (semantic_dict.json). Supports nested components format.")]
        [SerializeField] TextAsset m_SemanticDictAsset;
        
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
        
        /// <summary>Actual semantic layer count (from Asset layerInfo, = maxLayerId + 1)</summary>
        int m_SemanticCount;
        
        /// <summary>Sorted list of layer IDs that actually have splats in the asset</summary>
        int[] m_ActiveLayerIds = Array.Empty<int>();
        
        /// <summary>Per-layer splat counts (indexed by raw semantic ID)</summary>
        int[] m_LayerSplatCounts = Array.Empty<int>();
        
        // ═══════════════════════════════════════════════════════════
        //  公开属性
        // ═══════════════════════════════════════════════════════════
        
        /// <summary>Semantic layer count (= maxLayerId + 1, used for GPU buffer indexing)</summary>
        public int semanticCount => m_SemanticCount;
        
        /// <summary>Sorted list of layer IDs that actually have splats (use for UI iteration)</summary>
        public int[] activeLayerIds => m_ActiveLayerIds;
        
        /// <summary>Number of layers that actually have splats</summary>
        public int activeLayerCount => m_ActiveLayerIds.Length;
        
        /// <summary>Check if a given layer ID has splats in the asset</summary>
        public bool IsLayerActive(int layerId) => Array.IndexOf(m_ActiveLayerIds, layerId) >= 0;
        
        /// <summary>Get splat count for a given layer ID (0 if not present)</summary>
        public int GetLayerSplatCount(int layerId)
        {
            if (layerId < 0 || layerId >= m_LayerSplatCounts.Length) return 0;
            return m_LayerSplatCounts[layerId];
        }
        
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
            // Self-initialize if Renderer already has a valid asset loaded
            // (handles the case where SemanticController is added after the Renderer)
            if (m_Renderer != null && m_Renderer.HasValidAsset && m_SemanticCount == 0)
            {
                InitializeFromAsset(m_Renderer.asset);
            }
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
                m_ActiveLayerIds = Array.Empty<int>();
                m_LayerSplatCounts = Array.Empty<int>();
                return;
            }
            
            // Build active layer info from Asset layerInfo
            // layerInfo: Dictionary<int,int> where key=layerID, value=splatCount
            var layerInfo = asset.layerInfo;
            int maxLayerId = 0;
            var activeIds = new List<int>();
            foreach (var kv in layerInfo)
            {
                maxLayerId = Mathf.Max(maxLayerId, kv.Key);
                if (kv.Value > 0)
                    activeIds.Add(kv.Key);
            }
            activeIds.Sort();
            m_ActiveLayerIds = activeIds.ToArray();
            
            // semanticCount = maxId + 1 (GPU arrays are indexed by raw semantic_id)
            m_SemanticCount = Mathf.Clamp(maxLayerId + 1, 1, kMaxSemanticLayers);
            
            // Per-layer splat counts
            m_LayerSplatCounts = new int[m_SemanticCount];
            foreach (var kv in layerInfo)
            {
                if (kv.Key >= 0 && kv.Key < m_SemanticCount)
                    m_LayerSplatCounts[kv.Key] = kv.Value;
            }
            
            // Initialize CPU arrays (full kMaxSemanticLayers for safe GPU indexing)
            m_Visibility = new uint[kMaxSemanticLayers];
            m_Tints = new Vector4[kMaxSemanticLayers];
            
            for (int i = 0; i < kMaxSemanticLayers; i++)
            {
                m_Visibility[i] = 1;
                m_Tints[i] = new Vector4(1, 1, 1, 1);
            }
            
            // Create GPU buffers (fixed size, ensures no out-of-bounds on GPU side)
            m_GpuVisibility = new GraphicsBuffer(GraphicsBuffer.Target.Structured, kMaxSemanticLayers, sizeof(uint))
                { name = "SemanticVisibility" };
            m_GpuTints = new GraphicsBuffer(GraphicsBuffer.Target.Structured, kMaxSemanticLayers, 4 * sizeof(float))
                { name = "SemanticTints" };
            
            m_VisibilityDirty = true;
            m_TintsDirty = true;
            
            // Sync semantic names array size
            if (m_SemanticNames == null || m_SemanticNames.Length < m_SemanticCount)
            {
                var newNames = new string[m_SemanticCount];
                if (m_SemanticNames != null)
                    Array.Copy(m_SemanticNames, newNames, Mathf.Min(m_SemanticNames.Length, m_SemanticCount));
                m_SemanticNames = newNames;
            }
            
            // Auto-load names from assigned JSON asset
            if (m_SemanticDictAsset != null)
                LoadFromAssignedAsset();
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
        
        /// <summary>Semantic dict JSON asset reference</summary>
        public TextAsset semanticDictAsset
        {
            get => m_SemanticDictAsset;
            set => m_SemanticDictAsset = value;
        }
        
        // ═══════════════════════════════════════════════════════════
        //  Semantic name / alias API
        // ═══════════════════════════════════════════════════════════
        
        /// <summary>Per-layer alias lists (populated from JSON "aliases" field, used by NLI)</summary>
        string[][] m_Aliases;
        
        /// <summary>Per-layer descriptions (from JSON "description" field)</summary>
        string[] m_Descriptions;
        
        /// <summary>Per-layer default colors (from JSON "default_visual.color" [r,g,b])</summary>
        Color[] m_DefaultColors;
        
        /// <summary>Per-layer centroids (from JSON "centroid" [x,y,z])</summary>
        Vector3[] m_Centroids;
        
        /// <summary>Dataset name (from JSON "dataset" field)</summary>
        string m_DatasetName;
        
        /// <summary>
        /// Load semantic names (and aliases) from a semantic_dict.json string.
        /// Supported formats:
        ///   A) Nested:  { "components": [ { "semantic_id":0, "name":"head", "aliases":["skull",...] }, ... ] }
        ///   B) Flat:    { "0":"head", "1":"fin", ... }
        /// </summary>
        public bool LoadSemanticDictFromJson(string jsonText)
        {
            if (string.IsNullOrEmpty(jsonText))
                return false;
            
            // Try nested format first (has "components" array)
            var dict = ParseNestedComponents(jsonText, out var aliasDict,
                out var descDict, out var colorDict, out var centroidDict, out var datasetName);
            
            // Fallback to flat format
            if (dict == null || dict.Count == 0)
                dict = ParseFlatDict(jsonText);
            
            if (dict == null || dict.Count == 0)
            {
                Debug.LogWarning("[SemanticController] Failed to parse semantic_dict JSON.");
                return false;
            }
            
            // Ensure names array is large enough
            int maxId = 0;
            foreach (var kv in dict)
                maxId = Mathf.Max(maxId, kv.Key);
            
            int requiredSize = Mathf.Max(maxId + 1, m_SemanticCount);
            if (m_SemanticNames == null || m_SemanticNames.Length < requiredSize)
            {
                var newNames = new string[requiredSize];
                if (m_SemanticNames != null)
                    Array.Copy(m_SemanticNames, newNames, Mathf.Min(m_SemanticNames.Length, requiredSize));
                m_SemanticNames = newNames;
            }
            
            // Fill names
            foreach (var kv in dict)
            {
                if (kv.Key >= 0 && kv.Key < m_SemanticNames.Length)
                    m_SemanticNames[kv.Key] = kv.Value;
            }
            
            // Fill aliases
            if (aliasDict != null && aliasDict.Count > 0)
            {
                m_Aliases = new string[requiredSize][];
                foreach (var kv in aliasDict)
                {
                    if (kv.Key >= 0 && kv.Key < requiredSize)
                        m_Aliases[kv.Key] = kv.Value;
                }
            }
            
            // Fill descriptions
            if (descDict != null && descDict.Count > 0)
            {
                m_Descriptions = new string[requiredSize];
                foreach (var kv in descDict)
                {
                    if (kv.Key >= 0 && kv.Key < requiredSize)
                        m_Descriptions[kv.Key] = kv.Value;
                }
            }
            
            // Fill default colors
            if (colorDict != null && colorDict.Count > 0)
            {
                m_DefaultColors = new Color[requiredSize];
                for (int i = 0; i < requiredSize; i++)
                    m_DefaultColors[i] = Color.white;
                foreach (var kv in colorDict)
                {
                    if (kv.Key >= 0 && kv.Key < requiredSize)
                        m_DefaultColors[kv.Key] = kv.Value;
                }
            }
            
            // Fill centroids
            if (centroidDict != null && centroidDict.Count > 0)
            {
                m_Centroids = new Vector3[requiredSize];
                foreach (var kv in centroidDict)
                {
                    if (kv.Key >= 0 && kv.Key < requiredSize)
                        m_Centroids[kv.Key] = kv.Value;
                }
            }
            
            // Dataset name
            if (!string.IsNullOrEmpty(datasetName))
                m_DatasetName = datasetName;
            
            Debug.Log($"[SemanticController] Loaded {dict.Count} semantic names from JSON." +
                      (descDict?.Count > 0 ? $" {descDict.Count} descriptions." : "") +
                      (colorDict?.Count > 0 ? $" {colorDict.Count} colors." : ""));
            return true;
        }
        
        /// <summary>
        /// Load semantic names from the assigned TextAsset (m_SemanticDictAsset).
        /// </summary>
        public bool LoadFromAssignedAsset()
        {
            if (m_SemanticDictAsset == null)
            {
                Debug.LogWarning("[SemanticController] No semantic dict asset assigned.");
                return false;
            }
            return LoadSemanticDictFromJson(m_SemanticDictAsset.text);
        }
        
        // ── JSON Parsers ────────────────────────────────────────
        
        /// <summary>
        /// Parse nested format: { "components": [ { "semantic_id":N, "name":"...", "aliases":[...] } ] }
        /// Returns id->name dict, and optionally id->aliases dict.
        /// </summary>
        static Dictionary<int, string> ParseNestedComponents(string json, out Dictionary<int, string[]> aliasDict,
            out Dictionary<int, string> descDict, out Dictionary<int, Color> colorDict,
            out Dictionary<int, Vector3> centroidDict, out string datasetName)
        {
            aliasDict = new Dictionary<int, string[]>();
            descDict = new Dictionary<int, string>();
            colorDict = new Dictionary<int, Color>();
            centroidDict = new Dictionary<int, Vector3>();
            datasetName = null;
            var result = new Dictionary<int, string>();
            
            // Extract top-level "dataset" field
            datasetName = ExtractStringValue(json, "dataset");
            
            // Check if "components" key exists
            int compIdx = json.IndexOf("\"components\"", StringComparison.Ordinal);
            if (compIdx < 0) return result;
            
            // Find the opening bracket of the components array
            int arrStart = json.IndexOf('[', compIdx);
            if (arrStart < 0) return result;
            
            // Find matching closing bracket
            int arrEnd = FindMatchingBracket(json, arrStart, '[', ']');
            if (arrEnd < 0) return result;
            
            string arrContent = json.Substring(arrStart + 1, arrEnd - arrStart - 1);
            
            // Split into individual component objects by finding { } pairs
            int pos = 0;
            while (pos < arrContent.Length)
            {
                int objStart = arrContent.IndexOf('{', pos);
                if (objStart < 0) break;
                int objEnd = FindMatchingBracket(arrContent, objStart, '{', '}');
                if (objEnd < 0) break;
                
                string objStr = arrContent.Substring(objStart, objEnd - objStart + 1);
                
                // Extract "semantic_id": N
                int semId = ExtractIntValue(objStr, "semantic_id");
                
                // Extract "name": "..."
                string name = ExtractStringValue(objStr, "name");
                
                if (semId >= 0 && !string.IsNullOrEmpty(name))
                    result[semId] = name;
                
                // Extract "aliases": ["...", "..."]
                var aliases = ExtractStringArray(objStr, "aliases");
                if (aliases != null && aliases.Length > 0)
                    aliasDict[semId] = aliases;
                
                // Extract "description": "..."
                string desc = ExtractStringValue(objStr, "description");
                if (!string.IsNullOrEmpty(desc))
                    descDict[semId] = desc;
                
                // Extract "centroid": [x, y, z]
                var centroidArr = ExtractFloatArray(objStr, "centroid");
                if (centroidArr != null && centroidArr.Length >= 3)
                    centroidDict[semId] = new Vector3(centroidArr[0], centroidArr[1], centroidArr[2]);
                
                // Extract "default_visual": { "color": [r, g, b], ... }
                int dvIdx = objStr.IndexOf("\"default_visual\"", StringComparison.Ordinal);
                if (dvIdx >= 0)
                {
                    int dvObjStart = objStr.IndexOf('{', dvIdx);
                    if (dvObjStart >= 0)
                    {
                        int dvObjEnd = FindMatchingBracket(objStr, dvObjStart, '{', '}');
                        if (dvObjEnd >= 0)
                        {
                            string dvStr = objStr.Substring(dvObjStart, dvObjEnd - dvObjStart + 1);
                            var colorArr = ExtractFloatArray(dvStr, "color");
                            if (colorArr != null && colorArr.Length >= 3)
                                colorDict[semId] = new Color(colorArr[0], colorArr[1], colorArr[2], 1f);
                        }
                    }
                }
                
                pos = objEnd + 1;
            }
            
            return result;
        }
        
        /// <summary>
        /// Parse flat format: { "0": "head", "1": "fin", ... }
        /// </summary>
        static Dictionary<int, string> ParseFlatDict(string json)
        {
            var result = new Dictionary<int, string>();
            if (string.IsNullOrEmpty(json)) return result;
            
            json = json.Trim();
            if (json.StartsWith("{")) json = json.Substring(1);
            if (json.EndsWith("}")) json = json.Substring(0, json.Length - 1);
            
            int pos = 0;
            while (pos < json.Length)
            {
                int keyStart = json.IndexOf('"', pos);
                if (keyStart < 0) break;
                int keyEnd = json.IndexOf('"', keyStart + 1);
                if (keyEnd < 0) break;
                string keyStr = json.Substring(keyStart + 1, keyEnd - keyStart - 1);
                
                int colon = json.IndexOf(':', keyEnd + 1);
                if (colon < 0) break;
                
                int valStart = json.IndexOf('"', colon + 1);
                if (valStart < 0) break;
                int valEnd = json.IndexOf('"', valStart + 1);
                if (valEnd < 0) break;
                string valStr = json.Substring(valStart + 1, valEnd - valStart - 1);
                
                if (int.TryParse(keyStr, out int keyInt))
                    result[keyInt] = valStr;
                
                pos = valEnd + 1;
            }
            
            return result;
        }
        
        // ── JSON helper methods ─────────────────────────────────
        
        static int FindMatchingBracket(string s, int openPos, char open, char close)
        {
            int depth = 0;
            for (int i = openPos; i < s.Length; i++)
            {
                if (s[i] == open) depth++;
                else if (s[i] == close) { depth--; if (depth == 0) return i; }
            }
            return -1;
        }
        
        static int ExtractIntValue(string json, string key)
        {
            string pattern = "\"" + key + "\"";
            int idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return -1;
            int colon = json.IndexOf(':', idx + pattern.Length);
            if (colon < 0) return -1;
            
            // Read digits after colon (skip whitespace)
            int start = colon + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t' || json[start] == '\n' || json[start] == '\r'))
                start++;
            int end = start;
            if (end < json.Length && json[end] == '-') end++;
            while (end < json.Length && char.IsDigit(json[end])) end++;
            
            if (end > start && int.TryParse(json.Substring(start, end - start), out int val))
                return val;
            return -1;
        }
        
        static string ExtractStringValue(string json, string key)
        {
            string pattern = "\"" + key + "\"";
            int idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return null;
            int colon = json.IndexOf(':', idx + pattern.Length);
            if (colon < 0) return null;
            int valStart = json.IndexOf('"', colon + 1);
            if (valStart < 0) return null;
            int valEnd = json.IndexOf('"', valStart + 1);
            if (valEnd < 0) return null;
            return json.Substring(valStart + 1, valEnd - valStart - 1);
        }
        
        static string[] ExtractStringArray(string json, string key)
        {
            string pattern = "\"" + key + "\"";
            int idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return null;
            int colon = json.IndexOf(':', idx + pattern.Length);
            if (colon < 0) return null;
            int arrStart = json.IndexOf('[', colon + 1);
            if (arrStart < 0) return null;
            int arrEnd = FindMatchingBracket(json, arrStart, '[', ']');
            if (arrEnd < 0) return null;
            
            string content = json.Substring(arrStart + 1, arrEnd - arrStart - 1);
            var items = new List<string>();
            int pos = 0;
            while (pos < content.Length)
            {
                int qs = content.IndexOf('"', pos);
                if (qs < 0) break;
                int qe = content.IndexOf('"', qs + 1);
                if (qe < 0) break;
                items.Add(content.Substring(qs + 1, qe - qs - 1));
                pos = qe + 1;
            }
            return items.ToArray();
        }
        
        /// <summary>Extract a float array value from JSON: "key": [1.0, 2.0, 3.0]</summary>
        static float[] ExtractFloatArray(string json, string key)
        {
            string pattern = "\"" + key + "\"";
            int idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return null;
            int colon = json.IndexOf(':', idx + pattern.Length);
            if (colon < 0) return null;
            int arrStart = json.IndexOf('[', colon + 1);
            if (arrStart < 0) return null;
            int arrEnd = FindMatchingBracket(json, arrStart, '[', ']');
            if (arrEnd < 0) return null;
            
            string content = json.Substring(arrStart + 1, arrEnd - arrStart - 1);
            var items = new List<float>();
            foreach (var token in content.Split(','))
            {
                string t = token.Trim();
                if (float.TryParse(t, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float v))
                    items.Add(v);
            }
            return items.Count > 0 ? items.ToArray() : null;
        }
        
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
        //  NLI metadata API (description, color, centroid, LLM context)
        // ═══════════════════════════════════════════════════════════
        
        /// <summary>Dataset name from semantic_dict.json.</summary>
        public string datasetName => m_DatasetName ?? "";
        
        /// <summary>Get per-layer description (from JSON). Empty string if unavailable.</summary>
        public string GetDescription(int semanticId)
        {
            if (m_Descriptions == null || semanticId < 0 || semanticId >= m_Descriptions.Length)
                return "";
            return m_Descriptions[semanticId] ?? "";
        }
        
        /// <summary>Get per-layer default color (from JSON). White if unavailable.</summary>
        public Color GetDefaultColor(int semanticId)
        {
            if (m_DefaultColors == null || semanticId < 0 || semanticId >= m_DefaultColors.Length)
                return Color.white;
            return m_DefaultColors[semanticId];
        }
        
        /// <summary>Get per-layer centroid position (from JSON). Zero if unavailable.</summary>
        public Vector3 GetCentroid(int semanticId)
        {
            if (m_Centroids == null || semanticId < 0 || semanticId >= m_Centroids.Length)
                return Vector3.zero;
            return m_Centroids[semanticId];
        }
        
        /// <summary>Get per-layer aliases (from JSON). Null if unavailable.</summary>
        public string[] GetAliases(int semanticId)
        {
            if (m_Aliases == null || semanticId < 0 || semanticId >= m_Aliases.Length)
                return null;
            return m_Aliases[semanticId];
        }
        
        /// <summary>
        /// Display-friendly name: underscores→spaces, title case.
        /// </summary>
        public string GetDisplayName(int semanticId)
        {
            string raw = GetSemanticName(semanticId);
            if (string.IsNullOrEmpty(raw) || raw.StartsWith("Layer_")) return raw;
            string s = raw.Replace('_', ' ');
            var words = s.Split(' ');
            for (int i = 0; i < words.Length; i++)
                if (words[i].Length > 0)
                    words[i] = char.ToUpper(words[i][0]) + words[i].Substring(1);
            return string.Join(" ", words);
        }
        
        // ═══════════════════════════════════════════════════════════
        //  NLI 命令接口（供语音/文本指令调用）
        // ═══════════════════════════════════════════════════════════
        
        /// <summary>
        /// Find semantic ID by name.
        /// Searches names first (exact, then fuzzy), then aliases.
        /// </summary>
        public int FindSemanticIdByName(string name)
        {
            if (string.IsNullOrEmpty(name) || m_SemanticNames == null)
                return -1;
            
            // Exact match on primary name
            for (int i = 0; i < m_SemanticNames.Length; i++)
            {
                if (string.Equals(m_SemanticNames[i], name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            
            // Exact match on aliases
            if (m_Aliases != null)
            {
                for (int i = 0; i < m_Aliases.Length; i++)
                {
                    if (m_Aliases[i] == null) continue;
                    foreach (var alias in m_Aliases[i])
                    {
                        if (string.Equals(alias, name, StringComparison.OrdinalIgnoreCase))
                            return i;
                    }
                }
            }
            
            // Fuzzy match (Contains) on primary name
            for (int i = 0; i < m_SemanticNames.Length; i++)
            {
                if (!string.IsNullOrEmpty(m_SemanticNames[i]) &&
                    m_SemanticNames[i].IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    return i;
            }
            
            // Fuzzy match on aliases
            if (m_Aliases != null)
            {
                for (int i = 0; i < m_Aliases.Length; i++)
                {
                    if (m_Aliases[i] == null) continue;
                    foreach (var alias in m_Aliases[i])
                    {
                        if (!string.IsNullOrEmpty(alias) &&
                            alias.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                            return i;
                    }
                }
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
