using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using GitHub.Copilot;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.ImageGeneration;
using Lumi.Services.Permissions;
using Lumi.Services.Remote;
using Lumi.Services.Storage;
using Lumi.ViewModels;
using Microsoft.Extensions.AI;
using Xunit;
using ChatMessage = Lumi.Models.ChatMessage;

namespace Lumi.Tests;

public sealed class ImageGenerationTests : IDisposable
{
    private static readonly string NativeResult =
        "Image generated successfully.\nimage_id: sha256:34a39a\nsaved_to: " + Path.Combine(Path.GetTempPath(), "session", "images", "image-55435fa9.png");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lumi-image-tests-" + Guid.NewGuid().ToString("N"));

    public ImageGenerationTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    // ── Routing: one image_generation either way ──

    [Fact]
    public void Routing_UsesCopilotWhileItCanRun_WhicheverModelTheChatUses()
    {
        var settings = new UserSettings();
        settings.ByokEndpoints.Add(OpenAiAccount());

        Assert.Equal(ImageGenerationRoute.Copilot, ImageGenerationRouting.Resolve(settings, copilotAvailable: true).Route);
    }

    [Fact]
    public void Routing_UsesAnImageAccount_WithOwnAccountsOnlyOrWithoutCopilot()
    {
        var settings = new UserSettings();
        var account = OpenAiAccount();
        settings.ByokEndpoints.Add(account);

        var withoutCopilot = ImageGenerationRouting.Resolve(settings, copilotAvailable: false);
        settings.UseBYOKOnly = true;
        var ownAccountsOnly = ImageGenerationRouting.Resolve(settings, copilotAvailable: true);

        Assert.Equal(new ImageGenerationPlan(ImageGenerationRoute.Account, account), withoutCopilot);
        Assert.Equal(new ImageGenerationPlan(ImageGenerationRoute.Account, account), ownAccountsOnly);
    }

    [Fact]
    public void Routing_IsOff_WhenTurnedOffOrNothingCanCreateImages()
    {
        var settings = new UserSettings();
        Assert.Equal(ImageGenerationRoute.None, ImageGenerationRouting.Resolve(settings, copilotAvailable: false).Route);

        settings.EnableImageGeneration = false;
        Assert.Equal(ImageGenerationRoute.None, ImageGenerationRouting.Resolve(settings, copilotAvailable: true).Route);
    }

    [Fact]
    public void Routing_HonorsAChosenProvider_AndFallsBackWhenItsAccountIsGone()
    {
        var settings = new UserSettings();
        var account = OpenAiAccount();
        settings.ByokEndpoints.Add(account);

        settings.ImageGenerationProvider = account.Id;
        Assert.Equal(ImageGenerationRoute.Account, ImageGenerationRouting.Resolve(settings, copilotAvailable: true).Route);

        settings.ImageGenerationProvider = UserSettings.CopilotImageProvider;
        Assert.Equal(ImageGenerationRoute.None, ImageGenerationRouting.Resolve(settings, copilotAvailable: false).Route);

        settings.ImageGenerationProvider = "removed-account";
        Assert.Equal(ImageGenerationRoute.Copilot, ImageGenerationRouting.Resolve(settings, copilotAvailable: true).Route);
    }

    [Fact]
    public void Routing_NeverOffersAccountsWhoseProvidersLackTheImagesApi()
    {
        var settings = new UserSettings();
        settings.ByokEndpoints.Add(Account("OpenRouter", "https://openrouter.ai/api/v1", "openrouter"));
        settings.ByokEndpoints.Add(Account("Claude", "https://api.anthropic.com", "anthropic", providerType: "anthropic"));
        var custom = Account("Studio", "https://images.example.com/v1", null);
        settings.ByokEndpoints.Add(custom);

        Assert.Equal([custom], ImageGenerationRouting.ImageAccounts(settings));
        // A custom endpoint may create images when chosen, but "automatic" only picks known image providers.
        Assert.Equal(ImageGenerationRoute.None, ImageGenerationRouting.Resolve(settings, copilotAvailable: false).Route);
    }

