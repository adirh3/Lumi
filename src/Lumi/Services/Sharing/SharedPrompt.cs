using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Lumi.Services.Sharing;

[JsonConverter(typeof(JsonStringEnumConverter<PromptInputKind>))]
public enum PromptInputKind
{
    Text,
    File,
    Folder
}

public sealed record PromptInput(string Key, string Label, string Description, PromptInputKind Kind = PromptInputKind.Text);

/// <summary>A reusable task, not the source conversation or the sender's configuration.</summary>
public sealed record SharedPrompt
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Content { get; init; }
    public IReadOnlyList<PromptInput> Inputs { get; init; } = [];
    public IReadOnlyList<string> Requirements { get; init; } = [];
}

public static partial class PromptSharing
{
    public const int MaxContentLength = 60_000;
    public const int MaxInputs = 8;
    public const int MaxRequirements = 12;

    public const string SetupGuidance = """
        Adapt this task to my setup:
        - Check my operating system, available tools, versions and integrations before making changes. Use my own files, folders, accounts and permissions; never assume the sender's machine or configuration is available.
        - Check the prerequisites below. Prefer an equivalent tool I already have when it can preserve the requested result. If the workflow is platform-specific or a required capability is missing, explain the limitation and ask how to proceed instead of pretending it worked.
        - Ask for any missing input or attachment. Do not invent its contents, reuse the sender's approvals, or claim work from the source conversation has already been done here.
        - Keep credentials out of this prompt. Use my signed-in accounts or ask me to configure secrets securely in the appropriate settings.
        - Ask before installing software, enabling integrations, creating recurring jobs, or making destructive or system-level changes. Verify the requested result and report anything you could not complete.
        """;

    [GeneratedRegex(@"\{\{(?<key>[a-z][a-z0-9_]{0,39})\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex InputPattern();

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    public static IReadOnlyList<string> InputKeys(string content)
        => InputPattern().Matches(content).Select(static match => match.Groups["key"].Value)
            .Distinct(StringComparer.Ordinal).ToList();

    public static SharedPrompt Normalize(SharedPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (prompt.Name is null || prompt.Description is null || prompt.Content is null
            || prompt.Inputs is null || prompt.Requirements is null)
            throw new ArgumentException("The prompt is missing a required field.");

        var normalized = prompt with
        {
            Name = PackText.SingleLine(HiddenText.Strip(prompt.Name, out _)),
            Description = PackText.SingleLine(HiddenText.Strip(prompt.Description, out _)),
            Content = PackText.NormalizeNewlines(HiddenText.Strip(prompt.Content, out _)).Trim(),
            Inputs = prompt.Inputs.Select(static input => input is null
                ? throw new ArgumentException("A prompt input is missing.")
                : input with
                {
                    Label = PackText.SingleLine(HiddenText.Strip(input.Label, out _)),
                    Description = PackText.SingleLine(HiddenText.Strip(input.Description, out _))
                }).ToList(),
            Requirements = prompt.Requirements.Select(static requirement =>
                PackText.SingleLine(HiddenText.Strip(requirement, out _))).ToList()
        };

        if (normalized.Name.Length is 0 or > 120 || normalized.Description.Length > 1000
            || normalized.Content.Length is 0 or > MaxContentLength
            || normalized.Inputs.Count > MaxInputs || normalized.Requirements.Count > MaxRequirements)
            throw new ArgumentException("The prompt is empty or exceeds the supported size.");

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in normalized.Inputs)
        {
            if (input.Key is null || !KeyPattern().IsMatch(input.Key) || !keys.Add(input.Key)
                || SecretRedactor.IsSensitiveName(input.Key)
                || input.Label.Length is 0 or > 100 || input.Description.Length > 500
                || !Enum.IsDefined(input.Kind))
                throw new ArgumentException("The prompt contains an invalid, duplicate or secret-bearing input.");
        }

        if (!keys.SetEquals(InputKeys(normalized.Content))
            || InputPattern().Replace(normalized.Content, "").Contains("{{", StringComparison.Ordinal)
            || normalized.Requirements.Any(static requirement => requirement.Length is 0 or > 1000 || InputKeys(requirement).Count > 0))
            throw new ArgumentException("Every prompt placeholder must have exactly one matching input.");

        return normalized;
    }

    public static SharedPrompt Parse(string text)
    {
        var raw = text.Trim();
        if (PackText.TryUnwrapSingleFence(raw, out var inner))
            raw = inner.Trim();
        return Normalize(JsonSerializer.Deserialize(raw, PromptShareJsonContext.Default.SharedPrompt)
            ?? throw new JsonException("The model returned no reusable prompt."));
    }

    public static string Write(SharedPrompt prompt)
        => JsonSerializer.Serialize(Normalize(prompt), PromptShareJsonContext.Default.SharedPrompt);

    public static IReadOnlyList<PromptInput> InputsForEditedContent(string content, IReadOnlyList<PromptInput> original)
        => InputKeys(content).Select(key => original.FirstOrDefault(input => input.Key == key)
            ?? new PromptInput(key, PackText.HumanizeName(key.Replace('_', '-')), "")).ToList();

    /// <summary>Replace placeholders in one pass, so an input value cannot substitute another input.</summary>
    public static string Personalize(SharedPrompt prompt, IReadOnlyDictionary<string, string> values)
    {
        prompt = Normalize(prompt);
        foreach (var input in prompt.Inputs)
        {
            if (!values.TryGetValue(input.Key, out var value) || string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"Provide a value for {input.Label}.");
        }

        var content = InputPattern().Replace(prompt.Content, match => values[match.Groups["key"].Value].Trim());
        return ComposeText(prompt with { Content = content });
    }

    /// <summary>The plain-text hand-off and recipient draft both include the same setup checks.</summary>
    public static string ComposeText(SharedPrompt prompt)
    {
        var builder = new StringBuilder(prompt.Content.Trim());
        builder.Append("\n\n").Append(SetupGuidance);
        if (prompt.Requirements.Count > 0)
        {
            builder.Append("\n\nPrerequisites to check (not included in this share):");
            foreach (var requirement in prompt.Requirements)
                builder.Append("\n- ").Append(requirement);
        }

        return builder.ToString();
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(SharedPrompt))]
internal partial class PromptShareJsonContext : JsonSerializerContext;
