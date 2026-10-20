using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Views;
using Lumi.Remote.Protocol;
using Xunit;

namespace Lumi.Mobile.Tests;

[Collection("Headless mobile UI")]
public sealed class LibraryExperienceTests
{
    [Fact]
    public void DirtyCancelAndBackKeepTheDraftUntilDiscardIsExplicit()
    {
        var sink = new LibrarySink();
        var library = new LibraryViewModel(sink);
        var closed = false;
        library.CloseRequested += () => closed = true;
        library.BeginCreateCommand.Execute(null);
        Assert.False(library.HasUnsavedChanges);
        library.EditName = "Weekend project";
        library.EditBody = "Keep this unfinished thought.";
        library.EditWorkingDirectory = @"C:\Projects\Weekend";

        library.CancelEditCommand.Execute(null);

        Assert.True(library.IsEditing);
        Assert.True(library.IsDiscardConfirmationOpen);
        Assert.True(library.HasOpenSurface);
        Assert.False(closed);
        Assert.Equal("Weekend project", library.EditName);
        Assert.Equal("Keep this unfinished thought.", library.EditBody);
        Assert.Equal(@"C:\Projects\Weekend", library.EditWorkingDirectory);

        Assert.True(library.DismissTopmostSurface());
        Assert.False(library.IsDiscardConfirmationOpen);
        Assert.True(library.IsEditing);
        Assert.True(library.HasUnsavedChanges);
        library.CloseCommand.Execute(null);
        Assert.True(library.IsDiscardConfirmationOpen);
        Assert.False(closed);

        library.DiscardChangesCommand.Execute(null);

        Assert.False(library.HasOpenSurface);
        Assert.False(library.IsEditing);
        Assert.Empty(library.EditName);
        Assert.Empty(sink.Commands);
        library.CloseCommand.Execute(null);
        Assert.True(closed);
    }

    [Fact]
    public void DirtySectionSelectionWaitsForDiscardAndKeepEditingClearsThePendingSelection()
    {
        var library = new LibraryViewModel(new LibrarySink());
        library.BeginCreateCommand.Execute(null);
        library.EditName = "Do not lose this";

        library.SectionIndex = (int)LibrarySection.Skills;

        Assert.Equal(LibrarySection.Projects, library.Section);
        Assert.Equal(0, library.SectionIndex);
        Assert.Equal("New project", library.PageTitle);
        Assert.True(library.IsDiscardConfirmationOpen);
        library.KeepEditingCommand.Execute(null);
        Assert.True(library.IsEditing);
        Assert.Equal("Do not lose this", library.EditName);

        library.CancelEditCommand.Execute(null);
        library.DiscardChangesCommand.Execute(null);
        Assert.Equal(LibrarySection.Projects, library.Section);

        library.BeginCreateCommand.Execute(null);
        library.EditName = "Another draft";
        library.Section = LibrarySection.Memories;
        Assert.Equal(LibrarySection.Projects, library.Section);
        library.DiscardChangesCommand.Execute(null);

        Assert.Equal(LibrarySection.Memories, library.Section);
        Assert.Equal(3, library.SectionIndex);
        Assert.False(library.IsEditing);
        Assert.False(library.IsDiscardConfirmationOpen);
    }

    [Fact]
    public void RestoringTheOriginalDraftDoesNotAskToDiscard()
    {
        var library = new LibraryViewModel(new LibrarySink()) { Section = LibrarySection.Lumis };
        library.BeginCreateCommand.Execute(null);
        var originalGlyph = library.EditGlyph;
        library.EditDescription = "Changed";
        library.EditGlyph = "★";
        Assert.True(library.HasUnsavedChanges);

        library.EditDescription = "";
        library.EditGlyph = originalGlyph;
        Assert.False(library.HasUnsavedChanges);
        library.CancelEditCommand.Execute(null);

        Assert.False(library.IsEditing);
        Assert.False(library.IsDiscardConfirmationOpen);
    }

