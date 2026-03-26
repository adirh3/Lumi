using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Lumi.Localization;
using Lumi.ViewModels;
using Lumi.Views.Controls;
using StrataTheme.Controls;
using System;
using System.ComponentModel;
using System.Linq;

namespace Lumi.Views;

public partial class OnboardingView : UserControl
{
    private ComboBox? _sexCombo;
    private ComboBox? _languageCombo;
    private TextBlock? _learnTitleText;
    private TextBlock? _readyTitleText;
    private TextBlock? _discoverSubtitleText;
    private TextBlock? _agentSubtitleText;
    private ContentControl? _questionCardHost;
    private TextBox? _nameBox;
    private GitHubLoginView? _loginView;
    private OnboardingViewModel? _wiredVm;

    public OnboardingView()
    {
        AvaloniaXamlLoader.Load(this);

        _sexCombo = this.FindControl<ComboBox>("OnboardingSexCombo");
        _languageCombo = this.FindControl<ComboBox>("OnboardingLanguageCombo");
        _learnTitleText = this.FindControl<TextBlock>("LearnTitleText");
        _readyTitleText = this.FindControl<TextBlock>("ReadyTitleText");
        _discoverSubtitleText = this.FindControl<TextBlock>("DiscoverSubtitleText");
        _agentSubtitleText = this.FindControl<TextBlock>("AgentSubtitleText");
        _questionCardHost = this.FindControl<ContentControl>("QuestionCardHost");
        _nameBox = this.FindControl<TextBox>("OnboardingNameBox");
        _loginView = this.FindControl<GitHubLoginView>("OnboardingLoginView");

        if (_nameBox is not null)
            _nameBox.KeyDown += OnNameBoxKeyDown;

        if (_sexCombo is not null)
        {
            _sexCombo.ItemsSource = new[]
            {
                Loc.Onboarding_SexMale,
                Loc.Onboarding_SexFemale,
                Loc.Onboarding_SexPreferNot
            };
            _sexCombo.PlaceholderText = Loc.Onboarding_Sex;
            _sexCombo.SelectedIndex = 0;
        }

        if (_languageCombo is not null)
        {
            _languageCombo.ItemsSource =
                Loc.AvailableLanguages.Select(l => $"{l.DisplayName} ({l.Code})").ToArray();
            _languageCombo.PlaceholderText = Loc.Onboarding_Language;
            _languageCombo.SelectedIndex = 0;
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_wiredVm is not null)
        {
            _wiredVm.PropertyChanged -= OnViewModelPropertyChanged;
            _wiredVm.QuestionAsked -= OnQuestionAsked;
        }

        if (DataContext is OnboardingViewModel vm)
        {
            _wiredVm = vm;
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.QuestionAsked += OnQuestionAsked;
            UpdateFormattedTexts(vm);

            // Wire the shared login component to the OnboardingVM's GitHubLoginVM
            if (_loginView is not null)
                _loginView.DataContext = vm.LoginVM;
        }
        else
        {
            _wiredVm = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not OnboardingViewModel vm) return;

        if (e.PropertyName == nameof(OnboardingViewModel.UserName))
            Dispatcher.UIThread.Post(() => UpdateFormattedTexts(vm));

        // Clear question card when agent dismisses the question
        if (e.PropertyName == nameof(OnboardingViewModel.HasPendingQuestion) && !vm.HasPendingQuestion)
            Dispatcher.UIThread.Post(() => { if (_questionCardHost is not null) _questionCardHost.Content = null; });
    }

    private void UpdateFormattedTexts(OnboardingViewModel vm)
    {
        var name = string.IsNullOrWhiteSpace(vm.UserName) ? "!" : vm.UserName;
        if (_learnTitleText is not null)
            _learnTitleText.Text = string.Format(Loc.Onboarding_LearnTitle, name);
        if (_readyTitleText is not null)
            _readyTitleText.Text = string.Format(Loc.Onboarding_ReadyTitle, name);
        if (_discoverSubtitleText is not null)
            _discoverSubtitleText.Text = string.Format(
                Loc.Onboarding_DiscoverSubtitle ?? "Let me take a quick look around, {0}…", name);
        if (_agentSubtitleText is not null)
            _agentSubtitleText.Text = string.Format(
                Loc.Onboarding_MeetSubtitle ?? "Lumi is analyzing what was found and getting to know you…", name);
    }

    private void OnQuestionAsked(string question, string options)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_questionCardHost is null || _wiredVm is null) return;

            var card = new StrataQuestionCard
            {
                Question = question,
                Options = options,
                AllowFreeText = true,
                AllowMultiSelect = true,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            };

            card.AnswerSubmitted += (_, answer) =>
            {
                _wiredVm.SubmitQuestionAnswer(answer);
            };

            _questionCardHost.Content = card;
        });
    }

    private void OnNameBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _wiredVm is { CanContinueToLearn: true })
        {
            _wiredVm.ContinueToLearnCommand.Execute(null);
            e.Handled = true;
        }
    }
}
