using System;
using System.Collections.Generic;
using System.Reflection;
using MixedReality.Toolkit.UX.Experimental;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// VRKeyboardBridge v11 — Nuclear Clear via Full Reflection Scan.
///
/// WHY v10 FAILED:
///   Only TMP_Text cleared. But NonNativeKeyboard stores text in private
///   fields (e.g. m_text, _text). Keyboard's own Update() re-writes
///   TMP_Text from its internal buffer every frame. So TMP_Text clear
///   is immediately overwritten.
///
/// v11 FIX:
///   On Start(), scan ALL string fields (public + private + inherited)
///   of the keyboard component. On ClearAll(), set ALL of them to "".
///   Also log every field found at startup so we can see what's there.
/// </summary>
public class VRKeyboardBridge : MonoBehaviour
{
    [Header("Chat References")]
    public TMP_InputField inputField;
    public NLIChatController chat;
    public Button sendButton;

    [Header("Voice")]
    public VoiceInputController voiceInput;
    public Button micButton;

    [Header("MRTK3 Keyboard")]
    public MonoBehaviour keyboardComponent;

    [Header("Scale")]
    public Vector3 keyboardScale = new(0.0015f, 0.0015f, 0.0015f);

    [Header("Debug")]
    [SerializeField] string keyboardStatus = "Not initialized";
    [SerializeField] string readMode = "None";
    [SerializeField] bool enterButtonFound = false;

    // All string fields on the keyboard component (for nuclear clear)
    List<FieldInfo> _allStringFields = new();
    // All string properties that are writable
    List<PropertyInfo> _allStringProps = new();
    // Read source (best available)
    PropertyInfo _readProp;
    MethodInfo _clearMethod;
    TMP_InputField _kbInputField;
    TMP_Text _kbTmpText;
    NonNativeKeyboard _nnk;

    GameObject _kbGo;
    bool _ready;
    string _lastSyncedText = "";
    bool _skipSync;
    int _clearRetries;

    string _staleText = "";
    bool _ignoreUntilTextChanges;

    // ═══════════════════ Lifecycle ═══════════════════

    void Start()
    {
        if (sendButton != null)
            sendButton.onClick.AddListener(OnSendTriggered);

        if (micButton != null && voiceInput != null)
            micButton.onClick.AddListener(() => voiceInput.OnMicToggle());

        if (inputField != null)
        {
            inputField.onSubmit.RemoveAllListeners();
            inputField.onEndEdit.RemoveAllListeners();
        }

        if (keyboardComponent == null)
        {
            keyboardStatus = "No keyboard assigned.";
            return;
        }

        _kbGo = keyboardComponent.gameObject;
        _nnk = keyboardComponent as NonNativeKeyboard;
        if (_nnk == null)
            _nnk = _kbGo.GetComponentInParent<NonNativeKeyboard>(true)
                   ?? _kbGo.GetComponentInChildren<NonNativeKeyboard>(true)
                   ?? NonNativeKeyboard.Instance;

        if (_nnk != null && _kbGo != _nnk.gameObject)
            _kbGo = _nnk.gameObject;

        if (keyboardScale.sqrMagnitude > 0)
            _kbGo.transform.localScale = keyboardScale;
        if (!_kbGo.activeSelf)
            _kbGo.SetActive(true);

        ScanKeyboardReflection();
        FindDirectReferences();
        FindAndSubscribeEnterButton();

        if (_nnk != null) readMode = "NonNativeKeyboard.Text";

        _ready = (_nnk != null || _readProp != null || _kbTmpText != null || _allStringFields.Count > 0);
        keyboardStatus = _ready
            ? $"Ready (read:{readMode}, fields:{_allStringFields.Count}, props:{_allStringProps.Count}, enter:{(enterButtonFound ? "YES" : "NO")})"
            : "FAILED";
        Debug.Log($"[KBBridge] {keyboardStatus}");
    }

