using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Capabilities;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

public sealed class LumiSkillProviderTests
{
    [Fact]
    public async Task EmptyCatalog_DoesNotInventSkills()
    {
        var provider = CreateProvider([]);

        Assert.Empty(await provider.ListAsync());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => provider.ReadAsync("missing"));
    }

    [Fact]
    public async Task Catalog_ContainsMetadataAndReadsMarkdownOnDemand()
    {
        var skill = new Skill
        {
            Name = "Code Helper",
            Description = "Help with \"quoted\" code:\nsecond line.",
            Content = "# Instructions\nBODY_ONLY_MARKER",
        };
        var provider = CreateProvider([skill]);

        var descriptor = Assert.Single(await provider.ListAsync());
        Assert.Equal("code-helper", descriptor.Name);
        Assert.Contains(skill.Name, descriptor.Description);
        Assert.DoesNotContain("BODY_ONLY_MARKER", descriptor.Description);

        var markdown = await provider.ReadAsync("CODE-HELPER");
        Assert.Equal(DataStore.BuildSkillMarkdown(skill, descriptor.Name, descriptor.Description), markdown);
        Assert.Contains("BODY_ONLY_MARKER", markdown);
    }

    [Fact]
    public async Task Read_UsesLatestBodyButAdvertisedHeaderUntilReload()
    {
        var skill = new Skill { Name = "Writer", Description = "Before", Content = "old body" };
        var provider = CreateProvider([skill]);
        var before = Assert.Single(await provider.ListAsync());

        skill.Description = "After";
        skill.Content = "new body";
        var beforeReload = await provider.ReadAsync(before.Name);
        Assert.Contains("new body", beforeReload);
        Assert.Contains(DataStore.EncodeYamlScalar(before.Description), beforeReload);

        var after = Assert.Single(await provider.ListAsync());
        Assert.Contains("After", after.Description);
        Assert.Contains(DataStore.EncodeYamlScalar(after.Description), await provider.ReadAsync(after.Name));
    }

    [Fact]
    public async Task DeletedSkill_FailsInsteadOfReturningStaleContent()
    {
        var skills = new List<Skill> { new() { Name = "Temporary", Content = "Do not return after deletion." } };
        var provider = CreateProvider(skills);
        var descriptor = Assert.Single(await provider.ListAsync());
        skills.Clear();

        var error = await Assert.ThrowsAsync<KeyNotFoundException>(() => provider.ReadAsync(descriptor.Name));
        Assert.Contains("deleted", error.Message);
    }

    [Fact]
    public async Task RuntimeNames_AreBoundedUniqueAndResolveToOriginalSkills()
    {
        var skills = new List<Skill>
        {
            new() { Name = "Same Name" },
            new() { Name = "same-name" },
            new() { Name = new string('A', 100) },
            new() { Name = "\u05db\u05ea\u05d9\u05d1\u05d4" },
        };
        var provider = CreateProvider(skills);
        var descriptors = await provider.ListAsync();

        Assert.Equal(skills.Count, descriptors.Select(descriptor => descriptor.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var descriptor in descriptors)
        {
            Assert.Matches("^[a-z0-9][a-z0-9-]{0,63}$", descriptor.Name);
            Assert.NotNull(LumiSkillProvider.FindSkill(skills, descriptor.Name));
        }
        Assert.Equal(skills.Select(skill => skill.Id).Order(), descriptors
            .Select(descriptor => LumiSkillProvider.FindSkill(skills, descriptor.Name)!.Id).Order());
    }

    [Fact]
    public async Task RuntimeNames_DoNotShadowAnExternalSkillsInvocationName()
    {
        var skill = new Skill { Name = "Code Helper", Content = "Lumi instructions" };
        var snapshot = new CapabilitySnapshot(CapabilityQuery.Empty,
        [
            new CapabilityDescriptor
            {
                Kind = CapabilityKind.Skill,
                Name = "Native coding",
                SkillInvocationName = "code-helper",
                Origin = CapabilityOrigin.Project,
            },
        ], isComplete: true);
        var provider = new LumiSkillProvider(
            _ => Task.FromResult<IReadOnlyList<Skill>>([skill]), snapshot);
        var descriptor = Assert.Single(await provider.ListAsync());

        Assert.Equal($"code-helper-{skill.Id:N}", descriptor.Name);
        Assert.Same(skill, LumiSkillProvider.FindSkill([skill], descriptor.Name, snapshot));
        Assert.Contains(skill.Content, await provider.ReadAsync(descriptor.Name));
    }

    [Fact]
    public async Task ManagementHints_UseTheOwningProvidersAdvertisedAlias()
    {
        var skill = new Skill { Name = "\u05db\u05ea\u05d9\u05d1\u05d4", Content = "Instructions" };
        var provider = CreateProvider([skill]);
        var result = new FeatureChangeResult("Saved", SkillIds: [skill.Id]);
        Assert.Equal("Saved", ChatViewModel.AppendNativeSkillInvocations(result, provider));

        var descriptor = Assert.Single(await provider.ListAsync());
        skill.Name = "Renamed after advertisement";
        var beforeReload = ChatViewModel.AppendNativeSkillInvocations(result, provider);
        Assert.Contains($"skill({{\"skill\":\"{descriptor.Name}\"}})", beforeReload);
        Assert.DoesNotContain("renamed-after-advertisement", beforeReload);

        var reloaded = Assert.Single(await provider.ListAsync());
        var afterReload = ChatViewModel.AppendNativeSkillInvocations(result, provider);
        Assert.Contains($"skill({{\"skill\":\"{reloaded.Name}\"}})", afterReload);
        Assert.DoesNotContain(descriptor.Name, afterReload);
        Assert.Equal("Saved", ChatViewModel.AppendNativeSkillInvocations(result, null));
    }

    [Fact]
    public async Task LongDescription_IsBoundedWithoutChangingStoredData()
    {
        var skill = new Skill { Name = "Verbose", Description = new string('x', 1400), Content = "body" };
        var descriptor = Assert.Single(await CreateProvider([skill]).ListAsync());

        Assert.Equal(1024, descriptor.Description.Length);
        Assert.Equal(1400, skill.Description.Length);
    }

    [Fact]
    public async Task Cancellation_DoesNotReadTheStore()
    {
        var reads = 0;
        var provider = new LumiSkillProvider(_ =>
        {
            reads++;
            return Task.FromResult<IReadOnlyList<Skill>>([]);
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ListAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ReadAsync("missing", cancellation.Token));
        Assert.Equal(0, reads);
    }

    [Fact]
    public void AgentAccess_PreservesLegacyAndExplicitSelections()
    {
        Assert.True(LumiSkillProvider.IsAllowedForAgent(null));
        Assert.True(LumiSkillProvider.IsAllowedForAgent(new LumiAgent()));
        Assert.True(LumiSkillProvider.IsAllowedForAgent(new LumiAgent { ToolNames = ["fetch_skill"] }));
        Assert.True(LumiSkillProvider.IsAllowedForAgent(new LumiAgent { ToolNames = ["skill"] }));
        Assert.False(LumiSkillProvider.IsAllowedForAgent(new LumiAgent { ToolNames = ["lumi_fetch"] }));
        Assert.False(LumiSkillProvider.IsAllowedForAgent(new LumiAgent { HasExplicitToolSelection = true }));
    }

    private static LumiSkillProvider CreateProvider(IReadOnlyList<Skill> skills)
        => new(_ => Task.FromResult(skills));
}
