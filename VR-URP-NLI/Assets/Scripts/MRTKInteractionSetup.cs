using System;
using System.Reflection;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Auto-configures MRTK3 interaction on carp-3dgs and Panel_NLI.
///
/// MRTK3 key differences from MRTK2:
///   - ObjectManipulator → Microsoft.MixedReality.Toolkit.SpatialManipulation.ObjectManipulator
///   - No NearInteractionGrabbable — GrabInteractor on hand controllers handles this
///   - ConstraintManager → same namespace as ObjectManipulator
///   - Interaction driven by interactors on MRTK XR Rig (PokeInteractor, GrabInteractor, etc.)
///
/// Attach to Object Management, assign targets, run via:
///   - Editor: ContextMenu "Setup All MRTK3 Interaction"
///   - Runtime: auto-runs in Start()
///
/// carp-3dgs: ObjectManipulator(Everything), BoxCollider, Rigidbody(kinematic)
/// Panel_NLI: ObjectManipulator(Move only), BoxCollider(RectTransform-sized), Rigidbody(kinematic)
/// </summary>
public class MRTKInteractionSetup : MonoBehaviour
{
    [Header("3D Model (full manipulation)")]
    [Tooltip("The GS model root, e.g. carp-3dgs")]
    public GameObject modelTarget;

    [Header("UI Panel (move only)")]
    [Tooltip("The NLI panel, e.g. Panel_NLI")]
    public GameObject panelTarget;

    [Header("Model Collider")]
    public bool autoSizeModelCollider = true;
    public Vector3 modelColliderCenter = Vector3.zero;
    public Vector3 modelColliderSize = Vector3.one;

    [Header("Panel Collider")]
    public float panelColliderDepth = 0.02f;

    // ── MRTK3 type names (searched in order) ──
    static readonly string[] ObjectManipulatorTypes = {
        "Microsoft.MixedReality.Toolkit.SpatialManipulation.ObjectManipulator",  // MRTK3
        "Microsoft.MixedReality.Toolkit.UI.ObjectManipulator",                   // MRTK2 fallback
        "MixedReality.Toolkit.SpatialManipulation.ObjectManipulator",            // alternate
    };

    static readonly string[] ConstraintManagerTypes = {
        "Microsoft.MixedReality.Toolkit.SpatialManipulation.ConstraintManager",  // MRTK3
        "Microsoft.MixedReality.Toolkit.UI.ConstraintManager",                   // MRTK2 fallback
    };

    void Start() => Setup();

    [ContextMenu("Setup All MRTK3 Interaction")]
    public void Setup()
    {
        Debug.Log("[MRTKSetup] === Starting MRTK3 Interaction Setup ===");

        // Discover types once
        var omType = FindAnyType(ObjectManipulatorTypes);
        var cmType = FindAnyType(ConstraintManagerTypes);

        if (omType != null) Debug.Log($"[MRTKSetup] Found ObjectManipulator: {omType.FullName}");
        else Debug.LogError("[MRTKSetup] ObjectManipulator NOT FOUND in any assembly! See manual checklist.");

        if (cmType != null) Debug.Log($"[MRTKSetup] Found ConstraintManager: {cmType.FullName}");

        if (modelTarget != null) SetupModel(modelTarget, omType, cmType);
        else Debug.LogWarning("[MRTKSetup] modelTarget is null.");

        if (panelTarget != null) SetupPanel(panelTarget, omType, cmType);
        else Debug.LogWarning("[MRTKSetup] panelTarget is null.");

        Debug.Log("[MRTKSetup] === Setup Complete ===");
    }

    // ═══════════════════ MODEL (carp-3dgs) ═══════════════════

    void SetupModel(GameObject go, Type omType, Type cmType)
    {
        Debug.Log($"[MRTKSetup] ── Model: {go.name} ──");

        // 1. Rigidbody (kinematic, no gravity)
        var rb = Ensure<Rigidbody>(go);
        rb.isKinematic = true;
        rb.useGravity = false;
        Debug.Log($"[MRTKSetup]  ✓ Rigidbody: kinematic=true, gravity=false");

        // 2. BoxCollider
        var col = Ensure<BoxCollider>(go);
        if (autoSizeModelCollider)
        {
            var bounds = ComputeLocalBounds(go.transform);
            if (bounds.size.sqrMagnitude > 0.001f)
            {
                col.center = bounds.center;
                col.size = bounds.size + Vector3.one * 0.05f;
                Debug.Log($"[MRTKSetup]  ✓ BoxCollider auto-sized: center={col.center}, size={col.size}");
            }
            else
            {
                col.center = modelColliderCenter;
                col.size = modelColliderSize;
                Debug.LogWarning("[MRTKSetup]  ⚠ No renderer bounds. Using manual values — adjust BoxCollider in Inspector!");
            }
        }

        // 3. ConstraintManager → manual
        if (cmType != null) ConfigureConstraintManager(go, cmType);

        // 4. ObjectManipulator → Everything
        if (omType != null)
        {
            var om = EnsureComponent(go, omType);
            SetProperty(om, "HostTransform", go.transform);

            // MRTK3: AllowedManipulations flags (Move=1, Rotate=2, Scale=4, Everything=7)
            TrySetEnumProperty(om, "AllowedManipulations", 7);  // Everything

            // MRTK3: AllowedInteractionTypes (Near=1, Far/Ray=2, Gaze=4, Everything/All)
            TrySetEnumProperty(om, "AllowedInteractionTypes", -1);  // -1 = Everything/All flags

            Debug.Log($"[MRTKSetup]  ✓ ObjectManipulator: Everything + All interaction types");
        }

        // 5. MRTK3: No NearInteractionGrabbable needed!
        // GrabInteractor on MRTK XR Rig handles near-grab via collider detection.
        Debug.Log($"[MRTKSetup]  ✓ Model setup complete. MRTK3 GrabInteractor will detect BoxCollider automatically.");
    }

