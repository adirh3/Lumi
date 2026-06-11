using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class BrowserToggleAfterChatSwitchTests
{
    // Regression test for "the open browser button doesn't work after switching chats".
    // The per-chat browser session used to be disposed (and its runtime state swept) every time
    // the user switched away, so switching back left the toggle hidden with no session to show.
    [Fact]
    public async Task BrowserToggleAndSessionSurviveSwitchingChats()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var dataStore = CreateDataStore();
            var chatA = new Chat { Title = "A" };
            var chatB = new Chat { Title = "B" };
            chatA.Messages.Add(new ChatMessage { Role = "user", Content = "a" });
            chatB.Messages.Add(new ChatMessage { Role = "user", Content = "b" });
            dataStore.Data.Chats.Add(chatA);
            dataStore.Data.Chats.Add(chatB);

            var vm = new ChatViewModel(dataStore, new CopilotService());
            var shows = new List<Guid>();
            vm.BrowserShowRequested += shows.Add;

            await vm.LoadChatAsync(chatA);

            // Simulate the browser tool having created a live per-chat browser session in chat A.
            var services = GetField<ConcurrentDictionary<Guid, BrowserService>>(vm, "_chatBrowserServices");
            services[chatA.Id] = new BrowserService();
            vm.HasUsedBrowser = true;
            Assert.True(vm.ShowBrowserToggle);

            // Switch away to B: the toggle hides for B, but A's browser session must survive.
            await vm.LoadChatAsync(chatB);
            Assert.False(vm.HasUsedBrowser);
            Assert.True(services.ContainsKey(chatA.Id));

            // Switch back to A: the toggle reappears, the session is intact, and the panel
            // auto-restores via BrowserShowRequested.
            shows.Clear();
            await vm.LoadChatAsync(chatA);
            Assert.True(vm.HasUsedBrowser);
            Assert.True(vm.ShowBrowserToggle);
            Assert.NotNull(vm.GetBrowserServiceForChat(chatA.Id));
            Assert.Contains(chatA.Id, shows);

            // Clicking the toggle button now drives the live session for the current chat.
            shows.Clear();
            vm.ToggleBrowser();
            Assert.Contains(chatA.Id, shows);
            Assert.NotNull(vm.GetBrowserServiceForChat(chatA.Id));

            vm.Dispose();
        }, CancellationToken.None);
    }

    private static DataStore CreateDataStore()
        => new(new AppData
        {
            Settings = new UserSettings
            {
                AutoSaveChats = false,
                EnableMemoryAutoSave = false
            }
        });

    private static T GetField<T>(object instance, string name) where T : class
        => (T)(instance.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(instance)
            ?? throw new InvalidOperationException($"Field {name} was not found."));
}
