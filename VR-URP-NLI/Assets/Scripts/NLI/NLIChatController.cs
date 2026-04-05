using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// NLIChatController v6 — Adapted for SemanticController architecture.
///
/// Changes from v5:
///   - Routes through NLICommandBus instead of CommandBusHandlers
///   - LLMClient.RequestCommandAsync() no longer needs variant/activePart params
///   - Added show_tf action type
/// </summary>
public class NLIChatController : MonoBehaviour
{
    [Header("UI References")]
    public TMP_InputField inputField;
    public Button sendButton;
    public TMP_Text chatText;
    public ScrollRect scrollView;
    public TMP_Text statusText;

    [Header("Action Log (optional)")]
    [Tooltip("Separate panel for command execution log. Leave empty to skip.")]
    public TMP_Text actionLogText;
    public ScrollRect actionLogScroll;

    [Header("Core References")]
    public LLMClient llm;
    public NLICommandBus bus;

    bool _busy;
    int _scrollFrames;
    int _actionScrollFrames;

    void Start()
    {
        if (sendButton != null) sendButton.onClick.AddListener(OnSendClicked);
        if (llm == null) llm = FindObjectOfType<LLMClient>(true);
        if (bus == null) bus = FindObjectOfType<NLICommandBus>(true);
        if (chatText != null) chatText.text = "";
        if (actionLogText != null) actionLogText.text = "";
    }

    void Update()
    {
        if (_scrollFrames > 0)
        {
            _scrollFrames--;
            if (scrollView != null) scrollView.normalizedPosition = Vector2.zero;
        }
        if (_actionScrollFrames > 0)
        {
            _actionScrollFrames--;
            if (actionLogScroll != null) actionLogScroll.normalizedPosition = Vector2.zero;
        }
    }

    public async void OnSendClicked()
    {
        if (_busy || inputField == null || string.IsNullOrWhiteSpace(inputField.text)) return;
        string userText = inputField.text.Trim();
        inputField.text = "";
        _busy = true;
        SetStatus("Thinking...");

        AppendChat($"<align=right><color=#4A90D9>{userText}</color></align>");

        try
        {
            var env = await llm.RequestCommandAsync(userText);

            if (!string.IsNullOrWhiteSpace(env?.assistant_text))
                AppendChat($"<align=left><color=#27AE60>{env.assistant_text}</color></align>");

            if (env?.actions != null)
            {
                foreach (var a in env.actions)
                {
                    try
                    {
                        ExecuteAction(a);
                        AppendActionLog($"<color=#27AE60>[OK]</color> {FormatAction(a)}");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[NLIChat] Action '{a?.type}' failed: {ex.Message}");
                        AppendChat($"<color=#E74C3C>[Error] {a?.type}: {ex.Message}</color>");
                        AppendActionLog($"<color=#E74C3C>[FAIL] {a?.type}: {ex.Message}</color>");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[NLIChat] LLM error: {ex.Message}");
            AppendChat($"<color=#E74C3C>[Error] {ex.Message}</color>");
            AppendActionLog($"<color=#E74C3C>LLM Error: {ex.Message}</color>");
        }

        _busy = false;
        SetStatus("Ready");
    }

    void ExecuteAction(NliAction a)
    {
        if (a == null || string.IsNullOrWhiteSpace(a.type)) return;
        string v = a.value ?? "";
        string t = a.target ?? "";

        switch (a.type.ToLowerInvariant())
        {
            case "show_only_tf":  bus.Cmd_ShowOnlyLayer(v); break;
            case "show_tf":       bus.Cmd_ShowLayer(v); break;
            case "hide_tf":       bus.Cmd_HideLayer(v); break;
            case "focus_tf":      bus.Cmd_FocusLayer(v); break;
            case "set_tf_opacity": bus.Cmd_SetLayerOpacity(t, a.fvalue); break;
            case "set_tf_color":  bus.Cmd_SetLayerColor(t, a.color, a.strength); break;
            case "set_opacity":   bus.Cmd_SetOpacity(a.fvalue); break;
            case "set_splat_scale": bus.Cmd_SetSplatScale(a.fvalue); break;
            case "set_sh_order":  bus.Cmd_SetSHOrder(a.ivalue); break;
            case "set_sh_only":   bus.Cmd_SetSHOnly(a.enable); break;
            case "legend_add":    bus.Cmd_LegendAdd(a.label, a.color); break;
            case "legend_remove": bus.Cmd_LegendRemove(a.label); break;
            case "legend_clear":  bus.Cmd_LegendClear(); break;
            case "show_all":      bus.Cmd_ShowAll(); break;
            case "reset":         bus.Cmd_Reset(); break;
            case "guided_tour":   bus.Cmd_GuidedTour(); break;
            case "get_status":
                string s = bus.Cmd_GetStatus();
                AppendChat($"<color=#95A5A6>[Status] {s}</color>");
                break;
            default:
                Debug.LogWarning($"[NLIChat] Unknown action: {a.type}");
                break;
        }
        Debug.Log($"[NLIChat] Executed: {a.type} {v}{t}");
    }

    string FormatAction(NliAction a)
    {
        if (a == null) return "null";
        string details = a.type;
        if (!string.IsNullOrEmpty(a.value)) details += $" '{a.value}'";
        if (!string.IsNullOrEmpty(a.target)) details += $" target='{a.target}'";
        if (!string.IsNullOrEmpty(a.color)) details += $" color={a.color}";
        if (a.fvalue != 0) details += $" f={a.fvalue:F2}";
        if (a.ivalue != 0) details += $" i={a.ivalue}";
        return details;
    }

    // ═══════════════════ Chat Panel ═══════════════════

    void AppendChat(string line)
    {
        if (chatText == null) return;
        if (chatText.text.Length > 0) chatText.text += "\n";
        chatText.text += line;
        _scrollFrames = 2;
    }

    public void AppendTourMessage(string msg)
    {
        AppendChat($"<color=#8E44AD>{msg}</color>");
    }

    // ═══════════════════ Action Log Panel ═══════════════════

    void AppendActionLog(string line)
    {
        if (actionLogText == null) return;
        if (actionLogText.text.Length > 0) actionLogText.text += "\n";
        actionLogText.text += line;
        _actionScrollFrames = 2;
    }

    // ═══════════════════ Status ═══════════════════

    public void SetStatus(string text)
    {
        if (statusText != null) statusText.text = text;
    }
}
