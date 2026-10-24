using System.Reflection;
#if WINDOWS
using System.Runtime.InteropServices;
#endif
using Avalonia.Controls;
using Lumi.Localization;
using Lumi.Views;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class DesktopVisualSurfaceLocalizationTests
{
#if WINDOWS
    [Fact]
    public void NativePreviewErrorsKeepTheirHResultsWithLocalizedMessages()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Lumi-preview-localization-{Guid.NewGuid():N}.tmp");
        try
        {
            Loc.Load("he");
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            var stream = new ReadOnlyPreviewStream(file);

            static void Check(Action operation, string message, int errorCode)
            {
                var error = Assert.Throws<COMException>(operation);
                Assert.Equal(message, error.Message);
                Assert.Equal(errorCode, error.ErrorCode);
            }

            Check(() => stream.Seek(0, 3, 0), Loc.Preview_InvalidSeekOrigin, unchecked((int)0x80030001));
            Check(() => stream.Seek(-1, 0, 0), Loc.Preview_SeekBeforeStart, unchecked((int)0x80030001));
            Check(() => stream.Write([], 0, 0), Loc.Preview_StreamReadOnly, unchecked((int)0x80030005));
            Check(stream.Revert, Loc.Preview_StreamOperationUnsupported, unchecked((int)0x80030001));
        }
        finally
        {
            Loc.Load("en");
            File.Delete(path);
        }
    }
#endif

    [Fact]
    public async Task SharedControlStylesUseTheSavedLanguageAfterEarlyResourcesLoad()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var message = new StrataChatMessage();
            var question = new StrataQuestionCard();
            var markdown = new StrataMarkdown();
            var window = new Window
            {
                Content = new StackPanel { Children = { message, question, markdown } }
            };
            try
            {
                Loc.Load("he");
                window.Styles.Add(Lumi.App.CreateSharedControlTextStyles());
                window.Show();

                Assert.Equal("העתק הודעה", message.CopyLabel);
                Assert.Equal("העתק את הטקסט שנבחר", message.CopySelectionLabel);
                Assert.Equal("העתק את כל תגובת העוזר", message.CopyTurnLabel);
                Assert.Equal("הקלד את התשובה שלך...", question.FreeTextPlaceholder);
                Assert.Equal("he", markdown.InteractionLanguage);
            }
            finally
            {
                window.Close();
                Loc.Load("en");
            }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("he")]
    public void CopiedContextUsesLocalizedHeadingsAndCanRecognizeThem(string language)
    {
        try
        {
            Loc.Load(language);
            var payloadType = typeof(ChatView).GetNestedType("ClipboardCopyPayload", BindingFlags.NonPublic)!;
            var payload = Activator.CreateInstance(payloadType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null,
                args: ["Answer", new List<string> { "report.csv" }, new List<string> { "Planning" },
                    new List<string> { "https://example.test" }], culture: null)!;
            var format = typeof(ChatView).GetMethod("FormatClipboardText", BindingFlags.NonPublic | BindingFlags.Static)!;
            var text = Assert.IsType<string>(format.Invoke(null, [payload]));

            foreach (var (heading, section) in new[]
            {
                (Loc.Chat_ClipboardFilesHeading, "Files"),
                (Loc.Chat_ClipboardSkillsHeading, "UsedSkills"),
                (Loc.Chat_ClipboardSourcesHeading, "Sources"),
            })
            {
                Assert.Contains(heading + Environment.NewLine, text);
                Assert.Equal(section, ParseSection(heading));
            }

            Assert.StartsWith("Answer", text);
            Assert.Contains("- report.csv", text);
            Assert.Contains("- Planning", text);
            Assert.Contains("- https://example.test", text);
        }
        finally
        {
            Loc.Load("en");
        }
    }

    [Theory]
    [InlineData("Files:", "Files")]
    [InlineData("USED SKILLS:", "UsedSkills")]
    [InlineData("Sources:", "Sources")]
    public void HebrewPasteStillAcceptsExistingEnglishClipboardHeadings(string heading, string section)
    {
        try
        {
            Loc.Load("he");
            Assert.Equal(section, ParseSection(heading));
            Assert.Equal("None", ParseSection("Ordinary text about files and sources."));
        }
        finally
        {
            Loc.Load("en");
        }
    }

    private static string ParseSection(string heading)
    {
        var parse = typeof(ChatView).GetMethod("IsClipboardSection", BindingFlags.NonPublic | BindingFlags.Static)!;
        object?[] arguments = [heading, null];
        var recognized = Assert.IsType<bool>(parse.Invoke(null, arguments));
        var section = arguments[1]!.ToString()!;
        Assert.Equal(section != "None", recognized);
        return section;
    }
}
