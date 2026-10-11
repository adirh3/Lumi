using Lumi.Localization;

namespace Lumi.Services.Byok;

public enum ProviderConnectionKind { ApiKey, Browser, Local, Copilot }

/// <summary>How the add-account gallery groups a provider.</summary>
public enum ProviderCategory { Recommended, SignIn, Plans, Apis, Local, Custom }

public sealed record ProviderProfile(
    string Id,
    string Name,
    string BaseUrl,
    string ProviderType = "openai",
    string WireApi = "completions",
    ProviderConnectionKind Kind = ProviderConnectionKind.ApiKey,
    string? KeyUrl = null,
    string? UsageUrl = null,
    ProviderOAuthKind? OAuth = null,
    ProviderCategory Category = ProviderCategory.Apis,
    string Monogram = "",
    string? Hue = null)
{
    public string Description => Loc.Get($"EasyConnect_{Id.Replace('-', '_')}_Description");
    public string Billing => Loc.Get($"EasyConnect_{Id.Replace('-', '_')}_Billing");
    public string Notice => Loc.Get($"EasyConnect_{Id.Replace('-', '_')}_Notice");
    public bool IsLocal => Kind == ProviderConnectionKind.Local;
    public bool IsCopilot => Kind == ProviderConnectionKind.Copilot;
    public bool IsCustom => Id == "custom";
    public bool IsFoundry => Id == "foundry";
    public bool HasBrowserSignIn => OAuth is not null;
}

public static class ProviderCatalog
{
    // Public OAuth registration, not a credential. Users authorize their own Poe accounts.
    public const string PoeClientId = "client_86f69a1d29984ed4881f8b30fb243dfb";

