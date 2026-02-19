using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Lumi.ViewModels;
using StrataTheme.Controls;

namespace Lumi.Views;

public partial class SettingsView : UserControl
{
    private Control?[] _pages = [];

    public SettingsView()
    {
        InitializeComponent();

        // Handle StrataSetting.Reverted events bubbling up from any page
        AddHandler(StrataSetting.RevertedEvent, OnSettingReverted);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _pages =
        [
            this.FindControl<Control>("PageGeneral"),
            this.FindControl<Control>("PageAppearance"),
            this.FindControl<Control>("PageChat"),
            this.FindControl<Control>("PageAI"),
            this.FindControl<Control>("PagePrivacy"),
            this.FindControl<Control>("PageAbout"),
        ];
    }

    private void OnSettingReverted(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not StrataSetting setting) return;
        if (DataContext is not SettingsViewModel vm) return;

        var header = setting.Header;
        if (_revertActions.TryGetValue(header ?? "", out var action))
            action(vm);
    }

    private static readonly Dictionary<string, Action<SettingsViewModel>> _revertActions = new()
    {
        ["Launch at Startup"] = vm => vm.RevertLaunchAtStartupCommand.Execute(null),
        ["Start Minimized"] = vm => vm.RevertStartMinimizedCommand.Execute(null),
        ["Enable Notifications"] = vm => vm.RevertNotificationsEnabledCommand.Execute(null),
        ["Dark Mode"] = vm => vm.RevertIsDarkThemeCommand.Execute(null),
        ["Compact Density"] = vm => vm.RevertIsCompactDensityCommand.Execute(null),
        ["Font Size"] = vm => vm.RevertFontSizeCommand.Execute(null),
        ["Show Animations"] = vm => vm.RevertShowAnimationsCommand.Execute(null),
        ["Send with Enter"] = vm => vm.RevertSendWithEnterCommand.Execute(null),
        ["Show Timestamps"] = vm => vm.RevertShowTimestampsCommand.Execute(null),
        ["Show Tool Calls"] = vm => vm.RevertShowToolCallsCommand.Execute(null),
        ["Show Reasoning"] = vm => vm.RevertShowReasoningCommand.Execute(null),
        ["Auto-Generate Titles"] = vm => vm.RevertAutoGenerateTitlesCommand.Execute(null),
        ["Max Context Messages"] = vm => vm.RevertMaxContextMessagesCommand.Execute(null),
        ["Preferred Model"] = vm => vm.RevertPreferredModelCommand.Execute(null),
        ["Auto-Save Memories"] = vm => vm.RevertEnableMemoryAutoSaveCommand.Execute(null),
        ["Auto-Save Chats"] = vm => vm.RevertAutoSaveChatsCommand.Execute(null),
    };

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is SettingsViewModel vm)
        {
            ShowPage(vm.SelectedPageIndex);

            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SettingsViewModel.SelectedPageIndex))
                    ShowPage(vm.SelectedPageIndex);
            };
        }
    }

    public void ShowPage(int index)
    {
        for (int i = 0; i < _pages.Length; i++)
        {
            if (_pages[i] is not null)
                _pages[i]!.IsVisible = i == index;
        }
    }
}
