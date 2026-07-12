using System;
using System.IO;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Guards a class of defect that only surfaces in the trimmed, reflection-disabled runtime
/// (the app ships with <c>JsonSerializer.IsReflectionEnabledByDefault = false</c>): a management tool
/// whose <c>AIFunctionFactory.Create</c> omits the source-generated <c>JsonSerializerOptions</c>
/// (<c>AppDataJsonContext.Default.Options</c>) throws at tool-schema build time for parameters like
/// <c>string[]</c> — breaking EVERY chat turn on any surface where the tool is registered.
/// Unit tests run with reflection ENABLED and therefore cannot reproduce the throw at runtime, so we
/// assert the invariant at the source level: every management tool that takes an array/complex
/// parameter must pass the source-gen options, exactly like its siblings.
/// </summary>
public sealed class ManagementToolSerializerOptionsTests
{
    [Fact]
    public void ManageChatsTool_PassesSourceGeneratedSerializerOptions()
    {
        var source = ReadToolsSource();
        var index = source.IndexOf("\"manage_chats\"", StringComparison.Ordinal);
        Assert.True(index >= 0, "manage_chats tool registration not found in ChatViewModel.Tools.cs.");

        // From the tool-name literal onward, its AIFunctionFactory.Create(...) call must pass the
        // source-gen options. The manage_chats tool has a string[] 'skills' parameter which has no
        // JsonTypeInfo under the trimmed runtime unless AppDataJsonContext supplies it.
        var window = source.Substring(index, Math.Min(2000, source.Length - index));
        Assert.Contains("AppDataJsonContext.Default.Options", window);
    }

    private static string ReadToolsSource()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && !string.IsNullOrEmpty(dir); i++)
        {
            var candidate = Path.Combine(dir, "src", "Lumi", "ViewModels", "ChatViewModel.Tools.cs");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            dir = Path.GetDirectoryName(dir);
        }

        throw new FileNotFoundException(
            $"Could not locate src/Lumi/ViewModels/ChatViewModel.Tools.cs walking up from {AppContext.BaseDirectory}.");
    }
}