    [Fact]
    public void NativeSwitch_IsAlwaysExplicit_SoAResumedChatOnAnAccountTurnsCopilotsToolOff()
    {
#pragma warning disable GHCP001
        var settings = new UserSettings { ImageGenerationWatermark = true };
        var copilot = ChatViewModel.BuildImageGenerationConfig(new(ImageGenerationRoute.Copilot), settings);
        var account = ChatViewModel.BuildImageGenerationConfig(new(ImageGenerationRoute.Account, OpenAiAccount()), settings);
        var off = ChatViewModel.BuildImageGenerationConfig(ImageGenerationPlan.Off, settings);

        Assert.True(copilot.Enabled);
        Assert.True(copilot.Watermark);
        Assert.False(account.Enabled);
        Assert.False(off.Enabled);
#pragma warning restore GHCP001
    }

    // ── Results and guidance ──

    [Fact]
    public void SavedPaths_AreReadFromTheResultsSavedToLines()
    {
        var paths = GeneratedImageResults.ParseSavedPaths(NativeResult + "\nsaved_to: relative.png\nsaved_to: " + NativeResult.Split("saved_to: ")[1]);

        Assert.Equal([NativeResult.Split("saved_to: ")[1]], paths);
        Assert.Empty(GeneratedImageResults.ParseSavedPaths("Image generation failed."));
    }

    [Fact]
    public void Guidance_TellsTheModelToShowTheImageInItsReply_WithItsOwnPath()
    {
        var spaced = Path.Combine(Path.GetTempPath(), "My Pictures", "cat.png");

        var guidance = GeneratedImageResults.BuildPresentationGuidance([spaced]);

        Assert.Contains($"![short description](<{spaced}>)", guidance);
        Assert.Contains("only material for other work", guidance);
        Assert.Contains("announce_file", guidance);
        Assert.Null(GeneratedImageResults.BuildPresentationGuidance([]));
    }

