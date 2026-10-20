// The surfaces are the design preview's, which only Debug builds carry.
#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Mobile.Layout;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Views;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Mobile.Tests;

/// <summary>
/// The phone's type has to read like the other apps on the phone: no surface may draw words below the
/// caption rung, and the whole scale follows the phone's own font size, as every native app's text does.
/// </summary>
[Collection("Headless mobile UI")]
public sealed class MobileTypeScaleTests
{
    /// <summary>
    /// Every surface of the design preview, with every disclosure open: the chat list, the conversation
    /// with its finished work unfolded, each sheet, and every place. Words below the caption rung read like
    /// a desktop app shrunk onto a phone; only a letter in a tile may sit below it, and not far.
    /// </summary>
    [Fact]
    public async Task EveryPhoneSurface_ReadsAtLeastAtTheCaptionRung()
    {
        await RunPreviewAsync(async (shell, window) =>
        {
            // The floor is the caption rung itself, which may not sink back toward the desktop's: 13 in
            // Inter, whose lowercase stands as tall as Roboto's, is a little above Android's 12sp label.
            var floor = (double)window.FindResource("Font.SizeCaption")!;
            Assert.True(floor >= 13, $"The caption rung is {floor}px.");
            // The conversation stays beneath each sheet, so a word is reported once, where it first shows.
            var offenders = new Dictionary<string, string>();

            void Audit(string surface)
            {
                foreach (var text in Undersized(window, floor))
                    offenders.TryAdd(text, surface);
            }

            Audit("chat list");

            shell.ShowPageCommand.Execute("Chat");
            foreach (var work in shell.Chat.Turns.SelectMany(turn => turn.Items).OfType<WorkSummaryItemViewModel>())
                work.IsExpanded = true;
            await SettleAsync(window);
            Audit("conversation");

            await OpenAsync(window, () => shell.Chat.IsAddSheetOpen = true, () => shell.Chat.IsAddSheetOpen = false, "add sheet", Audit);
            await OpenAsync(window, () => shell.Chat.OpenContextPickerCommand.Execute("Skill"), () => shell.Chat.IsContextPickerOpen = false, "skill picker", Audit);
            await OpenAsync(window, () => shell.Chat.IsModelSheetOpen = true, () => shell.Chat.IsModelSheetOpen = false, "model sheet", Audit);
            await OpenAsync(window, () => shell.Chat.IsRunSettingsSheetOpen = true, () => shell.Chat.IsRunSettingsSheetOpen = false, "chat details", Audit);

            var activity = shell.Chat.Turns.SelectMany(turn => turn.Items)
                .SelectMany(item => item is WorkSummaryItemViewModel work ? work.Items : [item])
                .OfType<ActivitySummaryItemViewModel>()
                .First();
            await OpenAsync(window, () =>
            {
                shell.Chat.SelectedActivity = activity;
                shell.Chat.IsActivitySheetOpen = true;
            }, () => shell.Chat.IsActivitySheetOpen = false, "activity", Audit);

            var answer = shell.Chat.Turns.SelectMany(turn => turn.Items)
                .OfType<AssistantItemViewModel>()
                .First(item => item.HasSources);
            await OpenAsync(window, () =>
            {
                shell.Chat.SelectedSourceAnswer = answer;
                shell.Chat.IsSourcesSheetOpen = true;
            }, () => shell.Chat.IsSourcesSheetOpen = false, "sources", Audit);

            foreach (var place in new[] { MobilePlace.Jobs, MobilePlace.Customize, MobilePlace.Settings })
            {
                shell.SelectPlace(place);
                await SettleAsync(window);
                Audit(place.ToString());
            }

            Assert.True(offenders.Count == 0,
                string.Join(Environment.NewLine, offenders.Select(offender => $"{offender.Value}: {offender.Key}")));
        });
    }

    /// <summary>
    /// The phone's font size reaches every rung: the chat's title, its answer and its captions grow with
    /// it (headings by the platform's own smaller factor), a new setting replaces the last instead of
    /// compounding it, and the default font size restores the designed scale.
    /// </summary>
    [Fact]
    public async Task TheTypeScaleFollowsThePhonesFontSize()
    {
        await RunPreviewAsync(async (shell, window) =>
        {
            var application = Application.Current!;
            shell.ShowPageCommand.Execute("Chat");
            await SettleAsync(window);

            var title = Named<TextBlock>(window, "HeaderTitleText").Single();
            var footer = Named<TextBlock>(window, "AnswerFooter").First();
            TextBlock AnswerText() => window.GetVisualDescendants().OfType<StrataMarkdown>().First(markdown => markdown.IsEffectivelyVisible)
                .GetVisualDescendants().OfType<TextBlock>().First(text => text.IsEffectivelyVisible && Words(text).Length > 0);
            // Your messages keep the desktop's spacing, 1.33 of their size, at every font size.
            var message = Named<SelectableTextBlock>(window, "UserMessageText").First();
            Assert.Equal(16, title.FontSize);
            Assert.Equal(13, footer.FontSize);
            Assert.Equal(16, AnswerText().FontSize);
            Assert.Equal(16, message.FontSize);
            Assert.Equal(21.28, message.LineHeight);

            // As Android 14 converts at 130%: text by the whole factor, headings by less.
            MobileTypeScale.Apply(application, size => size <= 20 ? size * 1.3 : size * 1.15);
            await SettleAsync(window);
            Assert.Equal(20.8, title.FontSize);
            Assert.Equal(16.9, footer.FontSize);
            Assert.Equal(20.8, AnswerText().FontSize);
            Assert.Equal(25.3, application.FindResource("Font.SizeTitle"));
            Assert.Equal(20.8, message.FontSize);
            Assert.Equal(27.66, message.LineHeight);
            Assert.Equal(18.85, application.FindResource("Font.SizeMeta"));
            Assert.Equal(26.39, application.FindResource("Font.LineHeightMeta"));

            MobileTypeScale.Apply(application, size => size * 1.1);
            await SettleAsync(window);
            Assert.Equal(17.6, title.FontSize);

            MobileTypeScale.Apply(application, size => size);
            await SettleAsync(window);
            Assert.Equal(16, title.FontSize);
            Assert.Equal(16, AnswerText().FontSize);
            Assert.Equal(21.28, message.LineHeight);
            Assert.DoesNotContain(MobileTypeScale.Keys, key => application.Resources.ContainsKey(key));
            Assert.DoesNotContain(MobileTypeScale.LineHeightKeys, pair => application.Resources.ContainsKey(pair.LineHeight));
        });
    }

