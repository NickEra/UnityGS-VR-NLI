using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// LLMClient v8 — Adapted for SemanticController architecture.
///
/// Changes from v7:
///   - References NLICommandBus instead of SceneGSManager
///   - System prompt action types adapted for per-layer GPU operations
///   - Added show_tf / hide_tf (not just show_only_tf)
/// </summary>
public class LLMClient : MonoBehaviour
{
    public enum LLMProvider { DeepSeek, OpenAI, Seed, Qwen }

    [Header("Provider")]
    public LLMProvider provider = LLMProvider.DeepSeek;

    [Header("DeepSeek Settings")]
    public string deepSeek_ApiKey = "";
    public string deepSeek_Model = "deepseek-chat";
    public string deepSeek_Endpoint = "https://api.deepseek.com/v1/chat/completions";

    [Header("OpenAI Settings")]
    public string openai_ApiKey = "";
    public string openai_Model = "gpt-4o-mini";
    public string openai_Endpoint = "https://api.openai.com/v1/chat/completions";

    [Header("Seed (Doubao) Settings")]
    public string seed_ApiKey = "";
    public string seed_Model = "";
    public string seed_Endpoint = "https://ark.cn-beijing.volces.com/api/v3/chat/completions";

    [Header("Qwen (DashScope) Settings")]
    public string qwen_ApiKey = "";
    public string qwen_Model = "qwen-turbo";
    public string qwen_Endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions";

    [Header("References")]
    public NLICommandBus commandBus;

    [Header("Settings")]
    [Range(0f, 1.5f)] public float temperature = 0.3f;
    [Range(100, 4000)] public int maxTokens = 1024;

    readonly List<ChatMessage> _history = new();
    const int MAX_HISTORY = 20;

    void Start()
    {
        if (commandBus == null) commandBus = FindObjectOfType<NLICommandBus>(true);
    }

    // ═══════════════════ Public API ═══════════════════

    public async Task<NliCommandEnvelope> RequestCommandAsync(string userText)
    {
        string system = BuildSystemPrompt();
        string user = BuildUserMessage(userText);

        _history.Add(new ChatMessage { role = "user", content = user });
        if (_history.Count > MAX_HISTORY) _history.RemoveAt(0);

        string reply = await CallLLMWithHistory(system);

        _history.Add(new ChatMessage { role = "assistant", content = reply });
        if (_history.Count > MAX_HISTORY) _history.RemoveAt(0);

        return ParseReply(reply);
    }

    /// <summary>
    /// Lightweight one-shot LLM call — no history, no command parsing.
    /// Used for utility tasks like name matching.
    /// </summary>
    public async Task<string> RequestRawAsync(string systemPrompt, string userMessage)
    {
        return await CallLLMDirect(systemPrompt, userMessage);
    }
    
    /// <summary>Clear conversation history.</summary>
    public void ClearHistory() => _history.Clear();

    // ═══════════════════ Prompt Building ═══════════════════