    public static IReadOnlyList<ProviderProfile> All { get; } =
    [
        new("copilot", "GitHub Copilot", "", Kind: ProviderConnectionKind.Copilot,
            Category: ProviderCategory.Recommended),
        new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1",
            Kind: ProviderConnectionKind.Browser, KeyUrl: "https://openrouter.ai/settings/keys",
            UsageUrl: "https://openrouter.ai/settings/credits", OAuth: ProviderOAuthKind.OpenRouter,
            Category: ProviderCategory.SignIn, Monogram: "OR", Hue: "#6366F1"),
        new("poe", "Poe", "https://api.poe.com/v1", Kind: ProviderConnectionKind.Browser,
            KeyUrl: "https://poe.com/api/keys", UsageUrl: "https://poe.com/subscription_plans",
            OAuth: ProviderOAuthKind.Poe, Category: ProviderCategory.SignIn, Monogram: "P", Hue: "#A855F7"),
        new("nanogpt", "NanoGPT", "https://api.nano-gpt.com/api/v1", Kind: ProviderConnectionKind.Browser,
            KeyUrl: "https://nano-gpt.com/settings", UsageUrl: "https://nano-gpt.com/settings",
            OAuth: ProviderOAuthKind.NanoGpt, Category: ProviderCategory.SignIn, Monogram: "N", Hue: "#14B8A6"),
        new("minimax-plan", "MiniMax M Plan", "https://api.minimax.io/anthropic", "anthropic",
            KeyUrl: "https://platform.minimax.io",
            UsageUrl: "https://platform.minimax.io",
            Category: ProviderCategory.Plans, Monogram: "MM", Hue: "#EC4899"),
        new("mistral", "Mistral", "https://api.mistral.ai/v1",
            KeyUrl: "https://console.mistral.ai/api-keys", UsageUrl: "https://console.mistral.ai",
            Category: ProviderCategory.Plans, Monogram: "M", Hue: "#F97316"),
        new("ollama-cloud", "Ollama Cloud", "https://ollama.com/v1",
            KeyUrl: "https://ollama.com/settings/keys", UsageUrl: "https://ollama.com/settings/usage",
            Category: ProviderCategory.Plans, Monogram: "O"),
        new("huggingface", "Hugging Face", "https://router.huggingface.co/v1",
            KeyUrl: "https://huggingface.co/settings/tokens", UsageUrl: "https://huggingface.co/settings/billing",
            Category: ProviderCategory.Plans, Monogram: "HF", Hue: "#F59E0B"),
        new("kilo", "Kilo Gateway", "https://api.kilo.ai/api/gateway",
            KeyUrl: "https://app.kilo.ai", UsageUrl: "https://app.kilo.ai",
            Category: ProviderCategory.Plans, Monogram: "K", Hue: "#65A30D"),
        new("zen", "OpenCode Zen", "https://opencode.ai/zen/v1",
            KeyUrl: "https://opencode.ai/auth", UsageUrl: "https://opencode.ai",
            Category: ProviderCategory.Plans, Monogram: "Z"),
        new("openai", "OpenAI API", "https://api.openai.com/v1", WireApi: "responses",
            KeyUrl: "https://platform.openai.com/api-keys", UsageUrl: "https://platform.openai.com/usage",
            Monogram: "OA", Hue: "#10B981"),
        new("foundry", "Microsoft Foundry", "", WireApi: "responses",
            KeyUrl: "https://ai.azure.com", UsageUrl: "https://portal.azure.com",
            Monogram: "MF", Hue: "#0078D4"),
        new("anthropic", "Claude API", "https://api.anthropic.com", "anthropic",
            KeyUrl: "https://platform.claude.com", UsageUrl: "https://platform.claude.com",
            Monogram: "C", Hue: "#D97757"),
        new("gemini", "Gemini API", "https://generativelanguage.googleapis.com/v1beta/openai",
            KeyUrl: "https://aistudio.google.com/apikey", UsageUrl: "https://aistudio.google.com/usage",
            Monogram: "G", Hue: "#0EA5E9"),
        new("deepseek", "DeepSeek", "https://api.deepseek.com",
            KeyUrl: "https://platform.deepseek.com/api_keys", UsageUrl: "https://platform.deepseek.com",
            Monogram: "DS", Hue: "#4D6BFE"),
        new("groq", "Groq", "https://api.groq.com/openai/v1",
            KeyUrl: "https://console.groq.com/keys", UsageUrl: "https://console.groq.com",
            Monogram: "Gq", Hue: "#EF4444"),
        new("xai", "Grok API", "https://api.x.ai/v1", WireApi: "responses",
            KeyUrl: "https://console.x.ai", UsageUrl: "https://console.x.ai", Monogram: "X"),
        new("ollama", "Ollama", "http://localhost:11434/v1", Kind: ProviderConnectionKind.Local,
            KeyUrl: "https://ollama.com/download", Category: ProviderCategory.Local, Monogram: "O"),
        new("lmstudio", "LM Studio", "http://localhost:1234/v1", Kind: ProviderConnectionKind.Local,
            KeyUrl: "https://lmstudio.ai/download", Category: ProviderCategory.Local, Monogram: "LM", Hue: "#7C3AED"),
        new("custom", Loc.Get("EasyConnect_CustomName"), "",
            KeyUrl: null, Category: ProviderCategory.Custom),
    ];

    public static ProviderProfile? Find(string? id)
        => All.FirstOrDefault(profile => string.Equals(profile.Id, id, StringComparison.Ordinal));

    // Foundry's v1 catalog omits effort levels. Only documented model identities are recognized:
    // https://learn.microsoft.com/azure/foundry/openai/how-to/reasoning
    internal static IReadOnlyList<string>? GetFoundryReasoningEfforts(string modelId, string wireApi)
        => modelId switch
        {
            "gpt-6-astra" or "gpt-6-astra-2026-09-03"
                or "gpt-6-sol" or "gpt-6-sol-2026-09-22"
                or "gpt-6-luna" or "gpt-6-luna-2026-09-22"
                or "gpt-6.1-sol" or "gpt-6.1-sol-2026-09-29"
                or "gpt-5.6-sol" or "gpt-5.6-sol-2026-06-25"
                or "gpt-5.6-terra" or "gpt-5.6-terra-2026-06-25"
                or "gpt-5.6-luna" or "gpt-5.6-luna-2026-06-25" or "gpt-5.6-luna-2026-07-09"
                => wireApi == "responses" ? ["low", "medium", "high", "xhigh", "max"] : [],
            "gpt-5.5" or "gpt-5.5-2026-04-24"
                or "gpt-5.4" or "gpt-5.4-2026-03-05"
                or "gpt-5.4-mini" or "gpt-5.4-mini-2026-03-17"
                or "gpt-5.4-nano" or "gpt-5.4-nano-2026-03-17"
                or "gpt-5.1-codex-max" or "gpt-5.1-codex-max-2025-12-04"
                => ["low", "medium", "high", "xhigh"],
            "gpt-5.1" or "gpt-5.1-2025-11-13"
                or "gpt-5.1-codex" or "gpt-5.1-codex-2025-11-13"
                or "gpt-5.1-codex-mini" or "gpt-5.1-codex-mini-2025-11-13"
                or "gpt-5-codex" or "gpt-5-codex-2025-09-01"
                or "o1" or "o1-2024-12-17"
                or "o3" or "o3-2025-04-16"
                or "o3-mini" or "o3-mini-2025-01-31"
                or "o4-mini" or "o4-mini-2025-04-16"
                => ["low", "medium", "high"],
            "gpt-5" or "gpt-5-2025-08-07"
                or "gpt-5-mini" or "gpt-5-mini-2025-08-07"
                or "gpt-5-nano" or "gpt-5-nano-2025-08-07"
                => ["minimal", "low", "medium", "high"],
            "gpt-5-pro" or "gpt-5-pro-2025-10-06" => ["high"],
            _ => null
        };

    public static ProviderProfile? FromUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        return All.FirstOrDefault(profile =>
            Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var candidate)
            && uri.Scheme == candidate.Scheme
            && uri.Authority.Equals(candidate.Authority, StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.TrimEnd('/').Equals(candidate.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal))
            ?? (uri.Scheme == Uri.UriSchemeHttps
                && (uri.IdnHost.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase)
                    || uri.IdnHost.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase))
                && uri.AbsolutePath.TrimEnd('/') == "/openai/v1" ? Find("foundry") : null);
    }
}