    /// <summary>
    /// A question Lumi is still waiting on is answered with a thumb: each option, the reply field and its
    /// send button are 48 dp targets, and the options read at the body rung.
    /// </summary>
    [Fact]
    public async Task APendingQuestionIsAnsweredWithAThumb()
    {
        await RunPreviewAsync(async (shell, window) =>
        {
            shell.ShowPageCommand.Execute("Chat");
            await SettleAsync(window);

            var card = window.GetVisualDescendants().OfType<StrataQuestionCard>()
                .Single(question => question.IsEffectivelyVisible && !question.IsAnswered);
            Assert.True(card.TouchMode);
            var options = card.GetVisualDescendants().OfType<Button>()
                .Where(button => button.Classes.Contains("question-option")).ToList();
            Assert.Equal(2, options.Count);
            Assert.All(options, option =>
            {
                Assert.True(option.Bounds.Height >= 48, $"An option is {option.Bounds.Height:0.#} dp tall.");
                Assert.Equal(16, option.FontSize);
            });
            var reply = card.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "PART_FreeTextBox");
            var send = card.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "PART_FreeTextSubmit");
            Assert.True(reply.Bounds.Height >= 48, $"The reply field is {reply.Bounds.Height:0.#} dp tall.");
            Assert.True(send.Bounds is { Width: >= 48, Height: >= 48 }, $"Send is {send.Bounds.Width:0.#} × {send.Bounds.Height:0.#} dp.");
        });
    }

    private static IEnumerable<T> Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Where(control => control.Name == name && control.IsEffectivelyVisible);

    private static async Task OpenAsync(Window window, Action open, Action close, string surface, Action<string> audit)
    {
        open();
        await SettleAsync(window);
        audit(surface);
        close();
        await SettleAsync(window);
    }

    private static IEnumerable<string> Undersized(Window window, double floor) =>
        window.GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.IsEffectivelyVisible
                           && text.Bounds is { Width: > 0, Height: > 0 }
                           && EffectiveOpacity(text) > 0.05
                           && Words(text) is { Length: > 0 }
                           && text.FontSize < floor)
            .Select(text => $"'{Trim(Words(text))}' {text.FontSize}px in {Owner(text)}");

    private static string Words(TextBlock text) =>
        (text.Text ?? text.Inlines?.Text ?? "").Trim();

    private static string Trim(string text) => text.Length > 40 ? text[..40] + "…" : text;

    private static double EffectiveOpacity(Visual visual)
    {
        var opacity = 1d;
        for (Visual? current = visual; current is not null; current = current.GetVisualParent())
            opacity *= current.Opacity;
        return opacity;
    }

    private static string Owner(TextBlock text)
    {
        var named = text.GetVisualAncestors().OfType<StyledElement>()
            .FirstOrDefault(element => !string.IsNullOrEmpty(element.Name) || element.Classes.Count > 0);
        var self = string.IsNullOrEmpty(text.Name) ? string.Join('.', text.Classes) : "#" + text.Name;
        var parent = named is null ? "" : named.GetType().Name
            + (string.IsNullOrEmpty(named.Name) ? "" : "#" + named.Name)
            + (named.Classes.Count > 0 ? "." + string.Join('.', named.Classes) : "");
        return $"{text.GetType().Name}{(self.Length > 0 ? "[" + self + "]" : "")} < {parent}";
    }

    private static async Task RunPreviewAsync(Func<MobileShellViewModel, Window, Task> body)
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(async () =>
        {
            await using var shell = new MobileShellViewModel(store: session.NewStore(), post: action =>
            {
                if (Dispatcher.UIThread.CheckAccess())
                    action();
                else
                    Dispatcher.UIThread.Post(action);
            });
            // Tall enough that the whole conversation is realised, at the Fold's cover-screen width.
            var window = new Window { Width = 412, Height = 2600, Content = new MobileShellView { DataContext = shell } };
            try
            {
                window.Show();
                DesignPreview.Apply(shell);
                await SettleAsync(window);
                await body(shell, window);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
        failure?.Throw();
    }

    private static async Task SettleAsync(Window window)
    {
        for (var pass = 0; pass < 12; pass++)
        {
            window.UpdateLayout();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
#endif
