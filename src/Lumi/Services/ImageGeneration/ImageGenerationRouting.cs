using Lumi.Models;
using Lumi.Services.Byok;

namespace Lumi.Services.ImageGeneration;

/// <summary>How a chat creates images: GitHub Copilot's native tool, the user's own account through Lumi's tool, or not at all.</summary>
public enum ImageGenerationRoute
{
    None,
    Copilot,
    Account,
}

/// <summary>A chat's image route, and the account it uses on <see cref="ImageGenerationRoute.Account"/>.</summary>
public readonly record struct ImageGenerationPlan(ImageGenerationRoute Route, ByokEndpoint? Account = null)
{
    public static ImageGenerationPlan Off => new(ImageGenerationRoute.None);
}

/// <summary>
/// Chooses where a chat's images come from. GitHub Copilot's native <c>image_generation</c> tool runs in any chat
/// while Copilot is signed in, whichever model the chat uses, and is billed to Copilot; with "Use only your own
/// accounts" on, or without Copilot, an account that speaks the OpenAI Images API creates them instead. Either
/// way the model sees one tool named <c>image_generation</c>.
/// </summary>
public static class ImageGenerationRouting
{
    /// <summary>Providers known to serve the OpenAI Images API, in the order "automatic" prefers them.</summary>
    private static readonly string[] KnownImageProviders = ["openai", "foundry", "gemini", "xai", "nanogpt"];

    /// <summary>Providers known not to serve it: their accounts are never offered for images.</summary>
    private static readonly HashSet<string> KnownTextOnlyProviders = new(StringComparer.Ordinal)
    {
        ProviderCatalog.ChatGptId, "openrouter", "poe", "minimax-plan", "mistral", "ollama-cloud", "huggingface",
        "kilo", "zen", "anthropic", "deepseek", "groq", "ollama", "lmstudio",
    };

    /// <param name="copilotAvailable">GitHub Copilot is signed in and its account offers an image model.</param>
    public static ImageGenerationPlan Resolve(UserSettings settings, bool copilotAvailable)
    {
        if (!settings.EnableImageGeneration)
            return ImageGenerationPlan.Off;

        var canUseCopilot = copilotAvailable && !settings.UseBYOKOnly;
        var choice = settings.ImageGenerationProvider;
        if (string.Equals(choice, UserSettings.CopilotImageProvider, StringComparison.Ordinal))
            return canUseCopilot ? new(ImageGenerationRoute.Copilot) : ImageGenerationPlan.Off;

        if (!string.IsNullOrWhiteSpace(choice)
            && ImageAccounts(settings).FirstOrDefault(account => account.Id == choice) is { } chosen)
            return new(ImageGenerationRoute.Account, chosen);

        // Automatic, or an account that has since been removed.
        if (canUseCopilot)
            return new(ImageGenerationRoute.Copilot);
        return PreferredAccount(settings) is { } account
            ? new(ImageGenerationRoute.Account, account)
            : ImageGenerationPlan.Off;
    }

    /// <summary>The user's accounts that can create images, as the provider picker lists them.</summary>
    public static IReadOnlyList<ByokEndpoint> ImageAccounts(UserSettings settings)
        => ByokConfigHelper.GetValidEndpoints(settings).Where(CanCreateImages).ToList();

    /// <summary>The account "automatic" uses without Copilot: one from a provider known to create images.</summary>
    public static ByokEndpoint? PreferredAccount(UserSettings settings)
        => ImageAccounts(settings)
            .Select(account => (Account: account, Rank: Array.IndexOf(KnownImageProviders, ProviderId(account))))
            .Where(static entry => entry.Rank >= 0)
            .OrderBy(static entry => entry.Rank)
            .Select(static entry => entry.Account)
            .FirstOrDefault();

    /// <summary>The image model chosen for an account, or null (its newest) when the saved choice belongs to another account.</summary>
    public static string? ModelFor(UserSettings settings, ByokEndpoint account)
        => settings.ImageGenerationProvider == account.Id && !string.IsNullOrWhiteSpace(settings.ImageGenerationModel)
            ? settings.ImageGenerationModel
            : null;

    /// <summary>Providers whose accounts may create images: those known to, and custom endpoints.</summary>
    public static bool IsImageProvider(string providerId)
        => KnownImageProviders.Contains(providerId, StringComparer.Ordinal) || providerId == "custom";

    /// <summary>An enabled OpenAI-compatible account whose provider isn't known to lack the Images API.</summary>
    internal static bool CanCreateImages(ByokEndpoint endpoint)
        => endpoint.IsEnabled
           && !ByokConfigHelper.IsChatGptEndpoint(endpoint)
           && (string.IsNullOrWhiteSpace(endpoint.ProviderType) || endpoint.ProviderType == "openai")
           && !KnownTextOnlyProviders.Contains(ProviderId(endpoint));

    internal static string ProviderId(ByokEndpoint endpoint)
        => endpoint.ConnectionProviderId
           ?? ProviderCatalog.FromUrl(endpoint.BaseUrl)?.Id
           ?? "custom";
}