    [Fact]
    public async Task DeleteIsOnlyARequestUntilConfirmedAndBackKeepsTheItem()
    {
        var sink = new LibrarySink();
        var library = new LibraryViewModel(sink);
        var entry = ProjectEntry();

        await library.DeleteCommand.ExecuteAsync(entry);

        Assert.Empty(sink.Commands);
        Assert.True(library.IsRowActionsOpen);
        Assert.True(library.IsConfirmingDelete);
        Assert.Contains(entry.Name, library.DeleteConfirmationDescription);
        Assert.Contains("computer", library.DeleteConfirmationDescription);
        Assert.Contains("phone", library.DeleteConfirmationDescription);

        Assert.True(library.DismissTopmostSurface());
        Assert.False(library.IsConfirmingDelete);
        Assert.True(library.IsRowActionsOpen);
        Assert.Same(entry, library.ActionEntry);
        Assert.Empty(sink.Commands);

        await library.DeleteActionEntryCommand.ExecuteAsync(null);
        Assert.Empty(sink.Commands);
        await library.ConfirmDeleteActionEntryCommand.ExecuteAsync(null);

        var command = Assert.Single(sink.Commands);
        Assert.Equal(RemoteProtocol.Actions.ConfigureFeature, command.Action);
        Assert.Equal(RemoteProtocol.Resources.Projects, command.Get("resource"));
        Assert.Equal("delete", command.Get("featureAction"));
        Assert.Equal(entry.Identifier, command.Get("identifier"));
        Assert.Equal(1, sink.RefreshCount);
        Assert.False(library.IsRowActionsOpen);
        Assert.False(library.IsConfirmingDelete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedDeleteKeepsTheSelectedItemConfirmationAndFeedback(bool throws)
    {
        const string error = "The PC could not delete this project.";
        var sink = new LibrarySink
        {
            Result = new RemoteCommandResult { Error = error },
            CommandError = throws ? new IOException(error) : null
        };
        var library = new LibraryViewModel(sink);
        var entry = ProjectEntry();
        library.OpenRowActionsCommand.Execute(entry);
        await library.DeleteActionEntryCommand.ExecuteAsync(null);

        await library.ConfirmDeleteActionEntryCommand.ExecuteAsync(null);

        Assert.Single(sink.Commands);
        Assert.True(library.IsRowActionsOpen);
        Assert.True(library.IsConfirmingDelete);
        Assert.Same(entry, library.ActionEntry);
        Assert.Contains(error, library.StatusMessage);
        Assert.False(library.IsActionBusy);
        Assert.True(library.ConfirmDeleteActionEntryCommand.CanExecute(null));
        Assert.Equal(0, sink.RefreshCount);
    }

    [Fact]
    public async Task BusyDeleteDisablesResubmissionAndCannotBeDismissedByBack()
    {
        var sink = new LibrarySink
        {
            PendingCommand = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var library = new LibraryViewModel(sink);
        var entry = ProjectEntry();
        await library.DeleteCommand.ExecuteAsync(entry);
        var deletion = library.ConfirmDeleteActionEntryCommand.ExecuteAsync(null);
        try
        {
            Assert.True(library.IsActionBusy);
            Assert.False(library.ConfirmDeleteActionEntryCommand.CanExecute(null));
            Assert.True(library.DismissTopmostSurface());
            library.CloseRowActionsCommand.Execute(null);
            Assert.True(library.IsConfirmingDelete);
            Assert.True(library.IsRowActionsOpen);
            Assert.Same(entry, library.ActionEntry);
            Assert.Single(sink.Commands);
        }
        finally
        {
            sink.PendingCommand.TrySetResult(new RemoteCommandResult { Error = "Try again." });
            await deletion;
        }
        Assert.False(library.IsActionBusy);
        Assert.Equal("Try again.", library.StatusMessage);
    }

    [Fact]
    public async Task APreviousHostsDeleteCannotOverwriteANewDraftOrReopenASheet()
    {
        var sink = new LibrarySink
        {
            PendingCommand = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var library = new LibraryViewModel(sink);
        await library.DeleteCommand.ExecuteAsync(ProjectEntry());
        var deletion = library.ConfirmDeleteActionEntryCommand.ExecuteAsync(null);

        library.ResetHostState();
        library.BeginCreateCommand.Execute(null);
        library.EditName = "New PC draft";
        sink.PendingCommand.SetResult(new RemoteCommandResult { Error = "Old PC error" });
        await deletion;

        Assert.True(library.IsEditing);
        Assert.Equal("New PC draft", library.EditName);
        Assert.Null(library.ActionEntry);
        Assert.False(library.IsRowActionsOpen);
        Assert.False(library.IsConfirmingDelete);
        Assert.Null(library.StatusMessage);
    }

    [Fact]
    public async Task SaveFailureRetainsTheFullDetailDraftAndRetrySavesThatDraft()
    {
        var entry = new LibraryEntryViewModel
        {
            Section = LibrarySection.Skills,
            Identifier = Guid.NewGuid().ToString(),
            Name = "Full skill"
        };
        var sink = new LibrarySink
        {
            Detail = new RemoteLibraryItem
            {
                Resource = RemoteProtocol.Resources.Skills,
                Identifier = entry.Identifier,
                Name = entry.Name,
                Description = "A full description",
                Body = "Full content that is not the list preview.",
                Glyph = "✦"
            },
            Result = new RemoteCommandResult { Error = "The PC is offline." }
        };
        var library = new LibraryViewModel(sink);
        await library.BeginEditCommand.ExecuteAsync(entry);
        Assert.Equal("Edit skill", library.PageTitle);
        Assert.False(library.HasUnsavedChanges);
        library.EditBody += "\nMy unsaved addition.";
        var draft = library.EditBody;

        await library.SaveCommand.ExecuteAsync(null);

        Assert.True(library.IsEditing);
        Assert.True(library.HasUnsavedChanges);
        Assert.Same(entry, library.SelectedEntry);
        Assert.Equal(draft, library.EditBody);
        Assert.Equal("The PC is offline.", library.StatusMessage);
        Assert.Equal(0, sink.RefreshCount);
        Assert.True(library.SaveCommand.CanExecute(null));

        sink.Result = new RemoteCommandResult { Ok = true };
        await library.SaveCommand.ExecuteAsync(null);
        Assert.Equal(draft, sink.Commands[^1].Get("content"));
        Assert.Equal("✦", sink.Commands[^1].Get("iconGlyph"));
        Assert.Equal("update", sink.Commands[^1].Get("featureAction"));
        Assert.False(library.IsEditing);
        Assert.Equal(1, sink.RefreshCount);
    }

    [Fact]
    public async Task SavingDisablesRepeatSubmissionAndAFailedSaveKeepsTheDraft()
    {
        var sink = new LibrarySink
        {
            PendingCommand = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var library = new LibraryViewModel(sink);
        library.BeginCreateCommand.Execute(null);
        library.EditName = "Unfinished project";
        var save = library.SaveCommand.ExecuteAsync(null);
        try
        {
            Assert.True(library.IsSaving);
            Assert.Equal("Saving…", library.SaveButtonText);
            Assert.False(library.CanEditFields);
            Assert.False(library.SaveCommand.CanExecute(null));
            Assert.Single(sink.Commands);
        }
        finally
        {
            sink.PendingCommand.TrySetResult(new RemoteCommandResult { Error = "PC unavailable." });
            await save;
        }

        Assert.False(library.IsSaving);
        Assert.True(library.IsEditing);
        Assert.True(library.HasUnsavedChanges);
        Assert.True(library.CanEditFields);
        Assert.Equal("Unfinished project", library.EditName);
        Assert.Equal("PC unavailable.", library.StatusMessage);
    }

    [Fact]
    public async Task FailedDetailLoadStaysInTheEditorAndCannotSaveAListPreview()
    {
        var sink = new LibrarySink();
        var library = new LibraryViewModel(sink);
        var entry = ProjectEntry();
        await library.BeginEditCommand.ExecuteAsync(entry);

        Assert.True(library.IsEditing);
        Assert.True(library.HasEditorLoadFailed);
        Assert.Same(entry, library.SelectedEntry);
        Assert.Equal(entry.Name, library.EditName);
        Assert.False(library.CanEditFields);
        Assert.False(library.SaveCommand.CanExecute(null));
        await library.SaveCommand.ExecuteAsync(null);
        Assert.Empty(sink.Commands);

        sink.Detail = new RemoteLibraryItem
        {
            Resource = RemoteProtocol.Resources.Projects,
            Identifier = entry.Identifier,
            Name = entry.Name,
            Body = "Full instructions"
        };
        await library.RetryEditorCommand.ExecuteAsync(null);
        Assert.False(library.HasEditorLoadFailed);
        Assert.True(library.CanEditFields);
        Assert.Equal("Full instructions", library.EditBody);
        Assert.False(library.HasUnsavedChanges);
    }

    [Theory]
    [InlineData(LibrarySection.McpServers, true, "Disable connector")]
    [InlineData(LibrarySection.McpServers, false, "Enable connector")]
    [InlineData(LibrarySection.Jobs, true, "Disable job")]
    [InlineData(LibrarySection.Jobs, false, "Enable job")]
    public async Task AvailabilityActionsDescribeTheCurrentStateWithoutOfferingEditing(
        LibrarySection section, bool enabled, string action)
    {
        var sink = new LibrarySink();
        var library = new LibraryViewModel(sink) { Section = section };
        var entry = new LibraryEntryViewModel
        {
            Section = section,
            Identifier = Guid.NewGuid().ToString(),
            Name = "PC resource",
            IsEnabled = enabled
        };
        Assert.False(library.CanCreate);
        Assert.False(library.BeginCreateCommand.CanExecute(null));
        library.BeginCreateCommand.Execute(null);
        await library.BeginEditCommand.ExecuteAsync(entry);
        Assert.False(library.IsEditing);
        Assert.Equal(0, sink.DetailCount);

        library.OpenRowActionsCommand.Execute(entry);
        Assert.Equal(action, library.ToggleActionText);
        Assert.True(library.CanToggleActionEntry);
        Assert.False(library.CanEditActionEntry);
        Assert.False(library.CanDeleteActionEntry);
        await library.DeleteActionEntryCommand.ExecuteAsync(null);
        Assert.False(library.IsConfirmingDelete);
        Assert.Empty(sink.Commands);
        await library.ToggleActionEntryCommand.ExecuteAsync(null);

        Assert.Equal((!enabled).ToString(), Assert.Single(sink.Commands).Get("isEnabled"));
        Assert.Equal(!enabled, entry.IsEnabled);
        Assert.False(library.IsRowActionsOpen);
    }

    [Theory]
    [InlineData(LibrarySection.Projects, "projects")]
    [InlineData(LibrarySection.Skills, "skills")]
    [InlineData(LibrarySection.Lumis, "agents")]
    [InlineData(LibrarySection.Memories, "memories")]
    [InlineData(LibrarySection.McpServers, "connectors")]
    [InlineData(LibrarySection.Jobs, "jobs")]
    public void EmptyAndNoResultsGuidanceNamesTheCurrentSection(LibrarySection section, string plural)
    {
        var library = new LibraryViewModel(new LibrarySink()) { Section = section };
        Assert.Equal(new[] { "Projects", "Skills", "Agents", "Memories", "Connectors", "Jobs" }, library.SectionNames);
        Assert.Equal((int)section, library.SectionIndex);
        Assert.True(library.IsEmpty);
        Assert.False(library.IsNoResults);
        Assert.Equal($"No {plural} yet", library.EmptyTitle);
        Assert.Contains("computer", library.EmptyDescription);
        Assert.DoesNotMatch(@"\bPC\b", library.EmptyDescription);
        if (section == LibrarySection.Lumis)
            Assert.Contains("add an agent", library.EmptyDescription);

        library.SearchText = "missing";
        Assert.True(library.IsNoResults);
        Assert.Equal($"No {plural} found", library.EmptyTitle);
        Assert.Contains(plural, library.EmptyDescription);
        Assert.Contains("clear the search", library.EmptyDescription);

        library.ClearSearchCommand.Execute(null);
        Assert.False(library.IsNoResults);
        Assert.Equal($"No {plural} yet", library.EmptyTitle);
    }

    [Fact]
    public void SearchingAnotherCategoryDoesNotShowMatchesFromThePreviousOne()
    {
        var library = new LibraryViewModel(new LibrarySink());
        library.Apply(new RemoteLibrary
        {
            Projects = [new RemoteProject { Id = Guid.NewGuid(), Name = "Roadmap" }],
            Skills = [new RemoteSkill { Id = Guid.NewGuid(), Name = "Debugging" }]
        });
        library.SearchText = "Roadmap";
        Assert.Single(library.Entries);

        library.Section = LibrarySection.Skills;
        Assert.True(library.IsNoResults);
        Assert.Equal("No skills found", library.EmptyTitle);
        Assert.Empty(library.Entries);

        library.ClearSearchCommand.Execute(null);
        Assert.False(library.IsNoResults);
        Assert.False(library.IsEmpty);
        Assert.Equal("Debugging", Assert.Single(library.Entries).Name);
    }

    [Theory]
    [InlineData(LibrarySection.Projects, "New project")]
    [InlineData(LibrarySection.Skills, "New skill")]
    [InlineData(LibrarySection.Lumis, "New agent")]
    [InlineData(LibrarySection.Memories, "New memory")]
    public async Task EditorHidesBrowsingControlsAndPinsLabeledActionsAboveTheKeyboard(
        LibrarySection section, string title)
    {
        var library = new LibraryViewModel(new LibrarySink()) { Section = section };
        await RunView(320, 700, library, (view, window, shell) =>
        {
            Assert.Equal("Library", Required<TextBlock>(view, "LibraryTitle").Text);
            Assert.True(Required<Control>(view, "SectionPicker").IsEffectivelyVisible);
            Assert.True(Required<Control>(view, "LibrarySearchBox").IsEffectivelyVisible);
            Assert.True(Required<Control>(view, "LibraryNewButton").IsEffectivelyVisible);

            library.BeginCreateCommand.Execute(null);
            Pump(window);

            Assert.Equal(title, Required<TextBlock>(view, "LibraryTitle").Text);
            Assert.False(Required<Control>(view, "SectionPicker").IsEffectivelyVisible);
            Assert.False(Required<Control>(view, "LibrarySearchBox").IsEffectivelyVisible);
            Assert.False(Required<Control>(view, "LibraryNewButton").IsEffectivelyVisible);
            Assert.False(Required<Control>(view, "LibraryEntries").IsEffectivelyVisible);
            Assert.True(Required<Control>(view, "LibraryEditor").IsEffectivelyVisible);
            Assert.True(Required<Control>(view, "EditNameLabel").IsEffectivelyVisible);
            Assert.Equal(library.ShowGlyphEditor, Required<Control>(view, "EditGlyphLabel").IsEffectivelyVisible);
            Assert.Equal(library.ShowDescriptionEditor, Required<Control>(view, "EditDescriptionLabel").IsEffectivelyVisible);
            Assert.Equal(library.ShowProjectWorkingDirectory,
                Required<Control>(view, "EditWorkingDirectoryLabel").IsEffectivelyVisible);
            Assert.Equal("Name", AutomationProperties.GetName(Required<TextBox>(view, "EditNameBox")));

            var save = Required<Button>(view, "EditSaveButton");
            var cancel = Required<Button>(view, "EditCancelButton");
            Assert.Empty(save.GetVisualAncestors().OfType<ScrollViewer>());
            Assert.Empty(cancel.GetVisualAncestors().OfType<ScrollViewer>());

            window.Height = 380;
            shell.UpdateLayout(320, 380);
            Required<ScrollViewer>(view, "LibraryEditorScrollViewer").Offset = new Vector(0, 1000);
            Pump(window);

            AssertInsideViewport(save, window);
            AssertInsideViewport(cancel, window);
            Assert.True(save.Bounds.Height >= 48);
            Assert.True(cancel.Bounds.Height >= 48);
            Assert.True(save.Bounds.Width >= 48);
            Assert.True(cancel.Bounds.Width >= 48);
        });
    }

    [Theory]
    [InlineData(320)]
    [InlineData(600)]
    [InlineData(900)]
    public async Task RowsOpenOnTapKeepRoomForNamesAndTheirActionsConfirmDeletion(int width)
    {
        var sink = new LibrarySink();
        var library = new LibraryViewModel(sink);
        library.Apply(new RemoteLibrary
        {
            Projects =
            [
                new RemoteProject
                {
                    Id = Guid.NewGuid(),
                    Name = "A project with a deliberately long, recognizable name",
                    Instructions = "Shared instructions remain readable on the narrow phone.",
                    ChatCount = 123456
                }
            ]
        });

        await RunView(width, 780, library, async (view, window, _) =>
        {
            var row = Required<Button>(view, "LibraryEntryButton");
            var name = Required<TextBlock>(row, "LibraryEntryName");
            Assert.Same(library.OpenEntryCommand, row.Command);
            Assert.True(name.Bounds.Width >= 120, $"The row name only has {name.Bounds.Width:0.#} DIP.");
            Assert.True(row.Bounds.Height >= 48);
            AssertInsideViewport(row, window);

            // A long press or a secondary click asks for the row's actions.
            row.RaiseEvent(new ContextRequestedEventArgs());
            Pump(window);
            Assert.True(library.IsRowActionsOpen);
            Required<Button>(view, "LibraryDeleteButton").Command!.Execute(null);
            Pump(window);
            Assert.Empty(sink.Commands);
            Assert.True(library.IsConfirmingDelete);
            Assert.False(Required<Control>(view, "LibraryActionOptions").IsEffectivelyVisible);
            await Task.Delay(300);
            Pump(window);
            var confirm = Required<Button>(view, "LibraryConfirmDeleteButton");
            Assert.True(confirm.IsEffectivelyVisible);
            Assert.True(confirm.Bounds.Height >= 48);
            AssertInsideViewport(confirm, window);
        });
    }

    /// <summary>
    /// Every row opens on a real tap. Lumi's built-in skills and agents open read-only with their full
    /// content from the PC, and connectors and jobs switch on and off right on their row.
    /// </summary>
    [Fact]
    public async Task EveryRowOpensAndOffersOnlyWhatThePhoneCanDo()
    {
        var sink = new LibrarySink { Detail = new RemoteLibraryItem { Name = "Built-in skill", Body = "Step one, then step two." } };
        var library = new LibraryViewModel(sink) { Section = LibrarySection.Skills };
        library.Apply(new RemoteLibrary
        {
            Skills = [new RemoteSkill { Id = Guid.NewGuid(), Name = "Built-in skill", IsBuiltIn = true }],
            McpServers = [new RemoteMcpServer { Id = Guid.NewGuid(), Name = "Tools", ServerType = "local", IsEnabled = true }]
        });
        await RunView(320, 700, library, async (view, window, _) =>
        {
            await Task.Delay(320);
            Pump(window);
            Tap(window, Required<Button>(view, "LibraryEntryButton"));
            Assert.True(library.IsViewing);
            Assert.False(library.IsEditing);
            Assert.Equal(1, sink.DetailCount);
            await Task.Delay(320);
            Pump(window);
            Assert.Equal("Step one, then step two.", Required<SelectableTextBlock>(view, "LibraryViewerBody").Text);
            Assert.False(Required<Control>(view, "LibraryViewerActionsButton").IsEffectivelyVisible);
            library.OpenRowActionsCommand.Execute(library.Entries[0]);
            Assert.False(library.IsRowActionsOpen);
            Assert.True(library.DismissTopmostSurface());
            Assert.False(library.IsViewing);

            library.Section = LibrarySection.McpServers;
            Pump(window);
            await Task.Delay(320);
            Pump(window);
            Assert.False(Required<Control>(view, "LibraryNewButton").IsEffectivelyVisible);
            var toggle = Required<ToggleSwitch>(view, "LibraryEntrySwitch");
            Assert.True(toggle.IsEffectivelyVisible);
            Assert.True(toggle.IsChecked);
            Tap(window, toggle);
            var command = Assert.Single(sink.Commands);
            Assert.Equal("False", command.Arguments["isEnabled"]);
            Assert.False(library.Entries[0].IsEnabled);
            Assert.False(library.IsRowActionsOpen, "a switch never opens a sheet");
            Assert.False(library.IsViewing, "a switch never opens the page");

            Tap(window, Required<Button>(view, "LibraryEntryButton"));
            Assert.True(library.IsViewing);
            library.OpenRowActionsCommand.Execute(library.Entries[0]);
            Pump(window);
            Assert.False(Required<Control>(view, "LibraryEditButton").IsEffectivelyVisible);
            Assert.False(Required<Control>(view, "LibraryDeleteButton").IsEffectivelyVisible);
            Assert.True(Required<Control>(view, "LibraryToggleButton").IsEffectivelyVisible);
        });
    }

    [Fact]
    public async Task ASwitchThePcRefusesGoesBack()
    {
        var sink = new LibrarySink { Result = new RemoteCommandResult { Ok = false, Error = "Jobs are paused on this PC." } };
        var library = new LibraryViewModel(sink) { Section = LibrarySection.Jobs };
        library.Apply(new RemoteLibrary
        {
            Jobs = [new RemoteJob { Id = Guid.NewGuid(), Name = "Morning briefing", IsEnabled = true }]
        });
        var entry = library.Entries[0];
        var raised = 0;
        entry.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(LibraryEntryViewModel.IsEnabled))
                raised++;
        };

        await library.ToggleEntryCommand.ExecuteAsync(entry);

        Assert.True(entry.IsEnabled);
        Assert.True(raised > 0, "the switch is told to show the PC's state again");
        Assert.Equal("Jobs are paused on this PC.", library.StatusMessage);
        Assert.False(entry.IsSwitchBusy);
    }

    [Fact]
    public void ListsGroupYoursBeforeBuiltInsAndWhatIsOnBeforeWhatIsPaused()
    {
        var library = new LibraryViewModel(new LibrarySink()) { Section = LibrarySection.Lumis };
        library.Apply(new RemoteLibrary
        {
            Lumis =
            [
                new RemoteLumi { Id = Guid.NewGuid(), Name = "Coding Lumi", IsBuiltIn = true },
                new RemoteLumi { Id = Guid.NewGuid(), Name = "Release manager" },
                new RemoteLumi { Id = Guid.NewGuid(), Name = "Daily Planner", IsBuiltIn = true }
            ],
            Jobs =
            [
                new RemoteJob { Id = Guid.NewGuid(), Name = "Old watcher", IsEnabled = false },
                new RemoteJob { Id = Guid.NewGuid(), Name = "Morning briefing", IsEnabled = true, ScheduleSummary = "Every day at 08:00", LastRunStatus = "Completed" }
            ]
        });

        Assert.Equal(["Release manager", "Coding Lumi", "Daily Planner"], library.Entries.Select(entry => entry.Name));
        Assert.Equal(["Yours", "Built in", null], library.Entries.Select(entry => entry.GroupLabel));
        Assert.Equal([true, true, false], library.Entries.Select(entry => entry.IsGroupStart));
        Assert.Equal([true, false, true], library.Entries.Select(entry => entry.IsGroupEnd));
        Assert.All(library.Entries.Where(entry => entry.IsBuiltIn), entry => Assert.NotNull(entry.ReadOnlyNote));
        Assert.Null(library.ScopeSummary);

        Assert.True(library.RequestScope(LibraryScope.Jobs));
        Assert.Equal(LibrarySection.Jobs, library.Section);
        Assert.Equal("1 on · 1 paused", library.ScopeSummary);
        Assert.Equal(["Morning briefing", "Old watcher"], library.Entries.Select(entry => entry.Name));
        Assert.Equal(["On", "Paused"], library.Entries.Select(entry => entry.GroupLabel));
        Assert.All(library.Entries, entry => Assert.True(entry.IsGroupStart && entry.IsGroupEnd));
        Assert.Equal("Every day at 08:00 · Completed", library.Entries[0].Subtitle);
        Assert.All(library.Entries, entry => Assert.True(entry.HasSwitch));

        // A search keeps each match's edges true to what is shown.
        library.SearchText = "watcher";
        var match = Assert.Single(library.Entries);
        Assert.True(match.IsGroupStart && match.IsGroupEnd);
        Assert.Equal("Paused", match.GroupLabel);
    }

    /// <summary>
    /// Jobs and connectors are switched like settings, so each group sits on one card: its first
    /// row rounds the top, its last the bottom, and hairlines divide the rows between them. What is
    /// opened (agents, skills, projects) stays a plain row, as on the desktop's Customize lists.
    /// </summary>
    [Fact]
    public async Task WhatSwitchesSitsOnOneCardPerGroupAndWhatOpensStaysAPlainRow()
    {
        var library = new LibraryViewModel(new LibrarySink()) { Section = LibrarySection.Jobs };
        library.Apply(new RemoteLibrary
        {
            Jobs =
            [
                new RemoteJob { Id = Guid.NewGuid(), Name = "Morning briefing", IsEnabled = true, ScheduleSummary = "Every day at 08:00", LastRunStatus = "Completed" },
                new RemoteJob { Id = Guid.NewGuid(), Name = "Evening digest", IsEnabled = true, ScheduleSummary = "Every day at 21:00", LastRunStatus = "Completed" },
                new RemoteJob { Id = Guid.NewGuid(), Name = "Old watcher", IsEnabled = false }
            ],
            Lumis = [new RemoteLumi { Id = Guid.NewGuid(), Name = "Release manager", Description = "Ships Lumi builds" }]
        });
        await RunView(320, 700, library, async (view, window, _) =>
        {
            await Task.Delay(320);
            Pump(window);
            Border[] Segments() => [.. view.GetVisualDescendants().OfType<Border>().Where(border => border.Name == "LibraryRowSegment")];

            var segments = Segments();
            Assert.Equal(3, segments.Length);
            Assert.All(segments, segment => Assert.Contains("card", segment.Classes));
            Assert.All(segments, segment => Assert.Equal(255, ColorOf(segment.Background).A));
            Assert.Equal(new CornerRadius(14, 14, 0, 0), segments[0].CornerRadius);
            Assert.Equal(new CornerRadius(0, 0, 14, 14), segments[1].CornerRadius);
            Assert.Equal(new CornerRadius(14), segments[2].CornerRadius);
            Assert.Equal(new Thickness(1, 1, 1, 0), segments[0].BorderThickness);
            Assert.Equal(new Thickness(1, 0, 1, 1), segments[1].BorderThickness);

            // The group's rows join into one card, divided by a hairline only between them.
            var firstBottom = segments[0].TranslatePoint(new Point(0, segments[0].Bounds.Height), window)!.Value.Y;
            var secondTop = segments[1].TranslatePoint(default, window)!.Value.Y;
            Assert.Equal(firstBottom, secondTop, 1);
            bool DividerShown(Border segment) => Required<Border>(segment, "LibraryRowDivider").IsEffectivelyVisible;
            Assert.Equal([false, true, false], segments.Select(DividerShown));

            // A switch row reads as one line, and its text keeps clear of the switch.
            var subtitle = segments[0].GetVisualDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("entry-subtitle"));
            Assert.Equal(1, subtitle.MaxLines);
            var name = Required<TextBlock>(segments[0], "LibraryEntryName");
            var toggle = Required<ToggleSwitch>(segments[0], "LibraryEntrySwitch");
            Assert.True(name.TranslatePoint(new Point(name.Bounds.Width, 0), window)!.Value.X
                        <= toggle.TranslatePoint(default, window)!.Value.X);

            library.Section = LibrarySection.Lumis;
            Pump(window);
            await Task.Delay(320);
            Pump(window);
            var plain = Assert.Single(Segments());
            Assert.DoesNotContain("card", plain.Classes);
            Assert.True(plain.Background is null, "a row that opens is not framed");
            Assert.False(DividerShown(plain));
            Assert.Contains("free", Required<Button>(plain, "LibraryEntryButton").Classes);
        });
    }

    private static Color ColorOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    [Fact]
    public async Task AJobsPageOpensTheChatItRunsIn()
    {
        var chatId = Guid.NewGuid();
        var library = new LibraryViewModel(new LibrarySink()) { Section = LibrarySection.Jobs };
        library.Apply(new RemoteLibrary
        {
            Jobs = [new RemoteJob { Id = Guid.NewGuid(), Name = "Morning briefing", ChatId = chatId, IsEnabled = true }]
        });
        (Guid Id, string Title)? requested = null;
        library.ChatRequested += (id, title) => requested = (id, title);

        await library.OpenEntryCommand.ExecuteAsync(library.Entries[0]);
        Assert.True(library.IsViewing);
        Assert.True(library.ViewedEntry!.CanOpenChat);
        library.OpenViewedChatCommand.Execute(null);

        Assert.Equal((chatId, "Morning briefing"), requested);
    }

    [Fact]
    public void UnchangedLibrarySnapshotsPreserveRowsAndDoNotRebuildTheList()
    {
        var library = new LibraryViewModel(new LibrarySink()) { Section = LibrarySection.Skills };
        var catalog = new RemoteLibrary
        {
            Skills = [new RemoteSkill { Id = Guid.NewGuid(), Name = "Review", Description = "Review code" }]
        };
        library.Apply(catalog);
        var row = library.Entries[0];
        var changes = 0;
        library.Entries.CollectionChanged += (_, _) => changes++;

        library.Apply(catalog);

        Assert.Same(row, library.Entries[0]);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task LargeLibraryTabsRealizeOnlyVisibleRowsAndSearchTheEntireCatalog()
    {
        var library = new LibraryViewModel(new LibrarySink());
        var catalog = new RemoteLibrary
        {
            Skills = Enumerable.Range(0, 700).Select(index => new RemoteSkill
            {
                Id = Guid.NewGuid(), Name = $"Skill {index}", Description = "A reusable instruction for your PC."
            }).ToList(),
            Memories = Enumerable.Range(0, 700).Select(index => new RemoteMemory
            {
                Id = Guid.NewGuid(), Key = $"Memory {index}", Content = "Remember this detail."
            }).ToList()
        };
        library.Apply(catalog);
        await RunView(360, 780, library, (view, window, _) =>
        {
            foreach (var section in new[] { LibrarySection.Skills, LibrarySection.Memories, LibrarySection.Skills })
            {
                library.Section = section;
                Pump(window);
                var list = Required<ItemsControl>(view, "LibraryEntries");
                var rows = list.GetVisualDescendants().OfType<Button>()
                    .Count(button => button.Name == "LibraryEntryButton");
                Assert.InRange(rows, 1, 30);
                Assert.Equal(700, library.Entries.Count);
            }

            var listViewport = Required<ItemsControl>(view, "LibraryEntries")
                .GetVisualAncestors().OfType<ScrollViewer>().First();
            listViewport.Offset = new Vector(0, 12000);
            Pump(window);
            library.SearchText = "Skill 699";
            Pump(window);
            Assert.Equal("Skill 699", Assert.Single(library.Entries).Name);
            Assert.Contains(Required<ItemsControl>(view, "LibraryEntries").GetVisualDescendants().OfType<TextBlock>(),
                text => text.Name == "LibraryEntryName" && text.Text == "Skill 699");
        });
    }

    private static LibraryEntryViewModel ProjectEntry() => new()
    {
        Section = LibrarySection.Projects,
        Identifier = Guid.NewGuid().ToString(),
        Name = "Shared weekend project"
    };

    private static Task RunView(int width, int height, LibraryViewModel library,
        Action<LibraryView, Window, MobileShellViewModel> assert) =>
        RunView(width, height, library, (view, window, shell) =>
        {
            assert(view, window, shell);
            return Task.CompletedTask;
        });

    private static async Task RunView(int width, int height, LibraryViewModel library,
        Func<LibraryView, Window, MobileShellViewModel, Task> assert)
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(async () =>
        {
            MobileShellViewModel? shell = null;
            Window? window = null;
            try
            {
                shell = new MobileShellViewModel(store: session.NewStore(), post: action => action());
                shell.IsPaired = true;
                shell.IsSidebarCollapsed = true;
                shell.Page = MobilePage.Library;
                shell.UpdateLayout(width, height);
                window = new Window
                {
                    Width = width,
                    Height = height,
                    Content = new MobileShellView { DataContext = shell }
                };
                window.Show();
                Pump(window);
                var view = Required<LibraryView>(window, "LibraryPage");
                view.DataContext = library;
                Pump(window);
                await assert(view, window, shell);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window?.Close();
                if (shell is not null)
                    await shell.DisposeAsync();
            }
        }, CancellationToken.None);
        failure?.Throw();
    }

    private static T Required<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static void Tap(Window window, Control control)
    {
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Pump(window);
    }

    private static void Pump(Window window)
    {
        window.InvalidateMeasure();
        Dispatcher.UIThread.RunJobs();
    }

    private static void AssertInsideViewport(Control control, Window window)
    {
        var origin = control.TranslatePoint(default, window)!.Value;
        Assert.True(control.IsEffectivelyVisible);
        Assert.True(origin.X >= -1 && origin.Y >= -1);
        Assert.True(origin.X + control.Bounds.Width <= window.ClientSize.Width + 1);
        Assert.True(origin.Y + control.Bounds.Height <= window.ClientSize.Height + 1);
    }

    private sealed class LibrarySink : IRemoteCommandSink, IRemoteLibraryDetailSink, IRemoteCatalogRefreshSink
    {
        public List<RemoteCommand> Commands { get; } = [];
        public RemoteLibraryItem? Detail { get; set; }
        public RemoteCommandResult Result { get; set; } = new() { Ok = true };
        public Exception? CommandError { get; set; }
        public TaskCompletionSource<RemoteCommandResult>? PendingCommand { get; init; }
        public int RefreshCount { get; private set; }
        public int DetailCount { get; private set; }

        public Task<RemoteCommandResult> SendCommandAsync(RemoteCommand command)
        {
            Commands.Add(command);
            return CommandError is { } error
                ? Task.FromException<RemoteCommandResult>(error)
                : PendingCommand?.Task ?? Task.FromResult(Result);
        }

        public Task<RemoteUploadResponse> UploadAsync(string fileName, ReadOnlyMemory<byte> content) =>
            Task.FromResult(new RemoteUploadResponse { Ok = true });

        public Task<RemoteLibraryItem?> GetLibraryItemAsync(string resource, string identifier)
        {
            DetailCount++;
            return Task.FromResult(Detail);
        }

        public Task RefreshCatalogsAsync()
        {
            RefreshCount++;
            return Task.CompletedTask;
        }
    }
}
