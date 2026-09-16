using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
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
        Assert.Contains("PC", library.DeleteConfirmationDescription);
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
    [InlineData(LibrarySection.McpServers, true, "Disable MCP server")]
    [InlineData(LibrarySection.McpServers, false, "Enable MCP server")]
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
    [InlineData(LibrarySection.Lumis, "Lumis")]
    [InlineData(LibrarySection.Memories, "memories")]
    [InlineData(LibrarySection.McpServers, "MCP servers")]
    [InlineData(LibrarySection.Jobs, "jobs")]
    public void EmptyAndNoResultsGuidanceNamesTheCurrentSection(LibrarySection section, string plural)
    {
        var library = new LibraryViewModel(new LibrarySink()) { Section = section };
        Assert.Equal(new[] { "Projects", "Skills", "Lumis", "Memories", "MCP", "Jobs" }, library.SectionNames);
        Assert.Equal((int)section, library.SectionIndex);
        Assert.True(library.IsEmpty);
        Assert.False(library.IsNoResults);
        Assert.Equal($"No {plural} yet", library.EmptyTitle);
        Assert.Contains("PC", library.EmptyDescription);
        Assert.False(string.IsNullOrWhiteSpace(library.SectionDescription));

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
    [InlineData(LibrarySection.Lumis, "New Lumi")]
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
    public async Task RowsExposeA48DipActionWithoutSqueezingNamesAndConfirmationFits(int width)
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
            var action = Required<Button>(view, "LibraryRowActionsButton");
            var name = Required<TextBlock>(row, "LibraryEntryName");
            Assert.Same(library.BeginEditCommand, row.Command);
            Assert.Same(library.OpenRowActionsCommand, action.Command);
            Assert.True(name.Bounds.Width >= 120, $"The row name only has {name.Bounds.Width:0.#} DIP.");
            Assert.InRange(action.Bounds.Width, 48, 56);
            Assert.InRange(action.Bounds.Height, 48, 56);
            AssertInsideViewport(row, window);
            AssertInsideViewport(action, window);
            Assert.Contains(library.Entries[0].Name, AutomationProperties.GetName(action));
            var rowOrigin = row.TranslatePoint(default, window)!.Value;
            var actionOrigin = action.TranslatePoint(default, window)!.Value;
            Assert.True(rowOrigin.X + row.Bounds.Width <= actionOrigin.X);

            action.Command!.Execute(action.CommandParameter);
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

    [Fact]
    public async Task BuiltInsAndPcManagedRowsDoNotRenderUnsupportedActions()
    {
        var library = new LibraryViewModel(new LibrarySink()) { Section = LibrarySection.Skills };
        library.Apply(new RemoteLibrary
        {
            Skills = [new RemoteSkill { Id = Guid.NewGuid(), Name = "Built-in skill", IsBuiltIn = true }],
            McpServers = [new RemoteMcpServer { Id = Guid.NewGuid(), Name = "Tools", IsEnabled = true }]
        });
        await RunView(320, 700, library, (view, window, _) =>
        {
            Assert.False(Required<Control>(view, "LibraryEntryButton").IsEffectivelyVisible);
            Assert.False(Required<Control>(view, "LibraryRowActionsButton").IsEffectivelyVisible);
            Assert.True(Required<Control>(view, "LibraryReadOnlyRow").IsEffectivelyVisible);
            library.OpenRowActionsCommand.Execute(library.Entries[0]);
            Assert.False(library.IsRowActionsOpen);

            library.Section = LibrarySection.McpServers;
            Pump(window);
            Assert.False(Required<Control>(view, "LibraryNewButton").IsEffectivelyVisible);
            Assert.False(Required<Control>(view, "LibraryEntryButton").IsEffectivelyVisible);
            var actions = Required<Button>(view, "LibraryRowActionsButton");
            Assert.True(actions.IsEffectivelyVisible);
            actions.Command!.Execute(actions.CommandParameter);
            Pump(window);
            Assert.False(Required<Control>(view, "LibraryEditButton").IsEffectivelyVisible);
            Assert.False(Required<Control>(view, "LibraryDeleteButton").IsEffectivelyVisible);
            Assert.True(Required<Control>(view, "LibraryToggleButton").IsEffectivelyVisible);
        });
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