    string BuildSystemPrompt()
    {
        var sb = new StringBuilder(2048);
        sb.AppendLine("You are an NLI assistant for a VR volume visualization system.");
        sb.AppendLine("You control a 3D Gaussian Splatting scene with semantic layers.");
        sb.AppendLine("Each layer represents a distinct semantic component that can be shown, hidden, or tinted independently.");
        sb.AppendLine();

        // Inject layer context from SemanticController
        if (commandBus != null)
            sb.Append(commandBus.GetLLMContext());

        sb.AppendLine();

        // ── Response format ──
        sb.AppendLine("Respond ONLY with JSON: {\"assistant_text\":\"...\",\"actions\":[...]}");
        sb.AppendLine("Each action: {\"type\":\"...\", ...}");
        sb.AppendLine();

        // ── Action types ──
        sb.AppendLine("Action types:");
        sb.AppendLine("  show_only_tf: {type,value} — isolate one layer (hide all others)");
        sb.AppendLine("  show_tf: {type,value} — show a layer (make visible)");
        sb.AppendLine("  hide_tf: {type,value} — hide a layer");
        sb.AppendLine("  focus_tf: {type,value} — camera zoom to layer centroid");
        sb.AppendLine("  set_tf_opacity: {type,target,fvalue} — per-layer opacity simulation (0-1)");
        sb.AppendLine("  set_tf_color: {type,target,color,strength} — per-layer tint (#hex, strength 0-1)");
        sb.AppendLine("  set_opacity: {type,fvalue} — global opacity scale (0-5)");
        sb.AppendLine("  set_splat_scale: {type,fvalue} — splat size (0.001-5)");
        sb.AppendLine("  set_sh_order: {type,ivalue} — SH detail level (0-4)");
        sb.AppendLine("  set_sh_only: {type,enable} — SH-only mode");
        sb.AppendLine("  legend_add: {type,label,color} — add legend entry");
        sb.AppendLine("  legend_remove: {type,label}");
        sb.AppendLine("  legend_clear: {type}");
        sb.AppendLine("  show_all: {type} — show all layers, clear tints");
        sb.AppendLine("  reset: {type} — reset everything");
        sb.AppendLine("  guided_tour: {type} — start guided tour of all layers");
        sb.AppendLine("  get_status: {type}");
        sb.AppendLine();

        // ── Flexible interpretation guidance ──
        sb.AppendLine("IMPORTANT RULES:");
        sb.AppendLine("1. Use exact component names (or their aliases) in 'value' or 'target' fields.");
        sb.AppendLine("2. Interpret the user's INTENT flexibly — map natural language to the closest action type.");
        sb.AppendLine("   Examples of flexible mapping:");
        sb.AppendLine("   - 'give me a guided tour' / 'introduce this dataset' / 'walk me through' → guided_tour");
        sb.AppendLine("   - 'show me the skull' / 'isolate skull' / 'only skull' → show_only_tf");
        sb.AppendLine("   - 'make it transparent' / 'see through' → set_opacity with low value");
        sb.AppendLine("   - 'highlight X in red' → set_tf_color + legend_add");
        sb.AppendLine("   - 'start over' / 'clear everything' → reset");
        sb.AppendLine("   - 'what am I looking at?' → get_status");
        sb.AppendLine("3. For ambiguous requests, prefer the most helpful interpretation.");
        sb.AppendLine("4. Always include a brief, friendly assistant_text explaining what you did.");
        sb.AppendLine("5. You can understand commands in both English and Chinese.");

        return sb.ToString();
    }

    string BuildUserMessage(string userText)
    {
        if (commandBus == null) return userText;
        string[] names = commandBus.GetLayerNames();
        if (names.Length == 0) return userText;
        return $"[Available components: {string.Join(", ", names)}]\n{userText}";
    }

    // ═══════════════════ LLM Calls ═══════════════════

    async Task<string> CallLLMWithHistory(string systemPrompt)
    {
        string apiKey, model, endpoint;
        GetProviderConfig(out apiKey, out model, out endpoint);

        var sb = new StringBuilder();
        sb.Append("{");
        sb.Append($"\"model\":\"{model}\",\"temperature\":{temperature},");
        AppendTokenLimit(sb, maxTokens);
        sb.Append("\"messages\":[");
        sb.Append($"{{\"role\":\"system\",\"content\":{JsonEscape(systemPrompt)}}}");
        foreach (var m in _history)
            sb.Append($",{{\"role\":\"{m.role}\",\"content\":{JsonEscape(m.content)}}}");
        sb.Append("]}");

        return await SendRequest(endpoint, apiKey, sb.ToString());
    }

    async Task<string> CallLLMDirect(string systemPrompt, string userMessage)
    {
        string apiKey, model, endpoint;
        GetProviderConfig(out apiKey, out model, out endpoint);

        var sb = new StringBuilder();
        sb.Append("{");
        sb.Append($"\"model\":\"{model}\",\"temperature\":0.0,");
        AppendTokenLimit(sb, 512);
        sb.Append("\"messages\":[");
        sb.Append($"{{\"role\":\"system\",\"content\":{JsonEscape(systemPrompt)}}},");
        sb.Append($"{{\"role\":\"user\",\"content\":{JsonEscape(userMessage)}}}");
        sb.Append("]}");

        return await SendRequest(endpoint, apiKey, sb.ToString());
    }