    void Update()
    {
        if (!_ready) return;

        string raw = ReadText();
        if (raw == null) return;

        string clean = CleanText(raw);

        if (_skipSync)
        {
            if (_ignoreUntilTextChanges && clean == _staleText)
                return;

            _skipSync = false;
            _ignoreUntilTextChanges = false;
            _lastSyncedText = "";

            if (clean.Length == 0)
                return;
        }

        if (clean != _lastSyncedText)
        {
            _lastSyncedText = clean;
            if (inputField != null)
                inputField.text = clean;
        }
    }

    // ═══════════════════ Full Reflection Scan ═══════════════════

    /// <summary>
    /// Scan ALL fields and properties on the keyboard component type
    /// (including private, inherited). Log everything for debugging.
    /// </summary>
    void ScanKeyboardReflection()
    {
        var type = keyboardComponent.GetType();
        Debug.Log($"[KBBridge] === Scanning type: {type.FullName} ===");

        // Scan fields (all levels of inheritance)
        var currentType = type;
        while (currentType != null && currentType != typeof(MonoBehaviour))
        {
            var fields = currentType.GetFields(
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.DeclaredOnly);

            foreach (var f in fields)
            {
                if (f.FieldType == typeof(string))
                {
                    string val = "(null)";
                    try { val = f.GetValue(keyboardComponent) as string ?? "(null)"; } catch { }
                    Debug.Log($"[KBBridge]   FIELD: {currentType.Name}.{f.Name} = '{val}'");
                    _allStringFields.Add(f);
                }
            }
            currentType = currentType.BaseType;
        }

        // Scan properties
        currentType = type;
        while (currentType != null && currentType != typeof(MonoBehaviour))
        {
            var props = currentType.GetProperties(
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.DeclaredOnly);

            foreach (var p in props)
            {
                if (p.PropertyType == typeof(string) && p.CanRead)
                {
                    string val = "(null)";
                    try { val = p.GetValue(keyboardComponent) as string ?? "(null)"; } catch { }
                    Debug.Log($"[KBBridge]   PROP: {currentType.Name}.{p.Name} = '{val}' (write={p.CanWrite})");

                    if (p.CanRead)
                    {
                        if (_readProp == null)
                        {
                            _readProp = p;
                            readMode = $"prop:{p.Name}";
                        }
                    }
                    if (p.CanWrite)
                        _allStringProps.Add(p);
                }
            }
            currentType = currentType.BaseType;
        }

        // Scan methods for Clear
        foreach (string n in new[] { "Clear", "ClearKeyboardText", "ClearText" })
        {
            var m = type.GetMethod(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                                   null, Type.EmptyTypes, null);
            if (m != null)
            {
                _clearMethod = m;
                Debug.Log($"[KBBridge]   METHOD: {n}()");
                break;
            }
        }

        Debug.Log($"[KBBridge] Scan complete: {_allStringFields.Count} string fields, {_allStringProps.Count} writable string props");
    }

    void FindDirectReferences()
    {
        // Internal TMP_InputField
        _kbInputField = _kbGo.GetComponentInChildren<TMP_InputField>(true);
        if (_kbInputField != null)
            Debug.Log($"[KBBridge] Internal InputField: {_kbInputField.name}");

        // "Text Area / Text (TMP)"
        var textArea = FindChildRecursive(_kbGo.transform, "Text Area");
        if (textArea != null)
        {
            _kbTmpText = textArea.GetComponentInChildren<TMP_Text>(true);
            if (_kbTmpText != null)
            {
                Debug.Log($"[KBBridge] TMP_Text: {textArea.name}/{_kbTmpText.name}");
                if (_readProp == null) readMode = $"TMP:{_kbTmpText.name}";
            }
        }
    }

    // ═══════════════════ Enter Button ═══════════════════

