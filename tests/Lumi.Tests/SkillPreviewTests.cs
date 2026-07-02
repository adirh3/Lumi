using System;
using System.IO;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class SkillPreviewTests
{
    [Fact]
    public void FindSkill_ResolvesSlugifiedToolName_ToFrontMatterName()
    {
        // The native Copilot skill tool reports a slug ("Publish-New-Version") while the catalog is
        // keyed by the front-matter name ("Publish New Version"). Both must resolve to one skill.
        var snapshot = new ProjectContextCatalogSnapshot(
            new[] { new CopilotSkillDefinition("Publish New Version", "desc", "# Body", "/skills/pnv.md") },
            Array.Empty<CopilotAgentDefinition>(),
            Array.Empty<ProjectContextMcpServerDefinition>());

        Assert.Equal("Publish New Version", snapshot.FindSkill("Publish-New-Version")?.Name);
        Assert.Equal("Publish New Version", snapshot.FindSkill("publish new version")?.Name);
        Assert.Equal("Publish New Version", snapshot.FindSkill("Publish New Version")?.Name);
        Assert.Null(snapshot.FindSkill("Totally Different Skill"));
    }

    [Fact]
    public void OpenSkillPreview_RendersRepoSkillMarkdown_WhenChipUsesSlugName()
    {
        // Reproduces the reported bug: a repo skill (.github/skills/<name>/SKILL.md) invoked via the
        // native Copilot skill tool arrives as a slug ("Publish-New-Version"), while the catalog is
        // keyed by the front-matter name ("Publish New Version"). Clicking its chip must render the body.
        var root = Path.Combine(Path.GetTempPath(), "lumi-skill-slug-" + Guid.NewGuid().ToString("N"));
        var skillDir = Path.Combine(root, ".github", "skills", "Publish New Version");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            "---\nname: Publish New Version\ndescription: Bumps the version.\n---\n\n# Publish New Version\n\nStep-by-step release body.");

        try
        {
            var project = new Project { Name = "Repo", WorkingDirectory = root };

            // Disk discovery + slug resolution — the exact lookup GetProjectContextCatalog().FindSkill
            // performs at click time (the native tool reports the slug, the catalog is keyed by name).
            var discovered = ProjectContextCatalog.Discover(root, project).FindSkill("Publish-New-Version");
            Assert.NotNull(discovered);
            Assert.Equal("Publish New Version", discovered!.Name);
            Assert.Contains("Step-by-step release body.", discovered.Content);

            // Full ViewModel click path: a persisted chip stored with the slug name renders the body.
            var appData = new AppData();
            appData.Projects.Add(project);
            var viewModel = new ChatViewModel(new DataStore(appData), new CopilotService())
            {
                ActiveProjectFilterId = project.Id
            };

            viewModel.OpenSkillPreview(new SkillReference { Name = "Publish-New-Version" });

            Assert.Equal("Publish-New-Version", viewModel.SkillPreviewTitle);
            Assert.Contains("Step-by-step release body.", viewModel.SkillPreviewContent);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OpenSkillPreview_RendersSdkProvidedContent_ForBuiltinSkill_WithoutFilesystem()
    {
        // Reproduces the real remaining bug: a standard/builtin Copilot skill has no reachable
        // SKILL.md on this machine (builtin skills live inside the CLI package, plugin/remote skills
        // elsewhere), so filesystem re-discovery finds nothing and the preview shows empty. The SDK's
        // skill.invoked event supplies the full Content, which the chip now persists. Clicking it must
        // render that content directly — with no project working directory and no skill file on disk.
        var appData = new AppData();
        var viewModel = new ChatViewModel(new DataStore(appData), new CopilotService());

        var chip = new SkillReference
        {
            Name = "customize-cloud-agent",
            Description = "Configures the Copilot cloud agent.",
            Content = "# Customize Cloud Agent\n\nStep-by-step builtin skill body."
        };

        viewModel.OpenSkillPreview(chip);

        Assert.Equal("customize-cloud-agent", viewModel.SkillPreviewTitle);
        Assert.Contains("Step-by-step builtin skill body.", viewModel.SkillPreviewContent);
    }

    [Fact]
    public void SkillReferenceContent_SurvivesJsonRoundTrip()
    {
        // The save path projects ActiveSkills field-by-field (DataStore) and the load path uses the
        // source-generated serializer. Guard that Content persists across a round trip so a chip
        // created on one machine still renders on another after the chat JSON is synced.
        var message = new ChatMessage { Role = "assistant", Content = "done" };
        message.ActiveSkills.Add(new SkillReference
        {
            Name = "customize-cloud-agent",
            Content = "# Customize Cloud Agent\n\nStep-by-step builtin skill body."
        });

        var json = System.Text.Json.JsonSerializer.Serialize(
            new System.Collections.Generic.List<ChatMessage> { message },
            AppDataJsonContext.Default.ListChatMessage);
        var restored = System.Text.Json.JsonSerializer.Deserialize(
            json, AppDataJsonContext.Default.ListChatMessage);

        Assert.NotNull(restored);
        Assert.Equal(
            "# Customize Cloud Agent\n\nStep-by-step builtin skill body.",
            restored![0].ActiveSkills[0].Content);
    }
}
