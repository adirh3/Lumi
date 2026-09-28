using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Sharing;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class CapabilitySharingViewModelTests
{
    private static string NewTempDir()
        => Path.Combine(Path.GetTempPath(), "LumiShareVmTests", Guid.NewGuid().ToString("N"));

    private static DataStore NewStore(AppData? data = null)
        => new(data ?? new AppData { Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false } }, NewTempDir());

    private static (DataStore Store, LumiAgent Agent) StoreWithResearchLumi()
    {
        var store = NewStore();
        var skill = new Skill { Name = "Citations", Description = "Cite sources.", Content = "Always cite.", IconGlyph = "🔖" };
        var server = new McpServer
        {
            Name = "Brave Search",
            Command = "npx",
            Args = ["-y", "@modelcontextprotocol/server-brave-search"],
            Env = new() { ["BRAVE_API_KEY"] = "super-secret-brave-key" }
        };
        var agent = new LumiAgent
        {
            Name = "Research Buddy",
            SystemPrompt = "Research carefully.",
            IconGlyph = "🔎",
            SkillIds = [skill.Id],
            McpServerIds = [server.Id]
        };
        store.Data.Skills.Add(skill);
        store.Data.McpServers.Add(server);
        store.Data.Agents.Add(agent);
        return (store, agent);
    }

    [Fact]
    public void ShareSheet_ForLumi_ShowsEveryPartAndWhatWasKeptPrivate()
    {
        var (store, agent) = StoreWithResearchLumi();
        var sheet = new ShareSheetViewModel(store, static () => []);

        sheet.OpenFor(agent);

        Assert.True(sheet.IsOpen);
        Assert.False(sheet.IsSkill);
        Assert.Equal("research-buddy.lumi.md", sheet.SuggestedFileName);
        Assert.Equal("Research Buddy", sheet.Card!.Name);
        Assert.Equal(["Research Buddy", "Citations", "Brave Search"], sheet.Contents.Select(line => line.Text));
        Assert.True(sheet.HasManyContents);
        Assert.Contains(sheet.PrivacyNotes, note => note.IsSafe && note.Code == "BRAVE_API_KEY");
        Assert.DoesNotContain("super-secret-brave-key", sheet.ShareText);
        Assert.False(sheet.HasTargets);
    }

    [Fact]
    public void ShareSheet_SkillSendsStraightIntoDetectedAgentTools()
    {
        var skillsRoot = Path.Combine(NewTempDir(), "skills");
        var store = NewStore();
        var skill = new Skill { Name = "Word Creator", Description = "Docs.", Content = "Use python-docx." };
        store.Data.Skills.Add(skill);
        var sheet = new ShareSheetViewModel(store, () => [new SkillToolTarget("claude-code", "Claude Code", skillsRoot)]);
        try
        {
            sheet.OpenFor(skill);

            Assert.True(sheet.IsSkill);
            Assert.False(sheet.HasManyContents);
            var target = Assert.Single(sheet.Targets);
            Assert.Equal(SkillTargetState.NotInstalled, target.State);

            sheet.SendToTargetCommand.Execute(target);

            var installed = Path.Combine(skillsRoot, "word-creator", "SKILL.md");
            Assert.Equal(sheet.ShareText, File.ReadAllText(installed));
            Assert.Equal(SkillTargetState.UpToDate, target.State);
            Assert.False(target.CanSend);
            Assert.False(sheet.IsStatusError);
            Assert.Contains("Claude Code", sheet.StatusMessage);
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(skillsRoot)))
                Directory.Delete(Path.GetDirectoryName(skillsRoot)!, recursive: true);
        }
    }

    [Fact]
    public void ShareSheet_SaveToFolderWritesTheCanonicalLayoutWithoutDoubleNesting()
    {
        var root = NewTempDir();
        var store = NewStore();
        var skill = new Skill { Name = "Word Creator", Content = "Body" };
        store.Data.Skills.Add(skill);
        var sheet = new ShareSheetViewModel(store, static () => []);
        try
        {
            sheet.OpenFor(skill);

            sheet.SaveSkillToFolder(root);
            sheet.SaveSkillToFolder(Path.Combine(root, "word-creator"));

            Assert.Equal([Path.Combine(root, "word-creator", "SKILL.md")], Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ImportSheet_ReviewThenConfirm_AddsEverythingAndOffersNextSteps()
    {
        var (source, agent) = StoreWithResearchLumi();
        var text = CapabilityPackWriter.ForLumi(agent, source.Data).Text;
        var store = NewStore();
        var sheet = new ImportSheetViewModel(store);
        ImportOutcome? raised = null;
        sheet.Imported += outcome => raised = outcome;

        sheet.OpenWithText(text, "From a friend");

        Assert.True(sheet.IsOpen);
        Assert.True(sheet.IsReviewing);
        Assert.True(sheet.IsRiskCaution);
        Assert.True(sheet.CanConfirm);
        Assert.Equal(["Research Buddy", "Citations", "Brave Search"], sheet.Items.Select(item => item.Name));
        var server = sheet.Items.Single(item => item.Kind == SharedCapabilityKind.McpServer);
        Assert.Contains(server.Facts, fact => fact.IsCaution && fact.Code == "npx -y @modelcontextprotocol/server-brave-search"
                                              || fact.IsCaution && fact.Code == "npx.cmd -y @modelcontextprotocol/server-brave-search");
        Assert.Contains(server.Facts, fact => fact.Code == "BRAVE_API_KEY");
        Assert.Empty(store.Data.Agents);

        sheet.ConfirmCommand.Execute(null);

        Assert.True(sheet.IsDone);
        Assert.NotNull(raised);
        Assert.Single(store.Data.Agents);
        Assert.Single(store.Data.Skills);
        Assert.False(Assert.Single(store.Data.McpServers).IsEnabled);
        Assert.Equal(
            [NextStepAction.SetUpServer, NextStepAction.ChatWithLumi],
            sheet.NextSteps.Select(step => step.Action));
        Assert.True(File.Exists(store.GetSkillFilePath("Citations")));
    }

    [Fact]
    public void ImportSheet_UnreadableInputStaysOnPickWithAFriendlyError()
    {
        var sheet = new ImportSheetViewModel(NewStore());

        sheet.OpenWithText("just some notes", "Clipboard");

        Assert.True(sheet.IsPicking);
        Assert.True(sheet.HasError);
        Assert.Null(sheet.Card);
    }

    [Fact]
    public void ImportSheet_SomethingAlreadyInTheLibraryCannotBeAddedTwice()
    {
        var store = NewStore();
        var skill = new Skill { Name = "Tone", Content = "Be kind." };
        store.Data.Skills.Add(skill);
        var sheet = new ImportSheetViewModel(store);

        sheet.OpenWithText(CapabilityPackWriter.ForSkill(skill).Text, "Clipboard");

        Assert.True(sheet.IsReviewing);
        Assert.False(sheet.CanConfirm);
        Assert.True(Assert.Single(sheet.Items).IsReused);
        Assert.True(sheet.IsRiskSafe);
    }

    [Fact]
    public void ImportSheet_SkillFolderFileReportsCompanionFilesAndOpensTheSkillAfterwards()
    {
        var folder = Path.Combine(NewTempDir(), "pdf-processing");
        Directory.CreateDirectory(Path.Combine(folder, "scripts"));
        var path = Path.Combine(folder, "SKILL.md");
        File.WriteAllText(path, "---\nname: pdf-processing\ndescription: PDFs.\n---\n\nUse pdfplumber.\n");
        var store = NewStore();
        var sheet = new ImportSheetViewModel(store);
        (SharedCapabilityKind Kind, Guid Id)? opened = null;
        sheet.OpenItemRequested += (kind, id) => opened = (kind, id);
        try
        {
            sheet.OpenWithFile(path);

            Assert.True(sheet.IsReviewing);
            Assert.Equal("pdf-processing/SKILL.md", sheet.Card!.FooterLabel.Split(" · ")[0]);
            Assert.Contains(Assert.Single(sheet.Items).Facts, fact => fact.Code == "scripts/");

            sheet.ConfirmCommand.Execute(null);
            var step = Assert.Single(sheet.NextSteps);
            sheet.RunStepCommand.Execute(step);

            Assert.False(sheet.IsOpen);
            Assert.Equal((SharedCapabilityKind.Skill, Assert.Single(store.Data.Skills).Id), opened);
            Assert.Equal("Pdf Processing", store.Data.Skills[0].Name);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }

    [Fact]
    public void ShareFromTheEditor_FlagsEditsThatAreNotSavedYet()
    {
        var store = NewStore();
        var skill = new Skill { Name = "Tone", Content = "Be kind." };
        store.Data.Skills.Add(skill);
        var skills = new SkillsViewModel(store);
        bool? flagged = null;
        skills.ShareRequested += (_, unsaved) => flagged = unsaved;

        skills.SelectedSkill = skill;
        skills.ShareSkillCommand.Execute(null);
        Assert.False(flagged);

        skills.EditContent = "Be kind, and brief.";
        skills.ShareSkillCommand.Execute(null);
        Assert.True(flagged);

        var sheet = new ShareSheetViewModel(store, static () => []);
        sheet.OpenFor(skill, hasUnsavedEdits: true);
        Assert.True(sheet.HasUnsavedEdits);
        Assert.Contains("Be kind.", sheet.ShareText);
    }

    [Fact]
    public void ImportSheet_SafeBannerMentionsServersThatAreAlreadySetUp()
    {
        var (source, agent) = StoreWithResearchLumi();
        var text = CapabilityPackWriter.ForLumi(agent, source.Data).Text;
        var store = NewStore();
        store.Data.McpServers.Add(new McpServer { Name = "brave", Command = "npx", Args = ["-y", "@modelcontextprotocol/server-brave-search"] });
        var sheet = new ImportSheetViewModel(store);

        sheet.OpenWithText(text, "Clipboard");

        Assert.True(sheet.IsRiskSafe);
        Assert.Equal(Lumi.Localization.Loc.Import_RiskSafeReusedText, sheet.RiskText);
        Assert.True(sheet.Items.Single(item => item.Kind == SharedCapabilityKind.McpServer).IsReused);
    }

    /// <summary>
    /// The sidebar lists clear their selection when their items are rebuilt and push that through the
    /// two-way SelectedItem binding; this mimics it so the view models see what they see in the app.
    /// </summary>
    private static void ClearSelectionOnRebuild(MainViewModel vm)
    {
        vm.SkillsVM.Skills.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                vm.SkillsVM.SelectedSkill = null;
        };
        vm.AgentsVM.Agents.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                vm.AgentsVM.SelectedAgent = null;
        };
        vm.McpServersVM.Servers.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                vm.McpServersVM.SelectedServer = null;
        };
    }

    private static void ImportStandupNotes(MainViewModel vm)
    {
        vm.ImportVM.Open();
        vm.ImportVM.LoadText("---\nname: standup-notes\ndescription: Standups.\n---\n\nSummarise.\n", "Clipboard");
        vm.ImportVM.ConfirmCommand.Execute(null);
    }

    [Fact]
    public void Import_KeepsOpenEditorsAttachedAndTheirEditsInProgress()
    {
        var (store, agent) = StoreWithResearchLumi();
        var skill = store.Data.Skills.Single();
        var server = store.Data.McpServers.Single();
        var vm = new MainViewModel(store, TestCopilot.Shared, new UpdateService());
        ClearSelectionOnRebuild(vm);
        vm.SkillsVM.SelectedSkill = skill;
        vm.AgentsVM.SelectedAgent = agent;
        vm.McpServersVM.SelectedServer = server;
        vm.SkillsVM.EditContent = "Always cite, with links.";
        vm.AgentsVM.EditSystemPrompt = "Research very carefully.";
        vm.AgentsVM.AvailableSkills.Single().IsSelected = false;

        ImportStandupNotes(vm);

        Assert.Same(skill, vm.SkillsVM.SelectedSkill);
        Assert.True(vm.SkillsVM.IsEditing);
        Assert.Equal("Always cite, with links.", vm.SkillsVM.EditContent);
        Assert.Same(agent, vm.AgentsVM.SelectedAgent);
        Assert.Equal("Research very carefully.", vm.AgentsVM.EditSystemPrompt);
        Assert.False(vm.AgentsVM.AvailableSkills.Single(item => item.Name == "Citations").IsSelected);
        Assert.Contains(vm.AgentsVM.AvailableSkills, item => item.Name == "Standup Notes" && !item.IsSelected);
        Assert.Same(server, vm.McpServersVM.SelectedServer);

        vm.SkillsVM.SaveSkillCommand.Execute(null);
        var saved = Assert.Single(store.Data.Skills, item => item.Name == "Citations");
        Assert.Equal("Always cite, with links.", saved.Content);
    }

    [Fact]
    public void Refresh_LeavesAClosedEditorClosedAndShowsWhatIsStored()
    {
        var (store, _) = StoreWithResearchLumi();
        var skill = store.Data.Skills.Single();
        var vm = new MainViewModel(store, TestCopilot.Shared, new UpdateService());
        ClearSelectionOnRebuild(vm);
        vm.SkillsVM.SelectedSkill = skill;
        vm.SkillsVM.CancelEditCommand.Execute(null);

        skill.Content = "Changed elsewhere.";
        vm.SkillsVM.RefreshFromStore();

        Assert.False(vm.SkillsVM.IsEditing);
        Assert.Same(skill, vm.SkillsVM.SelectedSkill);

        vm.SkillsVM.SelectedSkill = null;
        vm.SkillsVM.SelectedSkill = skill;
        Assert.Equal("Changed elsewhere.", vm.SkillsVM.EditContent);
    }

    [Fact]
    public void MainViewModel_RoutesShareAndImportAndRefreshesTheLibraryAfterImport()
    {
        var store = NewStore();
        var skill = new Skill { Name = "Word Creator", Content = "Body" };
        store.Data.Skills.Add(skill);
        var vm = new MainViewModel(store, TestCopilot.Shared, new UpdateService());

        vm.SkillsVM.ShareSkillCommand.Execute(skill);
        Assert.True(vm.ShareVM.IsOpen);
        Assert.Equal("Word Creator", vm.ShareVM.Card!.Name);

        vm.McpServersVM.ImportCommand.Execute(null);
        Assert.True(vm.ImportVM.IsOpen);
        vm.ImportVM.LoadText("---\nname: tone-guide\ndescription: Tone.\n---\n\nBe kind.\n", "Clipboard");
        vm.ImportVM.ConfirmCommand.Execute(null);

        Assert.Contains(vm.SkillsVM.Skills, item => item.Name == "Tone Guide");
        var step = Assert.Single(vm.ImportVM.NextSteps);
        vm.ImportVM.RunStepCommand.Execute(step);
        Assert.Equal(3, vm.SelectedNavIndex);
        Assert.Equal("Tone Guide", vm.SkillsVM.SelectedSkill?.Name);
    }
}