    void FindAndSubscribeEnterButton()
    {
        if (_kbGo == null) return;

        string[] names = { "Enter_Button", "Enter", "EnterButton",
                           "Return_Button", "Return", "ReturnButton" };
        Button btn = null;
        foreach (string n in names)
        {
            var t = FindChildRecursive(_kbGo.transform, n);
            if (t != null) { btn = t.GetComponent<Button>(); if (btn != null) break; }
        }

        if (btn == null)
        {
            foreach (var b in _kbGo.GetComponentsInChildren<Button>(true))
            {
                string lo = b.name.ToLowerInvariant();
                if (lo.Contains("enter") || lo.Contains("return"))
                { btn = b; break; }
            }
        }

        if (btn != null)
        {
            btn.onClick.AddListener(OnEnterKeyPressed);
            enterButtonFound = true;
            Debug.Log($"[KBBridge] Enter button: {btn.name}");
        }
        else
        {
            Debug.LogWarning("[KBBridge] No Enter button found.");
        }
    }

    // ═══════════════════ Send ═══════════════════

    void OnEnterKeyPressed()
    {
        SyncNow();
        if (inputField == null || string.IsNullOrWhiteSpace(inputField.text)) return;
        Debug.Log($"[KBBridge] ENTER → '{inputField.text}'");
        DoSend();
    }

    public void OnSendTriggered()
    {
        SyncNow();
        if (inputField == null || string.IsNullOrWhiteSpace(inputField.text)) return;
        Debug.Log($"[KBBridge] SEND → '{inputField.text}'");
        DoSend();
    }

    public void OnSendPressed() => OnSendTriggered();

    void SyncNow()
    {
        string raw = ReadText();
        if (raw == null) return;
        string clean = CleanText(raw).Trim();
        if (!string.IsNullOrEmpty(clean) && inputField != null)
            inputField.text = clean;
    }

    // ═══════════════════ Send + Nuclear Clear ═══════════════════

    void DoSend()
    {
        _staleText = CleanText(ReadText() ?? inputField?.text ?? "");
        _ignoreUntilTextChanges = true;
        _skipSync = true;
        _clearRetries = 0;

        ClearKeyboardImmediate();

        chat?.OnSendClicked();
        Invoke(nameof(NuclearClear), 0.08f);
    }

    void ClearKeyboardImmediate()
    {
        var kb = _nnk != null ? _nnk : NonNativeKeyboard.Instance;
        if (kb != null)
        {
            kb.Clear();
            if (kb.Preview != null)
            {
                kb.Preview.Text = "";
                kb.Preview.CaretIndex = 0;
            }
            Debug.Log("[KBBridge]   ✓ NonNativeKeyboard.Clear()");
        }
        else if (_clearMethod != null)
        {
            try { _clearMethod.Invoke(keyboardComponent, null); } catch { }
        }
    }

    /// <summary>
    /// NUCLEAR CLEAR: set every string field and property to "",
    /// call Clear(), clear TMP_InputField, clear TMP_Text.
    /// </summary>
    void NuclearClear()
    {
        Debug.Log("[KBBridge] === NuclearClear ===");

        // 1) Clear method
        if (_clearMethod != null)
            try { _clearMethod.Invoke(keyboardComponent, null); Debug.Log("[KBBridge]   ✓ Clear()"); }
            catch (Exception e) { Debug.Log($"[KBBridge]   ✗ Clear(): {e.Message}"); }

        // 2) ALL string fields → ""
        foreach (var f in _allStringFields)
        {
            try
            {
                string before = f.GetValue(keyboardComponent) as string ?? "";
                if (before.Length > 0)
                {
                    f.SetValue(keyboardComponent, "");
                    Debug.Log($"[KBBridge]   ✓ field {f.Name}: '{before}' → ''");
                }
            }
            catch (Exception e) { Debug.Log($"[KBBridge]   ✗ field {f.Name}: {e.Message}"); }
        }

        // 3) ALL writable string properties → ""
        foreach (var p in _allStringProps)
        {
            try
            {
                string before = p.GetValue(keyboardComponent) as string ?? "";
                if (before.Length > 0)
                {
                    p.SetValue(keyboardComponent, "");
                    Debug.Log($"[KBBridge]   ✓ prop {p.Name}: '{before}' → ''");
                }
            }
            catch (Exception e) { Debug.Log($"[KBBridge]   ✗ prop {p.Name}: {e.Message}"); }
        }

        // 4) Internal TMP_InputField
        if (_kbInputField != null)
            try { _kbInputField.text = ""; Debug.Log("[KBBridge]   ✓ kbInputField=''"); }
            catch { }

        // 5) Direct TMP_Text
        if (_kbTmpText != null)
            try { _kbTmpText.text = ""; Debug.Log("[KBBridge]   ✓ kbTmpText=''"); }
            catch { }

        // 6) Panel InputField
        if (inputField != null)
            inputField.text = "";

        _lastSyncedText = "";

        Invoke(nameof(VerifyAndResume), 0.15f);
    }