    // ═══════════════════ PANEL (Panel_NLI) ═══════════════════

    void SetupPanel(GameObject go, Type omType, Type cmType)
    {
        Debug.Log($"[MRTKSetup] ── Panel: {go.name} ──");

        // 1. Rigidbody
        var rb = Ensure<Rigidbody>(go);
        rb.isKinematic = true;
        rb.useGravity = false;
        Debug.Log($"[MRTKSetup]  ✓ Rigidbody: kinematic=true, gravity=false");

        // 2. BoxCollider (sized to RectTransform)
        var col = Ensure<BoxCollider>(go);
        var rt = go.GetComponent<RectTransform>();
        if (rt != null)
        {
            col.center = Vector3.zero;
            col.size = new Vector3(rt.rect.width, rt.rect.height, panelColliderDepth);
            Debug.Log($"[MRTKSetup]  ✓ BoxCollider from RectTransform: {col.size}");
        }

        // 3. ConstraintManager → manual
        if (cmType != null) ConfigureConstraintManager(go, cmType);

        // 4. ObjectManipulator → Move only, Ray/Far only
        if (omType != null)
        {
            var om = EnsureComponent(go, omType);
            SetProperty(om, "HostTransform", go.transform);
            TrySetEnumProperty(om, "AllowedManipulations", 1);    // Move only
            TrySetEnumProperty(om, "AllowedInteractionTypes", 2); // Far/Ray only

            Debug.Log($"[MRTKSetup]  ✓ ObjectManipulator: Move + Ray only (near reserved for UI buttons)");
        }

        Debug.Log($"[MRTKSetup]  ✓ Panel setup complete. PokeInteractor will handle button clicks.");
    }

    // ═══════════════════ Helpers ═══════════════════

    void ConfigureConstraintManager(GameObject go, Type cmType)
    {
        var cm = EnsureComponent(go, cmType);
        // Try multiple field/property names for auto-constraint toggle
        if (!TrySetProperty(cm, "AutoConstraintSelection", false))
            TrySetProperty(cm, "autoConstraintSelection", false);
        Debug.Log($"[MRTKSetup]  ✓ ConstraintManager: manual mode");
    }

    static T Ensure<T>(GameObject go) where T : Component
    {
        var c = go.GetComponent<T>();
        return c != null ? c : go.AddComponent<T>();
    }

    static Component EnsureComponent(GameObject go, Type type)
    {
        var c = go.GetComponent(type);
        return c != null ? c : go.AddComponent(type);
    }

    static bool TrySetProperty(Component comp, string name, object value)
    {
        var type = comp.GetType();
        var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

        var prop = type.GetProperty(name, flags);
        if (prop != null && prop.CanWrite) { prop.SetValue(comp, value); return true; }

        var field = type.GetField(name, flags);
        if (field != null) { field.SetValue(comp, value); return true; }

        return false;
    }

    static void SetProperty(Component comp, string name, object value)
    {
        if (!TrySetProperty(comp, name, value))
            Debug.LogWarning($"[MRTKSetup] Could not set '{name}' on {comp.GetType().Name}. Set manually in Inspector.");
    }

    static void TrySetEnumProperty(Component comp, string name, int value)
    {
        var type = comp.GetType();
        var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

        // Try property first
        var prop = type.GetProperty(name, flags);
        if (prop != null && prop.CanWrite && prop.PropertyType.IsEnum)
        {
            prop.SetValue(comp, Enum.ToObject(prop.PropertyType, value));
            return;
        }

        // Try field
        var field = type.GetField(name, flags);
        if (field != null && field.FieldType.IsEnum)
        {
            field.SetValue(comp, Enum.ToObject(field.FieldType, value));
            return;
        }

        // Try serialized field name variations
        string[] variants = { name, char.ToLower(name[0]) + name.Substring(1),
            "m_" + name, "_" + char.ToLower(name[0]) + name.Substring(1) };
        foreach (var v in variants)
        {
            field = type.GetField(v, BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null && field.FieldType.IsEnum)
            {
                field.SetValue(comp, Enum.ToObject(field.FieldType, value));
                return;
            }
        }

        Debug.LogWarning($"[MRTKSetup] Could not set enum '{name}' on {comp.GetType().Name}. Set manually in Inspector.");
    }

    static Type FindAnyType(string[] candidates)
    {
        foreach (var name in candidates)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(name);
                if (t != null) return t;
            }
        }
        return null;
    }

    static Bounds ComputeLocalBounds(Transform root)
    {
        var renderers = root.GetComponentsInChildren<Renderer>(true);
        if (renderers == null || renderers.Length == 0)
            return new Bounds(Vector3.zero, Vector3.zero);

        Bounds world = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            world.Encapsulate(renderers[i].bounds);

        Vector3 c = root.InverseTransformPoint(world.center);
        Vector3 s = root.InverseTransformVector(world.size);
        return new Bounds(c, new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z)));
    }

#if UNITY_EDITOR
    [ContextMenu("Setup All (Editor + Undo)")]
    void SetupEditor()
    {
        if (modelTarget != null) Undo.RecordObject(modelTarget, "MRTK3 Model");
        if (panelTarget != null) Undo.RecordObject(panelTarget, "MRTK3 Panel");
        Setup();
        if (modelTarget != null) EditorUtility.SetDirty(modelTarget);
        if (panelTarget != null) EditorUtility.SetDirty(panelTarget);
    }
#endif
}