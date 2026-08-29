using System;
using System.IO;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Capabilities;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class SkillPreviewTests
{
    [Fact]
    public void ResolveSkillMarkdown_RendersRuntimeLocatedSkillBody_WhenChipUsesSlugName()
    {
        // A repo skill invoked via the native Copilot skill tool arrives as a slug
        // ("Publish-New-Version") while the capability is keyed by its front-matter name
        // ("Publish New Version"). The runtime reports the skill's path but not its body, so the
        // preview reads that exact file — no discovery, no directory enumeration.
        var root = Path.Combine(Path.GetTempPath(), "lumi-skill-slug-" + Guid.NewGuid().ToString("N"));
        var skillDir = Path.Combine(root, ".github", "skills", "Publish New Version");
        Directory.CreateDirectory(skillDir);
        var skillPath = Path.Combine(skillDir, "SKILL.md");
        File.WriteAllText(
            skillPath,
            "---\nname: Publish New Version\ndescription: Bumps the version.\n---\n\n# Publish New Version\n\nStep-by-step release body.");

        try
        {
            var snapshot = new CapabilitySnapshot(
                CapabilityQuery.Empty,
                [
                    new CapabilityDescriptor
                    {
                        Kind = CapabilityKind.Skill,
                        Name = "Publish New Version",
                        Origin = CapabilityOrigin.Project,
                        Description = "Bumps the version.",
                        SourcePath = skillPath,
                    }
                ],
                isComplete: true);

            // Slug resolution — the exact lookup the preview performs at click time.
            var discovered = snapshot.FindSkill("Publish-New-Version");
            Assert.NotNull(discovered);
            Assert.Equal("Publish New Version", discovered!.Name);

            Assert.True(CapabilityContent.TryReadBody(discovered, out var body));
            Assert.Contains("Step-by-step release body.", body);
            Assert.DoesNotContain("description: Bumps the version.", body);
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
        var viewModel = new ChatViewModel(new DataStore(appData), TestCopilot.Shared);

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