    // ═══════════════════ Provider Config ═══════════════════

    void GetProviderConfig(out string apiKey, out string model, out string endpoint)
    {
        switch (provider)
        {
            case LLMProvider.DeepSeek: apiKey = deepSeek_ApiKey; model = deepSeek_Model; endpoint = deepSeek_Endpoint; break;
            case LLMProvider.OpenAI:   apiKey = openai_ApiKey;   model = openai_Model;   endpoint = openai_Endpoint;   break;
            case LLMProvider.Seed:     apiKey = seed_ApiKey;     model = seed_Model;     endpoint = seed_Endpoint;     break;
            case LLMProvider.Qwen:     apiKey = qwen_ApiKey;     model = qwen_Model;     endpoint = qwen_Endpoint;     break;
            default: throw new Exception("Unknown provider");
        }
    }

    void AppendTokenLimit(StringBuilder sb, int tokens)
    {
        switch (provider)
        {
            case LLMProvider.DeepSeek:
            case LLMProvider.OpenAI:
                sb.Append($"\"max_completion_tokens\":{tokens},");
                break;
            case LLMProvider.Seed:
            case LLMProvider.Qwen:
            default:
                sb.Append($"\"max_tokens\":{tokens},");
                break;
        }
    }

    // ═══════════════════ HTTP Request ═══════════════════

    async Task<string> SendRequest(string endpoint, string apiKey, string jsonBody)
    {
        var req = new UnityWebRequest(endpoint, "POST");
        byte[] bodyBytes = Encoding.UTF8.GetBytes(jsonBody);
        req.uploadHandler = new UploadHandlerRaw(bodyBytes);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.SetRequestHeader("Authorization", $"Bearer {apiKey}");

        var op = req.SendWebRequest();
        while (!op.isDone) await Task.Yield();

        if (req.result != UnityWebRequest.Result.Success)
        {
            string errorBody = req.downloadHandler?.text ?? "";

            // Auto-retry: if server says use different token param, retry once
            if (errorBody.Contains("max_completion_tokens") && jsonBody.Contains("\"max_tokens\""))
            {
                Debug.LogWarning("[LLMClient] Retrying with max_completion_tokens...");
                jsonBody = jsonBody.Replace("\"max_tokens\"", "\"max_completion_tokens\"");
                return await SendRequest(endpoint, apiKey, jsonBody);
            }
            if (errorBody.Contains("max_tokens") && jsonBody.Contains("\"max_completion_tokens\""))
            {
                Debug.LogWarning("[LLMClient] Retrying with max_tokens...");
                jsonBody = jsonBody.Replace("\"max_completion_tokens\"", "\"max_tokens\"");
                return await SendRequest(endpoint, apiKey, jsonBody);
            }

            throw new Exception($"LLM request failed: {req.error}\n{errorBody}");
        }

        var resp = JsonUtility.FromJson<ChatCompletionResponse>(req.downloadHandler.text);
        if (resp?.choices == null || resp.choices.Length == 0)
            throw new Exception("Empty LLM response");

        return resp.choices[0].message.content;
    }

    // ═══════════════════ Helpers ═══════════════════

    static string JsonEscape(string s)
    {
        if (s == null) return "\"\"";
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "\"";
    }

    NliCommandEnvelope ParseReply(string reply)
    {
        reply = reply.Trim();
        if (reply.StartsWith("```")) reply = reply.Substring(reply.IndexOf('\n') + 1);
        if (reply.EndsWith("```")) reply = reply.Substring(0, reply.LastIndexOf("```"));
        reply = reply.Trim();

        try { return JsonUtility.FromJson<NliCommandEnvelope>(reply); }
        catch { return new NliCommandEnvelope { assistant_text = reply }; }
    }

    // ═══════════════════ JSON Models ═══════════════════

    [Serializable] class ChatMessage { public string role; public string content; }
    [Serializable] class ChatCompletionResponse { public Choice[] choices; }
    [Serializable] class Choice { public ChatMessage message; }
}
