using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Views;
using Lumi.Remote.Protocol;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Mobile.Tests;

/// <summary>
/// The phone's model sheet measured against the desktop's picker: the same sections and order, the
/// same facts on each row, pins shared with the PC, and effort and context kept within reach.
/// </summary>
[Collection("Headless mobile UI")]
public sealed class MobileModelPickerTests
{
    /// <summary>A PC's picker as the desktop projects it: pinned rows first, in the order they were pinned.</summary>
    private static RemoteSettings Catalog(params string[] pinned)
    {
        RemoteModelOption Row(string model, string name, string? cost, string? context,
            string provider = "GitHub Copilot", string? account = null, string? status = null) =>
            new()
            {
                Model = model, DisplayName = name, ProviderName = provider, Account = account,
                Cost = cost, ContextSize = context, Status = status, IsPinned = pinned.Contains(model)
            };

        RemoteModelOption[] rows =
        [
            Row("auto", "Auto", "Included", null),
            Row("claude-sonnet-5", "Claude Sonnet 5", "1×", "1M"),
            Row("gpt-6.1-sol", "GPT-6.1 Sol", "1×", "400K"),
            Row("claude-opus-5.5", "Claude Opus 5.5", "3×", "200K", status: "Needs enabling"),
            Row("byok:or-sol", "Sol", null, "128K", provider: "OpenRouter", account: "OpenRouter")
        ];
        return new RemoteSettings
        {
            PreferredModel = "gpt-6.1-sol",
            AvailableModels = [.. rows.Select(row => row.Model)],
            ModelDisplayNames = [.. rows.Select(row => $"{row.Model}={row.DisplayName}")],
            ModelReasoningEfforts = ["gpt-6.1-sol=low,medium,high"],
            ModelContextWindowTiers = ["gpt-6.1-sol=Default,Long"],
            ModelOptions =
            [
                .. pinned.Select(model => rows.Single(row => row.Model == model)),
                .. rows.Where(row => !pinned.Contains(row.Model))
            ]
        };
    }

    private static MobileChatViewModel OpenSheet(PinSink sink, RemoteSettings catalog)
    {
        var chat = new MobileChatViewModel(sink);
        chat.ApplyCatalogs(catalog);
        chat.Reset(Guid.Empty, "New chat");
        chat.OpenModelSheetCommand.Execute(null);
        return chat;
    }

    private static string[] Section(MobileChatViewModel chat, string label) =>
        [.. chat.ModelGroups.Single(group => group.Label == label).Options.Select(option => option.Name)];

    private static ModelPickerOption Row(MobileChatViewModel chat, string model) =>
        chat.ModelOptions.Single(option => option.Name == model);

    [Fact]
    public void FusionLeadsTheCopilotSectionAfterPinnedFavorites()
    {
        var catalog = Catalog("claude-opus-5.5", "byok:or-sol");
        var fusion = catalog.ModelOptions.Single(row => row.Model == "auto");
        fusion.Model = "auto:balance";
        fusion.DisplayName = "Copilot Fusion";
        fusion.SelectionAliases = ["auto", "auto:balance", "auto:hydrafusion", "auto:hydrafusion-max"];
        catalog.AvailableModels.InsertRange(1, fusion.SelectionAliases.Skip(1));
        var chat = OpenSheet(new PinSink(), catalog);

        Assert.Equal(["PINNED", "GITHUB COPILOT"], chat.ModelGroups.Select(group => group.Label));
        Assert.Equal(["claude-opus-5.5", "byok:or-sol"], Section(chat, "PINNED"));
        Assert.Equal(["auto:balance", "gpt-6.1-sol", "claude-sonnet-5"], Section(chat, "GITHUB COPILOT"));
        Assert.Equal("Copilot Fusion", Row(chat, "auto:balance").Label);
        Assert.Single(chat.ModelOptions, option => option.SelectionAliases.Count > 0);
    }