    void VerifyAndResume()
    {
        string remaining = ReadText();
        string cleaned = CleanText(remaining).Trim();

        if (cleaned.Length > 0 && _clearRetries < 3)
        {
            _clearRetries++;
            Debug.Log($"[KBBridge] Still has text (retry {_clearRetries}): '{cleaned}'");
            NuclearClear();
            return;
        }

        if (cleaned.Length > 0)
        {
            Debug.LogWarning($"[KBBridge] Could not fully clear. Remaining: '{cleaned}'");
            _staleText = cleaned;
            _ignoreUntilTextChanges = true;
            if (inputField != null) inputField.text = "";
            Debug.Log("[KBBridge] Sync paused until keyboard text changes.");
            return;
        }

        if (inputField != null) inputField.text = "";
        _lastSyncedText = "";
        _skipSync = false;
        _ignoreUntilTextChanges = false;
        Debug.Log("[KBBridge] Sync resumed.");
    }

    // ═══════════════════ Read ═══════════════════

    string ReadText()
    {
        var kb = _nnk != null ? _nnk : NonNativeKeyboard.Instance;
        if (kb != null)
            return kb.Text;

        if (_readProp != null)
            try { return _readProp.GetValue(keyboardComponent) as string; } catch { }

        if (_kbTmpText != null)
            try { return _kbTmpText.text; } catch { }

        // Fallback: try first non-empty string field
        foreach (var f in _allStringFields)
        {
            try
            {
                string v = f.GetValue(keyboardComponent) as string;
                if (!string.IsNullOrEmpty(v)) return v;
            }
            catch { }
        }

        return null;
    }

    static string CleanText(string raw) => raw?.Replace("\n", "").Replace("\r", "") ?? "";

    // ═══════════════════ Utility ═══════════════════

    static Transform FindChildRecursive(Transform parent, string name)
    {
        if (parent == null) return null;
        for (int i = 0; i < parent.childCount; i++)
        {
            var c = parent.GetChild(i);
            if (string.Equals(c.name, name, StringComparison.OrdinalIgnoreCase)) return c;
        }
        for (int i = 0; i < parent.childCount; i++)
        {
            var r = FindChildRecursive(parent.GetChild(i), name);
            if (r != null) return r;
        }
        return null;
    }

#if UNITY_EDITOR
    [ContextMenu("Dump All Keyboard State")]
    void DumpState()
    {
        if (keyboardComponent == null) { Debug.Log("No keyboard."); return; }

        Debug.Log("=== String Fields ===");
        foreach (var f in _allStringFields)
        {
            try { Debug.Log($"  {f.DeclaringType.Name}.{f.Name} = '{f.GetValue(keyboardComponent)}'"); }
            catch { Debug.Log($"  {f.Name} = (error)"); }
        }

        Debug.Log("=== String Props ===");
        foreach (var p in _allStringProps)
        {
            try { Debug.Log($"  {p.DeclaringType.Name}.{p.Name} = '{p.GetValue(keyboardComponent)}'"); }
            catch { Debug.Log($"  {p.Name} = (error)"); }
        }

        if (_kbInputField != null) Debug.Log($"=== InputField = '{_kbInputField.text}' ===");
        if (_kbTmpText != null) Debug.Log($"=== TMP_Text = '{_kbTmpText.text}' ===");
    }

    [ContextMenu("Force Nuclear Clear")]
    void ForceNuclear() { _skipSync = true; _clearRetries = 0; NuclearClear(); }
#endif
}