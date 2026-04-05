using UnityEngine;
using GaussianSplatting.Runtime;

/// <summary>
/// NLICommandBus — Routes NLI action commands to SemanticController.
///
/// Replaces the original CommandBusHandlers which targeted per-GameObject
/// SceneGSManager. This version targets per-layer GPU buffers via
/// SemanticController, which is more efficient and supports per-layer tint
/// natively (no isolate+global-tint workaround needed).
///
/// Attach to the same GameObject as GaussianSplatRenderer + SemanticController,
/// or assign references manually.
/// </summary>
public class NLICommandBus : MonoBehaviour
{
    [Header("Core References")]
    [Tooltip("SemanticController on the GS model. Auto-found if null.")]
    public SemanticController semantic;
    
    [Tooltip("GaussianSplatRenderer on the GS model. Auto-found if null.")]
    public GaussianSplatRenderer gsRenderer;
    
    [Header("Optional References")]
    public LegendManager legend;
    public GuidedTourController tour;
    
    void Start()
    {
        if (semantic == null)  semantic = FindObjectOfType<SemanticController>(true);
        if (gsRenderer == null) gsRenderer = FindObjectOfType<GaussianSplatRenderer>(true);
        if (legend == null)    legend = FindObjectOfType<LegendManager>(true);
        if (tour == null)      tour = FindObjectOfType<GuidedTourController>(true);
    }
    
    // Helper: resolve a name/alias to semantic ID, log warning on failure
    int Resolve(string input)
    {
        if (semantic == null || string.IsNullOrWhiteSpace(input)) return -1;
        int id = semantic.FindSemanticIdByName(input.Trim());
        if (id < 0)
            Debug.LogWarning($"[NLICommandBus] Layer not found: '{input}'");
        return id;
    }
    
    // ═══════════════════ Visibility ═══════════════════
    
    /// <summary>Isolate one layer (hide all others).</summary>
    public void Cmd_ShowOnlyLayer(string layerName)
    {
        int id = Resolve(layerName);
        if (id >= 0 && semantic != null)
        {
            semantic.IsolateLayer(id);
            Debug.Log($"[NLICommandBus] Isolate: '{layerName}' (id={id})");
        }
    }
    
    /// <summary>Hide a single layer.</summary>
    public void Cmd_HideLayer(string layerName)
    {
        int id = Resolve(layerName);
        if (id >= 0 && semantic != null)
        {
            semantic.SetVisibility(id, false);
            Debug.Log($"[NLICommandBus] Hide: '{layerName}' (id={id})");
        }
    }
    
    /// <summary>Show a single layer.</summary>
    public void Cmd_ShowLayer(string layerName)
    {
        int id = Resolve(layerName);
        if (id >= 0 && semantic != null)
        {
            semantic.SetVisibility(id, true);
            Debug.Log($"[NLICommandBus] Show: '{layerName}' (id={id})");
        }
    }
    
    /// <summary>
    /// Focus camera on a layer's centroid.
    /// Uses centroid from semantic_dict.json, transformed to world space.
    /// </summary>
    public void Cmd_FocusLayer(string layerName)
    {
        int id = Resolve(layerName);
        if (id < 0 || semantic == null) return;
        
        // Get centroid in local space, convert to world
        Vector3 localCentroid = semantic.GetCentroid(id);
        Vector3 worldPos = semantic.transform.TransformPoint(localCentroid);
        
        var cam = Camera.main;
        if (cam != null)
        {
            float dist = 1.5f;
            cam.transform.position = worldPos - cam.transform.forward * dist;
            cam.transform.LookAt(worldPos);
        }
        
        Debug.Log($"[NLICommandBus] Focus: '{layerName}' → {worldPos}");
    }
    
    // ═══════════════════ Per-Layer Tint ═══════════════════
    
    /// <summary>
    /// Set per-layer color tint. Direct GPU tint — no isolate workaround needed.
    /// </summary>
    public void Cmd_SetLayerColor(string layerName, string htmlColor, float strength = 1f)
    {
        int id = Resolve(layerName);
        if (id < 0 || semantic == null) return;
        if (string.IsNullOrWhiteSpace(htmlColor)) return;
        
        string colorStr = htmlColor.Trim();
        if (!colorStr.StartsWith("#")) colorStr = "#" + colorStr;
        
        if (!ColorUtility.TryParseHtmlString(colorStr, out Color c))
        {
            Debug.LogWarning($"[NLICommandBus] Invalid color: '{htmlColor}'");
            return;
        }
        
        // Apply strength by blending with white
        if (strength > 0 && strength < 1f)
            c = Color.Lerp(Color.white, c, strength);
        
        semantic.SetTint(id, c);
        Debug.Log($"[NLICommandBus] Tint: '{layerName}' → #{ColorUtility.ToHtmlStringRGB(c)}");
    }
    