    [Fact]
    public void TheSheetListsModelsAsTheDesktopPickerDoes()
    {
        var chat = OpenSheet(new PinSink(), Catalog("claude-opus-5.5", "byok:or-sol"));

        // Pinned first in the order they were pinned; then each account, newest versions first. An
        // account whose only model is pinned has no section of its own.
        Assert.Equal(["PINNED", "GITHUB COPILOT"], chat.ModelGroups.Select(group => group.Label));
        Assert.Equal(["claude-opus-5.5", "byok:or-sol"], Section(chat, "PINNED"));
        Assert.Equal(["gpt-6.1-sol", "claude-sonnet-5", "auto"], Section(chat, "GITHUB COPILOT"));
        Assert.Equal(chat.ModelGroups.SelectMany(group => group.Options), chat.ModelOptions);

        var opus = Row(chat, "claude-opus-5.5");
        Assert.Equal(("Claude Opus 5.5", "3×", "200K", "Needs enabling"), (opus.Label, opus.Cost, opus.ContextSize, opus.Status));
        Assert.True(opus.IsPinned);
        Assert.Equal("Unpin Claude Opus 5.5", opus.PinActionName);
        // A pinned account model names its account, which no section heading names any more.
        Assert.Equal("  OpenRouter", Row(chat, "byok:or-sol").PinnedAccount);
        Assert.Null(opus.PinnedAccount);
        // A screen reader hears the account too, so models of the same name on two accounts read apart.
        Assert.Equal("Sol, OpenRouter", Row(chat, "byok:or-sol").AccessibleName);
        Assert.Equal("Unpin Sol, OpenRouter", Row(chat, "byok:or-sol").PinActionName);
        Assert.Equal("Claude Opus 5.5", opus.AccessibleName);

        var sol = Row(chat, "gpt-6.1-sol");
        Assert.True(sol.IsSelected);
        Assert.False(sol.IsPinned);
        Assert.Equal("Pin GPT-6.1 Sol", sol.PinActionName);
        Assert.Single(chat.ModelOptions, option => option.IsSelected);
        Assert.All(chat.ModelOptions, option => Assert.True(option.CanPin));
        Assert.True(chat.HasModelSettings);
    }

    [Fact]
    public async Task PinningMovesTheRowAtOnceAndKeepsThePcsPin()
    {
        var answer = new TaskCompletionSource<RemoteCommandResult>();
        var sink = new PinSink { Respond = _ => answer.Task };
        var chat = OpenSheet(sink, Catalog("claude-opus-5.5"));

        var pinning = chat.ToggleModelPinCommand.ExecuteAsync(Row(chat, "claude-sonnet-5"));

        // The row moves before the PC answers, as the desktop's does, and the PC is told what to keep.
        Assert.Equal(["claude-opus-5.5", "claude-sonnet-5"], Section(chat, "PINNED"));
        var command = Assert.Single(sink.Commands);
        Assert.Equal(RemoteProtocol.Actions.PinModel, command.Action);
        Assert.Equal("claude-sonnet-5", command.Get("model"));
        Assert.True(command.GetBool("pinned"));

        // A catalog that crossed the pin on its way does not undo it, and the other pins stay usable.
        chat.ApplyCatalogs(Catalog("claude-opus-5.5"));
        Assert.Equal(["claude-opus-5.5", "claude-sonnet-5"], Section(chat, "PINNED"));
        Assert.True(chat.ToggleModelPinCommand.CanExecute(Row(chat, "auto")));

        answer.SetResult(new RemoteCommandResult { Ok = true });
        await pinning;
        Assert.True(Row(chat, "claude-sonnet-5").IsPinned);
        Assert.Null(chat.ModelPinError);
        Assert.True(chat.IsModelSheetOpen, "a pin is a setting of the open picker, not a choice that dismisses it");
        Assert.Equal("gpt-6.1-sol", chat.Model);

        // Unpinned, the row returns to its account in the desktop's order.
        sink.Respond = _ => Task.FromResult(new RemoteCommandResult { Ok = true });
        await chat.ToggleModelPinCommand.ExecuteAsync(Row(chat, "claude-sonnet-5"));
        Assert.Equal(["claude-opus-5.5"], Section(chat, "PINNED"));
        Assert.Equal(["gpt-6.1-sol", "claude-sonnet-5", "auto"], Section(chat, "GITHUB COPILOT"));
        Assert.False(sink.Commands[^1].GetBool("pinned"));
    }

