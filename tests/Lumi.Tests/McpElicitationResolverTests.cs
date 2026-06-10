using System.Collections.Generic;
using System.Text.Json;
using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class McpElicitationResolverTests
{
    [Fact]
    public void BuildPrompt_NoProperties_IsYesNoConfirmation()
    {
        var plan = McpElicitationResolver.BuildPrompt("Proceed with deploy?", null);

        Assert.Equal("Proceed with deploy?", plan.Question);
        Assert.Equal(["Yes", "No"], plan.Options);
        Assert.False(plan.AllowFreeText);
    }

    [Fact]
    public void BuildPrompt_BlankMessage_FallsBackToGenericText()
    {
        var plan = McpElicitationResolver.BuildPrompt("   ", null);

        Assert.Equal("An MCP server is requesting input.", plan.Question);
    }

    [Fact]
    public void BuildPrompt_SingleEnumField_BecomesChoiceList()
    {
        var props = new Dictionary<string, object?>
        {
            ["environment"] = Json("""{ "type": "string", "enum": ["dev", "staging", "prod"] }""")
        };

        var plan = McpElicitationResolver.BuildPrompt("Pick a target", props);

        Assert.Equal(["dev", "staging", "prod"], plan.Options);
        Assert.False(plan.AllowFreeText);
        Assert.Contains("environment", plan.Question);
    }

    [Fact]
    public void BuildPrompt_SingleFreeTextField_AllowsFreeText()
    {
        var props = new Dictionary<string, object?>
        {
            ["ticket"] = Json("""{ "type": "string" }""")
        };

        var plan = McpElicitationResolver.BuildPrompt("Enter a ticket id", props);

        Assert.Empty(plan.Options);
        Assert.True(plan.AllowFreeText);
        Assert.Contains("ticket (string)", plan.Question);
    }

    [Fact]
    public void BuildPrompt_MultipleFields_RequestsKeyValueLines()
    {
        var props = new Dictionary<string, object?>
        {
            ["host"] = Json("""{ "type": "string" }"""),
            ["port"] = Json("""{ "type": "integer" }""")
        };

        var plan = McpElicitationResolver.BuildPrompt("Configure", props);

        Assert.True(plan.AllowFreeText);
        Assert.Empty(plan.Options);
        Assert.Contains("host", plan.Question);
        Assert.Contains("port", plan.Question);
        Assert.Contains("field: value", plan.Question);
    }

    [Fact]
    public void Resolve_Confirmation_YesAccepts()
    {
        var (accept, content) = McpElicitationResolver.Resolve(null, "Yes");

        Assert.True(accept);
        Assert.Empty(content);
    }

    [Fact]
    public void Resolve_Confirmation_NoDeclines()
    {
        var (accept, content) = McpElicitationResolver.Resolve(null, "No");

        Assert.False(accept);
        Assert.Empty(content);
    }

    [Fact]
    public void Resolve_SingleField_CoercesToSchemaType()
    {
        var props = new Dictionary<string, object?>
        {
            ["port"] = Json("""{ "type": "integer" }""")
        };

        var (accept, content) = McpElicitationResolver.Resolve(props, "8080");

        Assert.True(accept);
        Assert.Equal(8080L, content["port"]);
    }

    [Fact]
    public void Resolve_EmptyAnswerWithFields_Declines()
    {
        var props = new Dictionary<string, object?>
        {
            ["name"] = Json("""{ "type": "string" }""")
        };

        var (accept, content) = McpElicitationResolver.Resolve(props, "   ");

        Assert.False(accept);
        Assert.Empty(content);
    }

    [Fact]
    public void Resolve_MultipleFields_ParsesKeyValueLines()
    {
        var props = new Dictionary<string, object?>
        {
            ["host"] = Json("""{ "type": "string" }"""),
            ["port"] = Json("""{ "type": "integer" }"""),
            ["secure"] = Json("""{ "type": "boolean" }""")
        };

        var (accept, content) = McpElicitationResolver.Resolve(
            props, "host: example.com\nport: 443\nsecure: yes");

        Assert.True(accept);
        Assert.Equal("example.com", content["host"]);
        Assert.Equal(443L, content["port"]);
        Assert.Equal(true, content["secure"]);
    }

    [Theory]
    [InlineData("true", "boolean", true)]
    [InlineData("yes", "boolean", true)]
    [InlineData("0", "boolean", false)]
    [InlineData("42", "integer", 42L)]
    [InlineData("3.5", "number", 3.5)]
    [InlineData("hello", "string", "hello")]
    public void Coerce_ConvertsByJsonSchemaType(string value, string type, object expected)
    {
        Assert.Equal(expected, McpElicitationResolver.Coerce(value, type));
    }

    [Fact]
    public void Coerce_NonNumericInteger_FallsBackToRawString()
    {
        Assert.Equal("not-a-number", McpElicitationResolver.Coerce("not-a-number", "integer"));
    }

    private static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }
}
