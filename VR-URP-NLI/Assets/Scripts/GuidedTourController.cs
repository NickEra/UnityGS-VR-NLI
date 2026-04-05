using System.Collections;
using UnityEngine;
using GaussianSplatting.Runtime;

/// <summary>
/// GuidedTourController v10 — Adapted for SemanticController architecture.
///
/// Key difference from v9:
///   SemanticController has per-layer GPU tint. No need to isolate+global-tint.
///   Each step: tint the current layer with its color, dim others via tint,
///   then restore. Much cleaner than the v9 workaround.
/// </summary>
public class GuidedTourController : MonoBehaviour
{
    [Header("References")]
    public SemanticController semantic;
    public NLIChatController chat;
    public LegendManager legend;

    [Header("Timing")]
    [Range(1f, 15f)]
    [Tooltip("Seconds to display each layer (highlighted)")]
    public float pausePerLayer = 4f;

    [Range(0.3f, 3f)]
    [Tooltip("Seconds to briefly show all layers between steps")]
    public float contextPause = 1f;

    [Header("Visual")]
    [Tooltip("Dim color applied to non-active layers during highlight")]
    public Color dimTint = new Color(0.3f, 0.3f, 0.3f, 1f);

    [Tooltip("Briefly show all layers (normal) between steps for spatial context")]
    public bool showContextBetweenSteps = true;

    Coroutine _tour;

    void Start()
    {
        if (semantic == null) semantic = FindObjectOfType<SemanticController>(true);
    }

    public void StartTour()
    {
        if (_tour != null) StopCoroutine(_tour);
        _tour = StartCoroutine(TourRoutine());
    }

    public void StopTour()
    {
        if (_tour != null) { StopCoroutine(_tour); _tour = null; }
        RestoreAll();
    }

    IEnumerator TourRoutine()
    {
        if (semantic == null) yield break;

        var ids = semantic.activeLayerIds;
        if (ids == null || ids.Length == 0)
        {
            chat?.AppendTourMessage("No semantic layers loaded.");
            yield break;
        }

        string ds = semantic.datasetName;
        if (string.IsNullOrEmpty(ds)) ds = "dataset";
        chat?.SetStatus("Guided Tour");
        chat?.AppendTourMessage($"Touring '{ds}' \u2014 {ids.Length} layers");
        legend?.ClearAll();

        for (int idx = 0; idx < ids.Length; idx++)
        {
            int layerId = ids[idx];
            string name = semantic.GetSemanticName(layerId);
            string displayName = semantic.GetDisplayName(layerId);
            string desc = semantic.GetDescription(layerId);
            Color layerColor = semantic.GetDefaultColor(layerId);
            int splatCount = semantic.GetLayerSplatCount(layerId);
            string colorHex = ColorUtility.ToHtmlStringRGB(layerColor);

            // ═══ Step A: Highlight current layer, dim others ═══
            
            // Show all layers
            semantic.SetAllVisibility(true);
            
            // Tint: highlight current, dim others
            foreach (int id in ids)
            {
                if (id == layerId)
                    semantic.SetTint(id, layerColor);   // Highlight with its color
                else
                    semantic.SetTint(id, dimTint);       // Dim others
            }

            // Add to legend (accumulates)
            legend?.AddOrUpdate(displayName, layerColor);

            // Chat message
            string msg;
            if (string.IsNullOrEmpty(desc))
                msg = $"[{idx + 1}/{ids.Length}] <color=#{colorHex}>{displayName}</color> ({splatCount:N0} splats)";
            else
                msg = $"[{idx + 1}/{ids.Length}] <color=#{colorHex}>{displayName}</color>: {desc}";
            chat?.AppendTourMessage(msg);

            Debug.Log($"[GuidedTour] [{idx + 1}/{ids.Length}] {name} (id={layerId}) \u2192 #{colorHex}");

            // Hold
            yield return new WaitForSeconds(pausePerLayer);

            // ═══ Step B: Brief context flash (show all normal) ═══
            if (showContextBetweenSteps && idx < ids.Length - 1)
            {
                semantic.ResetAllTints();
                yield return new WaitForSeconds(contextPause);
            }
        }

        // ═══ End: Restore all ═══
        RestoreAll();
        chat?.AppendTourMessage("Tour complete.");
        chat?.SetStatus("Ready");
        _tour = null;
    }

    void RestoreAll()
    {
        if (semantic == null) return;
        semantic.SetAllVisibility(true);
        semantic.ResetAllTints();
        // Keep legend visible for reference
    }
}