    [Fact]
    public async Task APinThePcRefusesPutsTheRowBackAndSaysWhy()
    {
        var sink = new PinSink
        {
            Respond = _ => Task.FromResult(new RemoteCommandResult { Error = "That model isn't available on this PC." })
        };
        var chat = OpenSheet(sink, Catalog("claude-opus-5.5"));

        await chat.ToggleModelPinCommand.ExecuteAsync(Row(chat, "claude-sonnet-5"));

        Assert.Equal(["claude-opus-5.5"], Section(chat, "PINNED"));
        Assert.False(Row(chat, "claude-sonnet-5").IsPinned);
        Assert.Equal("Couldn't pin Claude Sonnet 5. That model isn't available on this PC.", chat.ModelPinError);

        chat.IsModelSheetOpen = false;
        chat.OpenModelSheetCommand.Execute(null);
        Assert.Null(chat.ModelPinError);
    }

    /// <summary>
    /// A pin that fails returns the sheet to the PC's own pins, not merely the row's previous state: an
    /// unpinned row goes back to its place among them, and a pin the PC took anyway stays, unreported.
    /// </summary>
    [Fact]
    public async Task AFailedPinReturnsToThePcsPins()
    {
        var answer = new TaskCompletionSource<RemoteCommandResult>();
        var sink = new PinSink { Respond = _ => answer.Task };
        var chat = OpenSheet(sink, Catalog("claude-opus-5.5", "claude-sonnet-5"));

        var unpinning = chat.ToggleModelPinCommand.ExecuteAsync(Row(chat, "claude-opus-5.5"));
        Assert.Equal(["claude-sonnet-5"], Section(chat, "PINNED"));
        answer.SetResult(new RemoteCommandResult { Error = "The PC didn't answer." });
        await unpinning;
        Assert.Equal(["claude-opus-5.5", "claude-sonnet-5"], Section(chat, "PINNED"));
        Assert.Equal("Couldn't unpin Claude Opus 5.5. The PC didn't answer.", chat.ModelPinError);

        // The PC took this pin and its catalog said so, but the answer was lost on the way back.
        answer = new TaskCompletionSource<RemoteCommandResult>();
        var pinning = chat.ToggleModelPinCommand.ExecuteAsync(Row(chat, "gpt-6.1-sol"));
        chat.ApplyCatalogs(Catalog("claude-opus-5.5", "claude-sonnet-5", "gpt-6.1-sol"));
        answer.SetResult(new RemoteCommandResult { Error = "The PC didn't answer." });
        await pinning;
        Assert.Equal(["claude-opus-5.5", "claude-sonnet-5", "gpt-6.1-sol"], Section(chat, "PINNED"));
        Assert.Null(chat.ModelPinError);
    }

    [Fact]
    public async Task APinAnsweredAfterSwitchingPcsLeavesTheNewPcAlone()
    {
        var answer = new TaskCompletionSource<RemoteCommandResult>();
        var sink = new PinSink { Respond = _ => answer.Task };
        var chat = OpenSheet(sink, Catalog());

        var pinning = chat.ToggleModelPinCommand.ExecuteAsync(Row(chat, "claude-sonnet-5"));
        chat.ResetHostState();
        chat.ApplyCatalogs(Catalog());
        answer.SetResult(new RemoteCommandResult { Error = "The PC connection changed." });
        await pinning;

        Assert.DoesNotContain(chat.ModelOptions, option => option.IsPinned);
        Assert.DoesNotContain(chat.ModelGroups, group => group.Label == "PINNED");
        Assert.Null(chat.ModelPinError);
    }

    [Fact]
    public async Task APcThatCannotPinOffersNoPins()
    {
        var sink = new PinSink(supportsModelPins: false);
        var chat = OpenSheet(sink, new RemoteSettings
        {
            PreferredModel = "gpt-6.1-sol",
            AvailableModels = ["auto", "gpt-6.1-sol", "gpt-6-luna"],
            ModelDisplayNames = ["auto=Auto", "gpt-6.1-sol=GPT-6.1 Sol", "gpt-6-luna=GPT-6 Luna"]
        });

        Assert.All(chat.ModelOptions, option => Assert.False(option.CanPin));
        await chat.ToggleModelPinCommand.ExecuteAsync(Row(chat, "gpt-6-luna"));
        Assert.Empty(sink.Commands);
        Assert.False(Row(chat, "gpt-6-luna").IsPinned);

        // Without the desktop's rows the sheet still groups and orders plain ids as its picker does.
        Assert.Equal(["OTHER", "OPENAI"], chat.ModelGroups.Select(group => group.Label));
        Assert.Equal(["gpt-6.1-sol", "gpt-6-luna"], Section(chat, "OPENAI"));
        Assert.Equal("GPT-6 Luna", Row(chat, "gpt-6-luna").Label);
        Assert.Null(Row(chat, "gpt-6-luna").Cost);
    }

