using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>LegendManager v2 ¡ª Dynamic legend panel with colored labels.</summary>
public class LegendManager : MonoBehaviour
{
    [Header("Legend Container")]
    public Transform legendContainer;
    public GameObject legendEntryPrefab;

    [Header("Appearance")]
    public float swatchSize = 16f;
    public float fontSize = 14f;
    public Color textColor = Color.white;

    readonly List<(string label, Color color, GameObject go)> _entries = new();

    void Awake()
    {
        if (legendContainer == null) legendContainer = transform;
        var vlg = legendContainer.GetComponent<VerticalLayoutGroup>();
        if (vlg == null) { vlg = legendContainer.gameObject.AddComponent<VerticalLayoutGroup>(); vlg.spacing = 4; vlg.childAlignment = TextAnchor.UpperLeft; }
    }

    public void AddOrUpdate(string label, Color color)
    {
        for (int i = 0; i < _entries.Count; i++)
            if (string.Equals(_entries[i].label, label, System.StringComparison.OrdinalIgnoreCase))
            {
                var img = _entries[i].go.GetComponentInChildren<Image>(); if (img) img.color = color;
                _entries[i] = (label, color, _entries[i].go); return;
            }
        var go = CreateEntry(label, color);
        go.transform.SetParent(legendContainer, false);
        _entries.Add((label, color, go));
    }

    public void Remove(string label)
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
            if (string.Equals(_entries[i].label, label, System.StringComparison.OrdinalIgnoreCase))
            { Destroy(_entries[i].go); _entries.RemoveAt(i); }
    }

    public void ClearAll() { foreach (var e in _entries) if (e.go) Destroy(e.go); _entries.Clear(); }

    GameObject CreateEntry(string label, Color color)
    {
        if (legendEntryPrefab != null)
        {
            var inst = Instantiate(legendEntryPrefab);
            var img = inst.GetComponentInChildren<Image>(); if (img) img.color = color;
            var txt = inst.GetComponentInChildren<TMP_Text>(); if (txt) { txt.text = label; txt.color = textColor; }
            return inst;
        }
        var row = new GameObject($"Legend_{label}", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        var hlg = row.GetComponent<HorizontalLayoutGroup>(); hlg.spacing = 6; hlg.childAlignment = TextAnchor.MiddleLeft;

        var sw = new GameObject("Swatch", typeof(RectTransform), typeof(Image));
        sw.transform.SetParent(row.transform, false);
        sw.GetComponent<Image>().color = color;
        var le = sw.AddComponent<LayoutElement>(); le.preferredWidth = swatchSize; le.preferredHeight = swatchSize;

        var tgo = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        tgo.transform.SetParent(row.transform, false);
        var tmp = tgo.GetComponent<TextMeshProUGUI>(); tmp.text = label; tmp.fontSize = fontSize; tmp.color = textColor;
        var tle = tgo.AddComponent<LayoutElement>(); tle.preferredWidth = 150;
        return row;
    }
}