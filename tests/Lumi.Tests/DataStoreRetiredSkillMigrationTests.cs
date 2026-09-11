using Lumi.Models;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class DataStoreRetiredSkillMigrationTests
{
    [Fact]
    public void RemoveRetiredFeatureManagerSkill_RemovesOnlyBuiltInCopies()
    {
        var customSkill = new Skill
        {
            Name = "Lumi Feature Manager",
            IsBuiltIn = false
        };
        var data = new AppData
        {
            Skills =
            [
                new Skill
                {
                    Name = "Lumi Feature Manager",
                    IsBuiltIn = true
                },
                customSkill,
                new Skill
                {
                    Name = "Document Creator",
                    IsBuiltIn = true
                }
            ]
        };

        Assert.True(DataStore.RemoveRetiredFeatureManagerSkill(data));
        Assert.DoesNotContain(data.Skills, skill =>
            skill.IsBuiltIn
            && skill.Name.Equals("Lumi Feature Manager", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(customSkill, data.Skills);
        Assert.Contains(data.Skills, skill => skill.Name == "Document Creator");
        Assert.False(DataStore.RemoveRetiredFeatureManagerSkill(data));
    }
}