    /// <summary>
    /// The rendered sheet on a phone: every target is a fingertip, effort and context stay docked in
    /// view below a list longer than the sheet, a real tap on a pin pins without choosing the model,
    /// and a phone held sideways scrolls the settings with the list rather than squeezing it.
    /// </summary>
    [Fact]
    public Task TheSheetKeepsSettingsInReachAndPinsWithoutChoosing() => RunUiAsync(async (shell, window) =>
    {
        shell.Client.MarkProtocolCompatibleForTests(modelPins: true);
        var catalog = Catalog("claude-opus-5.5");
        var more = Enumerable.Range(1, 18).Select(index => new RemoteModelOption
        {
            Model = $"gpt-5.{index}", DisplayName = $"GPT-5.{index}", ProviderName = "GitHub Copilot", Cost = "1×", ContextSize = "128K"
        }).ToList();
        catalog.AvailableModels.AddRange(more.Select(row => row.Model));
        catalog.ModelOptions.AddRange(more);
        shell.Chat.ApplyCatalogs(catalog);
        shell.Chat.Reset(Guid.NewGuid(), "A chat", "gpt-6.1-sol");
        shell.Chat.OpenModelSheetCommand.Execute(null);
        await SettleAsync(window);

        var sheet = Named<StrataBottomSheet>(window, "ModelSheet");
        Assert.True(sheet.IsOpen);
        var layout = Named<DockPanel>(window, "ModelSheetLayout");
        var settings = Named<Border>(window, "ModelSheetSettings");
        var scroll = Named<ScrollViewer>(window, "ModelSheetScroll");
        Assert.Same(layout, settings.Parent);
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height, "the catalog is longer than the sheet");
        Assert.True(Named<Control>(window, "ModelSheetEffort").IsEffectivelyVisible);
        Assert.True(Named<Control>(window, "ModelSheetContext").IsEffectivelyVisible);
        // Docked: the settings close the sheet's content, which fits the screen, below the list's viewport.
        Assert.True(layout.Bounds.Height <= window.Bounds.Height, $"the sheet's content is {layout.Bounds.Height}dp tall");
        Assert.Equal(layout.Bounds.Height, settings.Bounds.Bottom, 1);
        Assert.True(scroll.Bounds.Bottom <= settings.Bounds.Top + 1);

        var rows = sheet.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("model-option") && button.IsEffectivelyVisible).ToList();
        var pins = sheet.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("model-pin") && button.IsEffectivelyVisible).ToList();
        Assert.NotEmpty(rows);
        Assert.Equal(rows.Count, pins.Count);
        Assert.All(rows, row => Assert.True(row.Bounds.Height >= 48, $"a row was {row.Bounds.Height}dp tall"));
        Assert.All(pins, pin => Assert.True(pin.Bounds is { Width: >= 48, Height: >= 48 }, $"a pin was {pin.Bounds}"));

        // A real tap on a row's pin reaches the pin, not the row: the model stays and the sheet stays
        // open. This PC is not connected, so the pin comes back and the sheet says why.
        var newestPin = pins.Single(pin => pin.DataContext is ModelPickerOption { Name: "gpt-5.18" });
        Tap(window, newestPin);
        Pump(window);
        Assert.True(sheet.IsOpen);
        Assert.Equal("gpt-6.1-sol", shell.Chat.Model);
        Assert.Equal("Couldn't pin GPT-5.18. Not connected to a Lumi desktop.", shell.Chat.ModelPinError);
        Assert.True(Named<Control>(window, "ModelPinErrorText").IsEffectivelyVisible);
        Assert.False(shell.Chat.ModelOptions.Single(option => option.Name == "gpt-5.18").IsPinned);

        // Held sideways, docked settings would leave the list a sliver, so they scroll with it.
        Resize(window, 852, 393);
        Assert.Same(Named<StackPanel>(window, "ModelSheetScrollContent"), settings.Parent);
        Resize(window, 412, 892);
        Assert.Same(layout, settings.Parent);
        Assert.Equal(0, layout.Children.IndexOf(settings));
        await SettleAsync(window);

        // A tap on the row itself chooses the model and closes the sheet.
        var opus = sheet.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Classes.Contains("model-option") && button.DataContext is ModelPickerOption { Name: "claude-opus-5.5" });
        Tap(window, opus);
        Assert.Equal("claude-opus-5.5", shell.Chat.Model);
        Assert.False(shell.Chat.IsModelSheetOpen);
    });

    /// <summary>
    /// A new chat starts on the PC's preferred model. Handed its first models with nothing selected,
    /// the composer selects the first one ("auto"): that fills a gap in the view and is not the user's
    /// choice, so it must neither outrank the PC's preference nor leave the untouched chat holding
    /// unsent work, which would keep the web app from updating itself.
    /// </summary>
    [Fact]
    public Task ANewChatStartsOnThePcsPreferredModel() => RunUiAsync((shell, window) =>
    {
        Assert.False(shell.Chat.HasChat);
        Assert.Null(shell.Chat.Model);

        shell.Chat.ApplyCatalogs(Catalog());
        Pump(window);

        var composer = Named<StrataChatComposer>(window, "Composer");
        Assert.Equal("gpt-6.1-sol", shell.Chat.Model);
        Assert.Equal("gpt-6.1-sol", composer.SelectedModel);
        Assert.Equal(["low", "medium", "high"], shell.Chat.QualityLevels);
        Assert.False(shell.Chat.HasPendingConfiguration);
        Assert.False(shell.Chat.HasUnsentWork);

        // Nothing was chosen on the phone, so the PC's next preference applies too.
        var changed = Catalog();
        changed.PreferredModel = "claude-sonnet-5";
        shell.Chat.ApplyCatalogs(changed);
        Pump(window);
        Assert.Equal("claude-sonnet-5", composer.SelectedModel);

        // A model picked on the phone is the user's, and a later catalog keeps it.
        shell.Chat.PickModelCommand.Execute("auto");
        shell.Chat.ApplyCatalogs(Catalog());
        Pump(window);
        Assert.Equal("auto", shell.Chat.Model);
        Assert.True(shell.Chat.HasPendingConfiguration);

        // A PC without a preference leaves the chat on the model the composer shows.
        shell.Chat.ResetHostState();
        var noPreference = Catalog();
        noPreference.PreferredModel = "";
        shell.Chat.ApplyCatalogs(noPreference);
        Pump(window);
        Assert.Equal("auto", shell.Chat.Model);
        Assert.Equal("Auto", shell.Chat.ModelDisplayName);
        Assert.False(shell.Chat.HasPendingConfiguration);
        return Task.CompletedTask;
    });

    private static async Task RunUiAsync(Func<MobileShellViewModel, Window, Task> test)
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(async () =>
        {
            await using var shell = new MobileShellViewModel(store: session.NewStore(), post: action =>
            {
                if (Dispatcher.UIThread.CheckAccess())
                    action();
                else
                    Dispatcher.UIThread.Post(action);
            });
            shell.IsPaired = shell.IsConnected = shell.IsHostReady = true;
            shell.HostName = "Preview PC";
            var window = new Window { Width = 412, Height = 892, Content = new MobileShellView { DataContext = shell } };
            try
            {
                window.Show();
                Pump(window);
                await test(shell, window);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
        failure?.Throw();
    }

    private static void Pump(Window window)
    {
        window.InvalidateMeasure();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Lets the sheet's opening motion finish, so taps land where the sheet comes to rest.</summary>
    private static async Task SettleAsync(Window window)
    {
        for (var pass = 0; pass < 12; pass++)
        {
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Resize(Window window, double width, double height)
    {
        window.Width = width;
        window.Height = height;
        Pump(window);
        Pump(window);
    }

    private static T Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    /// <summary>A real tap at the control's centre, so hit testing decides what receives it.</summary>
    private static void Tap(Window window, Control control)
    {
        var centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class PinSink(bool supportsModelPins = true) : IRemoteCommandSink
    {
        public List<RemoteCommand> Commands { get; } = [];

        public Func<RemoteCommand, Task<RemoteCommandResult>> Respond { get; set; } =
            _ => Task.FromResult(new RemoteCommandResult { Ok = true });

        public bool SupportsModelPins => supportsModelPins;

        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command)
        {
            Commands.Add(command);
            return Respond(command);
        }

        public Task<RemoteUploadResponse> UploadAsync(string fileName, ReadOnlyMemory<byte> content) =>
            Task.FromResult(new RemoteUploadResponse());
    }
}
