using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using Xunit;

namespace Lumi.Tests;

// Real end-to-end keyboard tests for the search palette. These mount the actual SearchOverlay control,
// focus its search TextBox, and drive PHYSICAL key presses through the headless input pipeline — exactly
// like a user pressing Down/Up/Escape while the caret is in the search box. This reproduces the real-world
// scenario where Avalonia's TextBox can swallow navigation keys before the overlay's handler ever sees them.
[Collection("Headless UI")]
public sealed class SearchOverlayKeyboardTests
{
    [Fact]
    public async Task ArrowKeys_MoveSelection_WhileSearchInputFocused()
    {
        using var session = HeadlessTestSession.Start();

        bool inputFocused = false;
        int idxStart = -99;
        int idxAfterDown = -99;
        int idxAfterSecondDown = -99;
        int idxAfterUp = -99;

        await session.Dispatch(async () =>
        {
            Loc.Load("en");

            var vm = CreateViewModel();
            vm.IsOpen = true;
            vm.FlatResults = [Result("One"), Result("Two"), Result("Three")];
            vm.SelectedIndex = 0;

            var overlay = new SearchOverlay { DataContext = vm };
            var window = new Window { Width = 960, Height = 680, Content = overlay };
            window.Show();
            await PumpAsync();

            var input = overlay.GetVisualDescendants()
                .OfType<TextBox>()
                .Single(control => control.Name == "SearchInput");
            input.Focus();
            await PumpAsync();
            inputFocused = input.IsFocused;
            idxStart = vm.SelectedIndex;

            PressKey(window, PhysicalKey.ArrowDown);
            await PumpAsync();
            idxAfterDown = vm.SelectedIndex;

            PressKey(window, PhysicalKey.ArrowDown);
            await PumpAsync();
            idxAfterSecondDown = vm.SelectedIndex;

            PressKey(window, PhysicalKey.ArrowUp);
            await PumpAsync();
            idxAfterUp = vm.SelectedIndex;

            window.Close();
        }, CancellationToken.None);

        Assert.True(inputFocused, "The search input should be focused so keys route through it (as they do for a real user).");
        Assert.Equal(0, idxStart);
        Assert.Equal(1, idxAfterDown);
        Assert.Equal(2, idxAfterSecondDown);
        Assert.Equal(1, idxAfterUp);
    }

    [Fact]
    public async Task Escape_ClosesOverlay_WhileSearchInputFocused()
    {
        using var session = HeadlessTestSession.Start();

        bool inputFocused = false;
        bool openBefore = false;
        bool closedAfterEscape = false;

        await session.Dispatch(async () =>
        {
            Loc.Load("en");

            var vm = CreateViewModel();
            vm.IsOpen = true;
            vm.FlatResults = [Result("One"), Result("Two")];
            vm.SelectedIndex = 0;

            var overlay = new SearchOverlay { DataContext = vm };
            var window = new Window { Width = 960, Height = 680, Content = overlay };
            window.Show();
            await PumpAsync();

            var input = overlay.GetVisualDescendants()
                .OfType<TextBox>()
                .Single(control => control.Name == "SearchInput");
            input.Focus();
            await PumpAsync();
            inputFocused = input.IsFocused;
            openBefore = vm.IsOpen;

            PressKey(window, PhysicalKey.Escape);
            await PumpAsync();
            closedAfterEscape = !vm.IsOpen;

            window.Close();
        }, CancellationToken.None);

        Assert.True(inputFocused, "The search input should be focused.");
        Assert.True(openBefore, "The overlay should start open.");
        Assert.True(closedAfterEscape, "Escape should close the overlay even while the search input is focused.");
    }

    [Fact]
    public async Task TypingText_StillReachesSearchInput_AfterKeyInterception()
    {
        using var session = HeadlessTestSession.Start();

        bool inputFocused = false;
        string queryAfterTyping = "<none>";

        await session.Dispatch(async () =>
        {
            Loc.Load("en");

            var vm = CreateViewModel();
            vm.IsOpen = true;

            var overlay = new SearchOverlay { DataContext = vm };
            var window = new Window { Width = 960, Height = 680, Content = overlay };
            window.Show();
            await PumpAsync();

            var input = overlay.GetVisualDescendants()
                .OfType<TextBox>()
                .Single(control => control.Name == "SearchInput");
            input.Focus();
            await PumpAsync();
            inputFocused = input.IsFocused;

            window.KeyTextInput("hello");
            await PumpAsync();
            queryAfterTyping = vm.SearchQuery;

            window.Close();
        }, CancellationToken.None);

        Assert.True(inputFocused, "The search input should be focused.");
        Assert.Equal("hello", queryAfterTyping);
    }

    private static void PressKey(Window window, PhysicalKey key)
    {
        window.KeyPressQwerty(key, RawInputModifiers.None);
        window.KeyReleaseQwerty(key, RawInputModifiers.None);
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Input);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static SearchOverlayViewModel CreateViewModel()
    {
        var service = new GlobalSearchService(
            () => new AppData(),
            _ => new ChatSearchSnapshot { Version = "empty" });
        return new SearchOverlayViewModel(service, () => 0);
    }

    private static SearchResultItem Result(string title)
        => new()
        {
            CategoryKey = GlobalSearchCategory.Chats,
            Category = "Chats",
            Title = title,
            Subtitle = string.Empty,
            Item = new object()
        };
}
