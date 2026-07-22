using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class DataStorePlatformSkillMigrationTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Migration_RewritesOnlyPlatformDefaultsAndPreservesSkillIdentity(
        bool sourceIsWindows,
        bool targetIsWindows)
    {
        var source = DataStore.CreatePlatformSkillText(sourceIsWindows);
        var desired = DataStore.CreatePlatformSkillText(targetIsWindows);
        var documentId = Guid.NewGuid();
        var websiteId = Guid.NewGuid();
        var document = new Skill
        {
            Id = documentId,
            Name = "Document Creator",
            Description = "Custom description remains",
            IsBuiltIn = true,
            Content = $"""
                Custom document prefix.
                {source.DocumentWord}
                {source.DocumentExcel}
                {source.DocumentPowerPoint}
                Custom document suffix.
                """
        };
        var website = new Skill
        {
            Id = websiteId,
            Name = "Website Creator",
            Description = source.WebsiteDescription,
            IsBuiltIn = true,
            Content = $"""
                Custom website prefix.
                {source.WebsitePresentation}
                {source.WebsiteSaveStep}
                {source.WebsiteOpenStep}
                {source.WebsiteOpenRule}
                Custom website suffix.
                """
        };
        var data = new AppData { Skills = [document, website] };

        Assert.True(DataStore.MigratePlatformSpecificBuiltInSkills(data, targetIsWindows));

        Assert.Equal(documentId, document.Id);
        Assert.Equal("Custom description remains", document.Description);
        Assert.Contains("Custom document prefix.", document.Content, StringComparison.Ordinal);
        Assert.Contains("Custom document suffix.", document.Content, StringComparison.Ordinal);
        Assert.Contains(desired.DocumentWord, document.Content, StringComparison.Ordinal);
        Assert.Contains(desired.DocumentExcel, document.Content, StringComparison.Ordinal);
        Assert.Contains(desired.DocumentPowerPoint, document.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(source.DocumentWord, document.Content, StringComparison.Ordinal);

        Assert.Equal(websiteId, website.Id);
        Assert.Equal(desired.WebsiteDescription, website.Description);
        Assert.Contains("Custom website prefix.", website.Content, StringComparison.Ordinal);
        Assert.Contains("Custom website suffix.", website.Content, StringComparison.Ordinal);
        Assert.Contains(desired.WebsitePresentation, website.Content, StringComparison.Ordinal);
        Assert.Contains(desired.WebsiteSaveStep, website.Content, StringComparison.Ordinal);
        Assert.Contains(desired.WebsiteOpenStep, website.Content, StringComparison.Ordinal);
        Assert.Contains(desired.WebsiteOpenRule, website.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(source.WebsitePresentation, website.Content, StringComparison.Ordinal);

        Assert.False(DataStore.MigratePlatformSpecificBuiltInSkills(data, targetIsWindows));
    }

    [Fact]
    public void Migration_DoesNotRewriteUserCreatedSkillsWithBuiltInNames()
    {
        var source = DataStore.CreatePlatformSkillText(isWindows: true);
        var skill = new Skill
        {
            Name = "Website Creator",
            Description = source.WebsiteDescription,
            IsBuiltIn = false,
            Content = source.WebsitePresentation
        };
        var data = new AppData { Skills = [skill] };

        Assert.False(DataStore.MigratePlatformSpecificBuiltInSkills(data, isWindows: false));
        Assert.Equal(source.WebsiteDescription, skill.Description);
        Assert.Equal(source.WebsitePresentation, skill.Content);
    }
}