    /// <summary>
    /// Per-layer opacity simulation via tint alpha.
    /// Sets tint to white with modified alpha. 0 = transparent overlay hint,
    /// 1 = normal. Note: actual per-splat opacity is controlled by the asset,
    /// this provides a visual distinction via color modulation.
    /// </summary>
    public void Cmd_SetLayerOpacity(string layerName, float opacity)
    {
        int id = Resolve(layerName);
        if (id < 0 || semantic == null) return;
        
        if (opacity <= 0.01f)
        {
            // Effectively hide
            semantic.SetVisibility(id, false);
        }
        else
        {
            semantic.SetVisibility(id, true);
            // Use a dim tint to simulate reduced opacity
            float dim = Mathf.Clamp01(opacity);
            semantic.SetTint(id, new Color(dim, dim, dim, 1f));
        }
        
        Debug.Log($"[NLICommandBus] LayerOpacity: '{layerName}' → {opacity:F2}");
    }
    
    // ═══════════════════ Global Render Params ═══════════════════
    
    public void Cmd_SetOpacity(float v)
    {
        if (gsRenderer != null)
        {
            gsRenderer.m_OpacityScale = Mathf.Clamp(v, 0f, 5f);
            Debug.Log($"[NLICommandBus] GlobalOpacity → {v:F2}");
        }
    }
    
    public void Cmd_SetSplatScale(float v)
    {
        if (gsRenderer != null)
        {
            gsRenderer.m_SplatScale = Mathf.Clamp(v, 0.001f, 5f);
            Debug.Log($"[NLICommandBus] SplatScale → {v:F3}");
        }
    }
    
    public void Cmd_SetSHOrder(int v)
    {
        if (gsRenderer != null)
        {
            gsRenderer.m_SHOrder = Mathf.Clamp(v, 0, 4);
            Debug.Log($"[NLICommandBus] SHOrder → {v}");
        }
    }
    
    public void Cmd_SetSHOnly(bool v)
    {
        if (gsRenderer != null)
        {
            gsRenderer.m_SHOnly = v;
            Debug.Log($"[NLICommandBus] SHOnly → {v}");
        }
    }
    
    // ═══════════════════ Legend ═══════════════════
    
    public void Cmd_LegendAdd(string label, string htmlColor)
    {
        if (legend == null) return;
        Color c = Color.white;
        if (!string.IsNullOrEmpty(htmlColor))
        {
            string s = htmlColor.Trim();
            if (!s.StartsWith("#")) s = "#" + s;
            ColorUtility.TryParseHtmlString(s, out c);
        }
        legend.AddOrUpdate(label, c);
    }
    
    public void Cmd_LegendRemove(string label) => legend?.Remove(label);
    public void Cmd_LegendClear() => legend?.ClearAll();
    
    // ═══════════════════ Show All / Reset / Tour / Status ═══════════════════
    
    public void Cmd_ShowAll()
    {
        if (semantic == null) return;
        semantic.SetAllVisibility(true);
        semantic.ResetAllTints();
        Debug.Log("[NLICommandBus] ShowAll");
    }
    
    public void Cmd_Reset()
    {
        semantic?.ResetAll();
        legend?.ClearAll();
        tour?.StopTour();
        Debug.Log("[NLICommandBus] Reset");
    }
    
    public void Cmd_GuidedTour() => tour?.StartTour();
    
    public string Cmd_GetStatus()
    {
        if (semantic == null) return "unavailable";
        int visibleCount = 0;
        var ids = semantic.activeLayerIds;
        if (ids != null)
            foreach (int id in ids)
                if (semantic.GetVisibility(id)) visibleCount++;
        string ds = semantic.datasetName;
        return $"{{\"dataset\":\"{ds}\",\"layers\":{semantic.activeLayerCount}," +
               $"\"visible\":{visibleCount}}}";
    }
    
    // ═══════════════════ Context for LLM ═══════════════════
    
    /// <summary>
    /// Build a context string describing all active layers for LLM system prompt.
    /// Reads metadata from SemanticController's public API.
    /// </summary>
    public string GetLLMContext()
    {
        if (semantic == null) return "No SemanticController available.\n";
        
        var ids = semantic.activeLayerIds;
        if (ids == null || ids.Length == 0)
            return "No semantic layers loaded.\n";
        
        var sb = new System.Text.StringBuilder(1024);
        string ds = semantic.datasetName;
        if (string.IsNullOrEmpty(ds)) ds = "unknown";
        sb.AppendLine($"Dataset '{ds}' has {ids.Length} semantic components:");
        
        foreach (int id in ids)
        {
            string name = semantic.GetSemanticName(id);
            int count = semantic.GetLayerSplatCount(id);
            string desc = semantic.GetDescription(id);
            var aliases = semantic.GetAliases(id);
            
            sb.Append($"  - \"{name}\" (id={id}, {count:N0} splats)");
            if (!string.IsNullOrEmpty(desc))
                sb.Append($": {desc}");
            if (aliases != null && aliases.Length > 0)
                sb.Append($" [aliases: {string.Join(", ", aliases)}]");
            sb.AppendLine();
        }
        
        sb.AppendLine("Use exact component names (or aliases) in 'value' or 'target' fields.");
        return sb.ToString();
    }
    
    /// <summary>Get all layer names for the available-components hint.</summary>
    public string[] GetLayerNames()
    {
        if (semantic == null || semantic.activeLayerIds == null) return System.Array.Empty<string>();
        var ids = semantic.activeLayerIds;
        var names = new string[ids.Length];
        for (int i = 0; i < ids.Length; i++)
            names[i] = semantic.GetSemanticName(ids[i]);
        return names;
    }
}
