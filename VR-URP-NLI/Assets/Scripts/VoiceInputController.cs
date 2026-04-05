using UnityEngine;
using UnityEngine.UI;
using TMPro;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using UnityEngine.Windows.Speech;
#endif

/// <summary>
/// VoiceInputController v2 — Local STT via Windows DictationRecognizer.
///
/// OnMicToggle() is safe to call from ANY button onClick, including
/// MRTK3 PressableButton. It handles all error cases gracefully.
///
/// BINDING THE MRTK3 KEYBOARD AUDIO BUTTON:
///   Option A (recommended): Wire to VRKeyboardBridge.OnMicButtonPressed()
///   Option B (direct): Wire to VoiceInputController.OnMicToggle()
///   Both work. Option A adds extra logging.
/// </summary>
public class VoiceInputController : MonoBehaviour
{
    [Header("References")]
    public TMP_InputField inputField;
    public Button micButton;
    public NLIChatController chat;

    [Header("Settings")]
    public float autoStopSeconds = 10f;

    [Header("Debug")]
    [SerializeField] string micStatus = "Idle";

    bool _recording;
    float _recordTimer;

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    DictationRecognizer _dictation;
    bool _initialized;

    void Start()
    {
        if (micButton != null)
            micButton.onClick.AddListener(OnMicToggle);

        try
        {
            _dictation = new DictationRecognizer();
            _dictation.DictationResult += OnDictationResult;
            _dictation.DictationComplete += OnDictationComplete;
            _dictation.DictationError += OnDictationError;
            _initialized = true;
            micStatus = "Ready";
            Debug.Log("[Voice] DictationRecognizer initialized.");
        }
        catch (System.Exception e)
        {
            _initialized = false;
            micStatus = $"Init failed: {e.Message}";
            Debug.LogError($"[Voice] Failed to create DictationRecognizer: {e.Message}");
        }
    }

    void OnDestroy()
    {
        if (_dictation != null)
        {
            try
            {
                if (_dictation.Status == SpeechSystemStatus.Running)
                    _dictation.Stop();
                _dictation.Dispose();
            }
            catch { }
        }
    }

    void Update()
    {
        if (_recording)
        {
            _recordTimer += Time.deltaTime;
            if (_recordTimer >= autoStopSeconds)
                StopRecording();
        }
    }

    /// <summary>
    /// Toggle mic on/off. Safe to call from any button onClick.
    /// </summary>
    public void OnMicToggle()
    {
        if (!_initialized)
        {
            Debug.LogWarning("[Voice] DictationRecognizer not initialized. Check Windows Speech settings.");
            micStatus = "Not initialized";
            return;
        }

        try
        {
            if (_recording)
                StopRecording();
            else
                StartRecording();
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[Voice] OnMicToggle error: {e.Message}");
            micStatus = $"Error: {e.Message}";
            _recording = false;
        }
    }

    void StartRecording()
    {
        if (_dictation == null) return;

        try
        {
            if (_dictation.Status == SpeechSystemStatus.Running)
            {
                Debug.Log("[Voice] Already running.");
                return;
            }

            // PhraseRecognitionSystem must be off for DictationRecognizer to work
            if (PhraseRecognitionSystem.isSupported && PhraseRecognitionSystem.Status == SpeechSystemStatus.Running)
            {
                Debug.Log("[Voice] Shutting down PhraseRecognitionSystem for dictation.");
                PhraseRecognitionSystem.Shutdown();
            }

            _recording = true;
            _recordTimer = 0;
            _dictation.Start();
            micStatus = "Listening...";
            chat?.SetStatus("Listening...");
            Debug.Log("[Voice] Recording started.");
        }
        catch (System.Exception e)
        {
            _recording = false;
            micStatus = $"Start failed: {e.Message}";
            Debug.LogError($"[Voice] Start failed: {e.Message}");
        }
    }

    void StopRecording()
    {
        _recording = false;
        micStatus = "Idle";

        if (_dictation != null && _dictation.Status == SpeechSystemStatus.Running)
        {
            try { _dictation.Stop(); }
            catch { }
        }

        chat?.SetStatus("Idle");
        Debug.Log("[Voice] Recording stopped.");
    }

    void OnDictationResult(string text, ConfidenceLevel confidence)
    {
        Debug.Log($"[Voice] Result ({confidence}): {text}");
        if (inputField != null)
        {
            inputField.text = text;
            chat?.OnSendClicked();
        }
        StopRecording();
    }

    void OnDictationComplete(DictationCompletionCause cause)
    {
        _recording = false;
        micStatus = "Idle";
        if (cause != DictationCompletionCause.Complete)
            Debug.LogWarning($"[Voice] Dictation ended: {cause}");
    }

    void OnDictationError(string error, int hresult)
    {
        Debug.LogError($"[Voice] Error: {error} (0x{hresult:X})");
        micStatus = $"Error: {error}";
        StopRecording();
    }

#else
    void Start()
    {
        micStatus = "Not supported (non-Windows)";
        Debug.LogWarning("[Voice] DictationRecognizer only available on Windows.");
        if (micButton != null)
            micButton.onClick.AddListener(OnMicToggle);
    }

    /// <summary>Safe no-op on non-Windows platforms.</summary>
    public void OnMicToggle()
    {
        Debug.LogWarning("[Voice] Not available on this platform.");
        micStatus = "Not supported";
    }
#endif
}