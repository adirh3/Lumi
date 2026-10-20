using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Lumi.Tests;

public sealed class ChatPauseControlsTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] PauseStringKeys =
    [
        "Chat_Pause", "Chat_Resume", "Chat_Paused", "Chat_Pausing",
        "Chat_PauseDescription", "Chat_PausingDescription",
        "Chat_PauseTooltip", "Chat_ResumeTooltip", "Chat_PausedPlaceholder", "Chat_PausedSendBlocked",
        "Chat_ActivitySummary", "Chat_AllChats", "Chat_Actions", "Chat_PauseFailed", "Chat_ResumeFailed",
        "Chat_PauseAll", "Chat_ResumeAll",
        "Chat_PauseAllTooltip", "Chat_ResumeAllTooltip", "Chat_ActivityScope",
        "Chat_PauseMenu", "Chat_ResumeMenu", "Chat_PausedIndicatorTooltip",
        "Chat_StopSession",
    ];

    [Fact]
    public void PauseAction_IsContextualAndBesideComposer_NotInTheHeader()
    {
        var view = LoadView("ChatView.axaml");
        var toggle = Named(view, "PauseChatButton");
        var header = view.Descendants().Single(element => element.Name.LocalName == "StrataChatShell.Header");

        Assert.Equal("{Binding TogglePauseCommand}", Attribute(toggle, "Command"));
        Assert.Equal("{Binding CanPauseChat}", Attribute(toggle, "IsVisible"));
        Assert.Contains(Named(view, "CodingStrip"), toggle.Ancestors());
        Assert.DoesNotContain(header.Descendants(),
            element => Attribute(element, "Command") == "{Binding TogglePauseCommand}"
                       || Attribute(element, "Command") == "{Binding ResumeChatCommand}");
        AssertAccessibleButton(toggle, "{loc:Str Chat_Pause}");
        Assert.Equal("{loc:Str Chat_PauseTooltip}", Attribute(toggle, "ToolTip.Tip"));
    }

    [Fact]
    public void PauseNotice_PreservesComposerAndEditingStatusContent()
    {
        var view = LoadView("ChatView.axaml");
        var notice = Named(view, "ChatPauseStatusPanel");
        var composer = Named(view, "Composer");

        Assert.Same(composer.Parent, notice.Parent);
        Assert.Contains(composer.ElementsBeforeSelf(), element => ReferenceEquals(element, notice));
        Assert.Equal("{Binding IsPaused}", Attribute(notice, "IsVisible"));
        Assert.Equal("{Binding PauseStatusTitle}", Attribute(Named(view, "ChatPauseStatusTitle"), "Text"));
        Assert.Equal("{Binding PauseStatusDescription}", Attribute(Named(view, "ChatPauseStatusDescription"), "Text"));

        var resume = Named(view, "ResumeChatButton");
        Assert.Equal("{Binding ResumeChatCommand}", Attribute(resume, "Command"));
        AssertAccessibleButton(resume, "{loc:Str Chat_Resume}");

        var stop = Named(view, "StopPausedChatButton");
        Assert.Equal("{Binding StopGenerationCommand}", Attribute(stop, "Command"));
        Assert.Equal("{loc:Str Chat_CancelPausedRun}", Attribute(stop, "Content"));

        Assert.Equal("{Binding IsComposerBusy}", Attribute(composer, "IsBusy"));
        Assert.Equal("{Binding PromptText, Mode=TwoWay}", Attribute(composer, "PromptText"));
        Assert.Equal("{Binding ComposerPlaceholder}", Attribute(composer, "Placeholder"));

        var editingStatus = composer.Element(composer.Name.Namespace + "StrataChatComposer.StatusContent");
        Assert.NotNull(editingStatus);
        Assert.Equal("{Binding IsEditingMessage}", Attribute(Assert.Single(editingStatus.Elements()), "IsVisible"));
        Assert.Contains(editingStatus.Descendants(Avalonia + "Button"),
            button => Attribute(button, "Command") == "{Binding CancelComposerEditCommand}");
        Assert.Contains(editingStatus.Descendants(Avalonia + "Button"),
            button => Attribute(button, "Command") == "{Binding SendMessageCommand}");
    }

    [Fact]
    public void SidebarActions_TargetTheirChatAndReplaceRunningIndicator()
    {
        var view = LoadView("MainWindow.axaml");
        var pause = Named(view, "PauseChatMenuItem");
        var resume = Named(view, "ResumeChatMenuItem");

        foreach (var item in new[] { pause, resume })
        {
            Assert.Equal("{Binding $parent[Window].DataContext.ToggleChatPauseCommand}", Attribute(item, "Command"));
            Assert.Equal("{Binding}", Attribute(item, "CommandParameter"));
            Assert.False(string.IsNullOrWhiteSpace(Attribute(item, "AutomationProperties.Name")));
        }

        Assert.Equal("{Binding CanPause}", Attribute(pause, "IsVisible"));
        Assert.Equal("{Binding IsPaused}", Attribute(resume, "IsVisible"));
        Assert.Equal("{Binding IsPaused}", Attribute(Named(view, "PausedChatIndicator"), "IsVisible"));
        Assert.Equal("{Binding ShowRunningIndicator}", Attribute(Named(view, "BusyIndicator"), "IsVisible"));
        Assert.Equal("{Binding HasBackgroundActivity}", Attribute(Named(view, "BackgroundChatIndicator"), "IsVisible"));
    }

    [Fact]
    public void BulkActions_AreInTheChatActionsMenu_NotAStandingToolbar()
    {
        var view = LoadView("MainWindow.axaml");
        var actions = Named(view, "ChatActionsButton");
        Assert.DoesNotContain(view.Descendants(),
            element => (string?)element.Attribute(Xaml + "Name") == "ChatActivityBar");
        var pause = Named(view, "PauseAllChatsMenuItem");
        Assert.Contains(actions, pause.Ancestors());
        Assert.Equal("{Binding PauseAllChatsCommand}", Attribute(pause, "Command"));
        Assert.Equal("{Binding CanPauseAllChats}", Attribute(pause, "IsVisible"));

        var resume = Named(view, "ResumeAllChatsMenuItem");
        Assert.Equal("{Binding ResumeAllChatsCommand}", Attribute(resume, "Command"));
        Assert.Equal("{Binding CanResumeAllChats}", Attribute(resume, "IsVisible"));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("he")]
    public void PauseStrings_ArePresentAndPreserveBothSummaryCounts(string language)
    {
        using var strings = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(FindLumiSourceRoot(), "Resources", "Strings", $"{language}.json")));

        foreach (var key in PauseStringKeys)
        {
            var value = strings.RootElement.GetProperty(key).GetString();
            Assert.False(string.IsNullOrWhiteSpace(value), $"Missing {language} localization: {key}");
        }

        var summary = strings.RootElement.GetProperty("Chat_ActivitySummary").GetString()!;
        Assert.Contains("{0}", summary);
        Assert.Contains("{1}", summary);
        var formatted = string.Format(CultureInfo.InvariantCulture, summary, 2, 3);
        Assert.Contains("2", formatted);
        Assert.Contains("3", formatted);
    }

    private static void AssertAccessibleButton(XElement button, string name)
    {
        Assert.Equal("True", Attribute(button, "Focusable"));
        Assert.Equal(name, Attribute(button, "AutomationProperties.Name"));
        Assert.False(string.IsNullOrWhiteSpace(Attribute(button, "AutomationProperties.HelpText")));
    }

    private static XDocument LoadView(string name) =>
        XDocument.Load(Path.Combine(FindLumiSourceRoot(), "Views", name));

    private static XElement Named(XDocument view, string name) =>
        view.Descendants().Single(element => (string?)element.Attribute(Xaml + "Name") == name);

    private static string? Attribute(XElement element, string name) => (string?)element.Attribute(name);

    private static string FindLumiSourceRoot([CallerFilePath] string sourceFilePath = "")
    {
        for (var directory = new FileInfo(sourceFilePath).Directory; directory is not null; directory = directory.Parent)
        {
            var sourceRoot = Path.Combine(directory.FullName, "src", "Lumi");
            if (File.Exists(Path.Combine(sourceRoot, "Lumi.csproj")))
                return sourceRoot;
        }

        throw new DirectoryNotFoundException("Could not locate the Lumi source tree.");
    }
}