    [Fact]
    public async Task PostToolHook_AddsGuidanceOnlyAfterAnImageIsCreated()
    {
        var hooks = ChatViewModel.BuildSessionHooks();
        using var result = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["textResultForLlm"] = NativeResult,
            ["resultType"] = "success",
        }));

        var image = await hooks.OnPostToolUse!(new PostToolUseHookInput { ToolName = "image_generation", ToolResult = result.RootElement.Clone() }, null!);
        var other = await hooks.OnPostToolUse!(new PostToolUseHookInput { ToolName = "view", ToolResult = result.RootElement.Clone() }, null!);

        Assert.Contains("image-55435fa9.png", image?.AdditionalContext);
        Assert.Null(other);
    }

    [Fact]
    public void ToolCompletion_RecordsTheImagesOnItsMessage()
    {
        var message = new ChatMessage { Role = "tool", ToolName = "image_generation" };
        var view = new ChatMessage { Role = "tool", ToolName = "view" };

        Assert.True(ChatViewModel.RecordGeneratedImages(message, NativeResult));
        Assert.False(ChatViewModel.RecordGeneratedImages(view, NativeResult));
        Assert.Single(message.GeneratedImages!);
        Assert.Null(view.GeneratedImages);
    }

    // ── Lumi's tool: Copilot's shape, the user's account ──

    [Fact]
    public void AccountTool_OffersTheModelExactlyWhatCopilotsNativeToolDoes()
    {
        var tool = new AccountImageGenerationTool(OpenAiAccount(), null, _folder);

        Assert.Equal("image_generation", tool.Name);
        Assert.StartsWith("Generates or edits one PNG. Only invoke this tool when the user explicitly asks", tool.Description);
        Assert.Equal(true, tool.AdditionalProperties["is_override"]);
        var schema = tool.JsonSchema;
        Assert.Equal("prompt", schema.GetProperty("required")[0].GetString());
        Assert.Equal(["prompt", "input_images", "size", "quality"],
            schema.GetProperty("properties").EnumerateObject().Select(property => property.Name));
        Assert.True(ApprovalPolicy.SkipsApproval(tool.Name));
    }

    [Fact]
    public async Task AccountTool_CreatesWithTheNewestImageModel_SavesTheImage_AndReportsItLikeCopilot()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var handler = new FakeImagesApi(png);
        var reported = new List<(string CallId, string Path)>();
        var tool = new AccountImageGenerationTool(OpenAiAccount(), null, _folder, new OpenAiImageClient(new HttpClient(handler)),
            onSaved: (callId, path) => reported.Add((callId, path)));

        var result = await tool.InvokeAsync(Arguments("""{"prompt":"A lighthouse at dusk","size":"1536x1024","quality":"low"}""", "call-7"));

        var content = Assert.IsType<ToolResultAIContent>(result);
        Assert.Equal("success", content.Result.ResultType);
        var saved = Assert.Single(GeneratedImageResults.ParseSavedPaths(content.Result.TextResultForLlm));
        Assert.Equal(png, await File.ReadAllBytesAsync(saved));
        Assert.Equal(("call-7", saved), Assert.Single(reported));
        Assert.Equal(ToolBinaryResultType.Image, Assert.Single(content.Result.BinaryResultsForLlm!).Type);

        using var request = JsonDocument.Parse(handler.GenerationBody!);
        Assert.Equal("gpt-image-1.5", request.RootElement.GetProperty("model").GetString());
        Assert.Equal("1536x1024", request.RootElement.GetProperty("size").GetString());
        Assert.Equal("low", request.RootElement.GetProperty("quality").GetString());
        Assert.False(request.RootElement.TryGetProperty("response_format", out _));
        Assert.Equal("Bearer sk-test", handler.Authorization);
    }

    [Fact]
    public async Task AccountTool_ReportsTheAccountsOwnError_AsAFailureTheModelWontRetry()
    {
        var handler = new FakeImagesApi([], HttpStatusCode.BadRequest, """{"error":{"message":"Your request was rejected by the safety system."}}""");
        var tool = new AccountImageGenerationTool(OpenAiAccount(), "gpt-image-1", _folder, new OpenAiImageClient(new HttpClient(handler)));

        var content = Assert.IsType<ToolResultAIContent>(await tool.InvokeAsync(Arguments("""{"prompt":"x"}""", "call-1")));

        Assert.Equal("failure", content.Result.ResultType);
        Assert.Contains("safety system", content.Result.Error);
        Assert.Empty(Directory.EnumerateFiles(_folder));
    }

    [Fact]
    public async Task Foundry_OffersItsImageDeployments_NotItsWholeCatalog()
    {
        var handler = new FakeImagesApi([]);
        var client = new OpenAiImageClient(new HttpClient(handler));
        var foundry = new ImageAccountConnection("https://res.services.ai.azure.com/openai/v1", "key", UsesApiKeyHeader: true,
            new Dictionary<string, string>());

        var deployments = await client.ListImageModelsAsync(foundry, CancellationToken.None);

        Assert.Equal(["my-images-2", "older-images"], deployments);
        Assert.Equal("https://res.services.ai.azure.com/openai/deployments", handler.LastPath);
        Assert.Equal("https://res.services.ai.azure.com", OpenAiImageClient.FoundryResourceUrl("https://res.services.ai.azure.com/openai/v1/"));
    }

    [Fact]
    public async Task ImageSetup_ListsImageProviders_AndConnectsAnAccountForImagesAlone()
    {
        var data = new AppData();
        data.Settings.PreferredModel = "gpt-6-luna";
        var handler = new FakeImagesApi([]);
        using var connect = new EasyConnectViewModel(new DataStore(data), TestCopilot.Shared, new FakeSecureKeyStore(), () => { },
            new Lumi.Services.Byok.ProviderConnectionService(new HttpClient(handler)));

        connect.OpenImageSetupCommand.Execute(null);

        Assert.True(connect.IsDialogOpen);
        Assert.All(connect.Providers, card => Assert.True(ImageGenerationRouting.IsImageProvider(card.Profile.Id)));
        Assert.Contains(connect.Providers, card => card.Profile.Id == "foundry");
        Assert.DoesNotContain(connect.Providers, card => card.Profile.Id is "anthropic" or "openrouter");

        connect.Providers.Single(card => card.Profile.Id == "foundry").OpenCommand.Execute(null);
        connect.BaseUrl = "https://res.services.ai.azure.com";
        connect.ApiKey = "test-key";
        await connect.DiscoverCommand.ExecuteAsync(null);
        for (var i = 0; i < 100 && connect.IsFindingImageModels; i++)
            await Task.Delay(20);

        Assert.False(connect.ShowChatModels);
        Assert.True(connect.UseForImages);
        Assert.Equal("my-images-2", connect.SelectedImageModelId);
        Assert.True(connect.CanSave);

        await connect.SaveConnectionCommand.ExecuteAsync(null);

        var account = Assert.Single(data.Settings.ByokEndpoints);
        Assert.Equal(account.Id, data.Settings.ImageGenerationProvider);
        Assert.Equal("my-images-2", data.Settings.ImageGenerationModel);
        Assert.Empty(data.Settings.ByokModels);
        Assert.Equal("gpt-6-luna", data.Settings.PreferredModel);
        Assert.Equal(new ImageGenerationPlan(ImageGenerationRoute.Account, account),
            ImageGenerationRouting.Resolve(data.Settings, copilotAvailable: true));
    }

    [Fact]
    public void SavedModel_BelongsToItsAccount_NotToTheOneChatsFallBackTo()
    {
        var settings = new UserSettings { UseBYOKOnly = true, ImageGenerationProvider = "removed-xai", ImageGenerationModel = "grok-imagine-image" };
        var openAi = OpenAiAccount();
        settings.ByokEndpoints.Add(openAi);

        Assert.Equal(new ImageGenerationPlan(ImageGenerationRoute.Account, openAi), ImageGenerationRouting.Resolve(settings, copilotAvailable: false));
        Assert.Null(ImageGenerationRouting.ModelFor(settings, openAi));

        settings.ImageGenerationProvider = openAi.Id;
        Assert.Equal("grok-imagine-image", ImageGenerationRouting.ModelFor(settings, openAi));
    }

    [Fact]
    public void ManagingAnImageAccount_KeepsItsModelChosenBeforeDiscoveryAnswers()
    {
        var data = new AppData();
        var foundry = Account("Microsoft Foundry", "https://res.services.ai.azure.com/openai/v1", "foundry");
        data.Settings.ByokEndpoints.Add(foundry);
        data.Settings.ImageGenerationProvider = foundry.Id;
        data.Settings.ImageGenerationModel = "my-images-2";
        using var connect = new EasyConnectViewModel(new DataStore(data), TestCopilot.Shared, new FakeSecureKeyStore(), () => { },
            new Lumi.Services.Byok.ProviderConnectionService(new HttpClient(new FakeImagesApi([]))));

        connect.Connections.Single().ManageCommand.Execute(null);

        Assert.True(connect.UseForImages);
        Assert.Equal("my-images-2", connect.SelectedImageModelId);
        Assert.True(connect.ImagesReady);
    }

    [Fact]
    public void NewestModel_PrefersTheStrongestFamilyAndItsLatestVersion()
    {
        Assert.Equal("gpt-image-2.5-flare", OpenAiImageClient.OrderNewestFirst(
            ["dall-e-3", "gpt-image-1", "gpt-image-2", "gpt-image-2.5-flare", "gpt-image-1-mini", "imagen-4.0-generate-001", "gpt-4o"]).First());
        Assert.Equal("imagen-4.0-generate-001", OpenAiImageClient.OrderNewestFirst(["imagen-3.0-generate-002", "imagen-4.0-generate-001"]).First());
        Assert.Empty(OpenAiImageClient.OrderNewestFirst(["gpt-4o", "text-embedding-3-small"]));
    }

    [Fact]
    public void FoundryDeploymentAlias_GetsGptImageOptions()
    {
        using var request = JsonDocument.Parse(OpenAiImageClient.BuildGenerationBody("my-images-2", "a fox", "1024x1024", "low"));

        Assert.Equal("low", request.RootElement.GetProperty("quality").GetString());
        Assert.False(request.RootElement.TryGetProperty("response_format", out _));
    }

    [Fact]
    public async Task FoundryAccountOnNewestAvailable_UsesItsImageDeployment()
    {
        var handler = new FakeImagesApi([1, 2, 3]);
        var foundry = Account("Microsoft Foundry", "https://res.services.ai.azure.com/openai/v1", "foundry");
        var tool = new AccountImageGenerationTool(foundry, null, _folder, new OpenAiImageClient(new HttpClient(handler)));

        var content = Assert.IsType<ToolResultAIContent>(await tool.InvokeAsync(Arguments("""{"prompt":"x"}""", "call-1")));

        Assert.Equal("success", content.Result.ResultType);
        using var request = JsonDocument.Parse(handler.GenerationBody!);
        Assert.Equal("my-images-2", request.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void GenerationRequest_AsksOtherModelsForBase64()
    {
        using var request = JsonDocument.Parse(OpenAiImageClient.BuildGenerationBody("imagen-4.0-generate-001", "a fox", "auto", "high"));

        Assert.Equal("b64_json", request.RootElement.GetProperty("response_format").GetString());
        Assert.False(request.RootElement.TryGetProperty("size", out _));
        Assert.False(request.RootElement.TryGetProperty("quality", out _));
    }

    // ── Where images show ──

    [Fact]
    public void Transcript_NamesTheStepByWhatItDoes()
    {
        var (create, prompt) = ToolDisplayHelper.GetFriendlyToolDisplay("image_generation", null, """{"prompt":"A red bicycle\nleaning on a wall"}""");
        var (edit, _) = ToolDisplayHelper.GetFriendlyToolDisplay("image_generation", null, """{"prompt":"Make it blue","input_images":[{"path":"C:\\bike.png"}]}""");

        Assert.Equal(Lumi.Localization.Loc.Tool_CreatingImage, create);
        Assert.Equal("A red bicycle", prompt);
        Assert.Equal(Lumi.Localization.Loc.Tool_EditingImage, edit);
        Assert.Equal(TranscriptIcons.Image, ToolDisplayHelper.GetToolIconKey("image_generation"));
        Assert.Contains("bike.png", ToolDisplayHelper.FormatToolArgsFriendly("image_generation", """{"prompt":"Make it blue","input_images":[{"path":"C:\\bike.png"}]}"""));
    }

    [Fact]
    public void CreatedImages_ReachTheLibraryAndThePhone()
    {
        var image = Path.Combine(_folder, "image-1.png");
        var message = new ChatMessage { Role = "tool", ToolName = "image_generation", GeneratedImages = [image] };
        var chat = new Chat { Id = Guid.NewGuid(), Title = "Posters" };

        Assert.Equal(ChatMessageKinds.Image, ChatMessageKinds.Classify(message));
        var reference = Assert.Single(LibraryService.ExtractReferences(chat, [message]));
        Assert.Equal(LibraryArtifactOrigin.Created, reference.Origin);
        Assert.Equal(LibraryArtifactKind.Image, reference.Kind);
        Assert.Contains(Path.GetFullPath(image), RemoteMarkdownImageFiles.BuildAuthorizedPaths([message]));
    }

    private static ByokEndpoint OpenAiAccount() => Account("OpenAI API", "https://api.openai.com/v1", "openai");

    private static ByokEndpoint Account(string name, string url, string? provider, string providerType = "openai")
        => new()
        {
            Name = name,
            BaseUrl = url,
            ProviderType = providerType,
            ConnectionProviderId = provider,
            ApiKeyMode = ByokApiKeyMode.Stored,
            ApiKey = "sk-test",
        };

    private static AIFunctionArguments Arguments(string json, string toolCallId)
    {
        using var document = JsonDocument.Parse(json);
        var arguments = new AIFunctionArguments(document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value.Clone()))
        {
            Context = new Dictionary<object, object?>
            {
                [typeof(ToolInvocation)] = new ToolInvocation { ToolCallId = toolCallId, ToolName = "image_generation", SessionId = "session" },
            },
        };
        return arguments;
    }

    private sealed class FakeImagesApi(byte[] image, HttpStatusCode status = HttpStatusCode.OK, string? error = null) : HttpMessageHandler
    {
        public string? GenerationBody { get; private set; }
        public string? Authorization { get; private set; }
        public string? LastPath { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            LastPath = request.RequestUri!.GetLeftPart(UriPartial.Path);
            if (request.RequestUri.AbsolutePath.EndsWith("/openai/deployments", StringComparison.Ordinal))
                return Json("""{"data":[{"id":"chat","model":"gpt-6-luna"},{"id":"older-images","model":"gpt-image-1"},{"id":"my-images-2","model":"gpt-image-2"}]}""");
            if (request.RequestUri.AbsolutePath.EndsWith("/models", StringComparison.Ordinal))
                return Json("""{"data":[{"id":"gpt-4o"},{"id":"dall-e-3"},{"id":"gpt-image-1"},{"id":"gpt-image-1.5"},{"id":"gpt-image-1-mini"}]}""");

            GenerationBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return status == HttpStatusCode.OK
                ? Json($$"""{"data":[{"b64_json":"{{Convert.ToBase64String(image)}}"}]}""")
                : new HttpResponseMessage(status) { Content = new StringContent(error ?? "", Encoding.UTF8, "application/json") };
        }

        private static HttpResponseMessage Json(string body)
            => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
