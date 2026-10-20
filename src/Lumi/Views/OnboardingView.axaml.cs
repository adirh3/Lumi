using System;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Lumi.ViewModels;
using Lumi.Views.Management;
using StrataTheme.Animation;
using StrataTheme.Controls;

namespace Lumi.Views;

public partial class OnboardingView : UserControl
{
    private static readonly TimeSpan DemoRise = TimeSpan.FromMilliseconds(340);

    /// <summary>The conversation line a question card shows; answering the card records the answer on it.</summary>
    public static readonly AttachedProperty<OnboardingChatItem?> QuestionItemProperty =
        AvaloniaProperty.RegisterAttached<OnboardingView, StrataQuestionCard, OnboardingChatItem?>("QuestionItem");

    private readonly Grid? _root;
    private readonly StrataPresence? _presence;
    private readonly TextBox? _nameBox;
    private readonly ScrollViewer? _conversationScroller;
    private readonly Control? _demoHeader;
    private readonly Control? _demoRequest;
    private readonly ItemsControl? _demoSteps;
    private readonly Control? _demoReply;
    private OnboardingViewModel? _wiredVm;
    private bool _isActive = true;

    static OnboardingView()
    {
        QuestionItemProperty.Changed.AddClassHandler<StrataQuestionCard>((card, e) =>
        {
            card.AnswerSubmitted -= OnQuestionAnswered;
            if (e.NewValue is OnboardingChatItem)
                card.AnswerSubmitted += OnQuestionAnswered;
        });
    }

    public OnboardingView()
    {
        AvaloniaXamlLoader.Load(this);

        _root = this.FindControl<Grid>("OnboardingRoot");
        _presence = this.FindControl<StrataPresence>("OnboardingPresence");
        _nameBox = this.FindControl<TextBox>("OnboardingNameBox");
        _conversationScroller = this.FindControl<ScrollViewer>("ConversationScroller");
        _demoHeader = this.FindControl<Control>("DemoHeader");
        _demoRequest = this.FindControl<Control>("DemoRequest");
        _demoSteps = this.FindControl<ItemsControl>("DemoSteps");
        _demoReply = this.FindControl<Control>("DemoReply");

        if (_nameBox is not null)
            _nameBox.KeyDown += OnNameBoxKeyDown;
        if (this.FindControl<ItemsControl>("OnboardingConversation") is { } conversation)
            conversation.ContainerPrepared += OnConversationContainerPrepared;
    }

    public static OnboardingChatItem? GetQuestionItem(StrataQuestionCard card) => card.GetValue(QuestionItemProperty);

    public static void SetQuestionItem(StrataQuestionCard card, OnboardingChatItem? value) => card.SetValue(QuestionItemProperty, value);

    private static void OnQuestionAnswered(object? sender, string answer)
    {
        if (sender is StrataQuestionCard card && GetQuestionItem(card) is { } item)
            item.Submit(answer);
    }

    /// <summary>
    /// The shell hides onboarding once it is done. The presence then leaves the visual tree, which stops
    /// its render-thread motion instead of animating behind a hidden panel.
    /// </summary>
    public void SetActive(bool active)
    {
        if (_isActive == active || _root is null || _presence is null)
            return;

        _isActive = active;
        if (!active)
            _root.Children.Remove(_presence);
        else if (!_root.Children.Contains(_presence))
            _root.Children.Insert(0, _presence);
    }

    private bool MotionEnabled => ManagementMotion.IsEnabledFor(this);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        FocusNameSoon();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_wiredVm is not null)
        {
            _wiredVm.PropertyChanged -= OnViewModelPropertyChanged;
            _wiredVm.PresencePulseRequested -= OnPresencePulseRequested;
            _wiredVm.Conversation.CollectionChanged -= OnConversationChanged;
        }

        _wiredVm = DataContext as OnboardingViewModel;
        if (_wiredVm is not null)
        {
            _wiredVm.PropertyChanged += OnViewModelPropertyChanged;
            _wiredVm.PresencePulseRequested += OnPresencePulseRequested;
            _wiredVm.Conversation.CollectionChanged += OnConversationChanged;
            // A replay brings a fresh onboarding to a view that is already showing.
            FocusNameSoon();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not OnboardingViewModel vm)
            return;

        if (e.PropertyName == nameof(OnboardingViewModel.SelectedCapability))
        {
            PlayDemo();
        }
        else if (e.PropertyName == nameof(OnboardingViewModel.CurrentStep))
        {
            if (vm.IsHelloStep)
                FocusNameSoon();
            else if (vm.IsCapabilitiesStep)
                PlayDemo();
        }
    }

    private void OnPresencePulseRequested(PresencePulse pulse)
    {
        if (_isActive && MotionEnabled && _presence is { IsVisible: true })
            _presence.Pulse(pulse);
    }

    private void OnNameBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _wiredVm is null)
            return;

        if (_wiredVm.ContinueCommand.CanExecute(null))
            _wiredVm.ContinueCommand.Execute(null);
        e.Handled = true;
    }

    private void FocusNameSoon()
    {
        if (_wiredVm is not { IsHelloStep: true } || _nameBox is null)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (!_nameBox.IsEffectivelyVisible)
                return;
            _nameBox.Focus();
            // A replay starts with the saved name; keep the caret after it.
            _nameBox.CaretIndex = _nameBox.Text?.Length ?? 0;
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Replays the capability's little conversation: request, then each step, then Lumi's reply.</summary>
    private void PlayDemo()
    {
        if (_wiredVm is not { IsCapabilitiesStep: true } || !MotionEnabled)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            var delay = TimeSpan.Zero;
            Rise(_demoHeader, delay);
            delay += TimeSpan.FromMilliseconds(140);
            Rise(_demoRequest, delay);
            delay += TimeSpan.FromMilliseconds(320);
            if (_demoSteps is not null)
            {
                foreach (var step in _demoSteps.GetRealizedContainers())
                {
                    Rise(step, delay);
                    delay += TimeSpan.FromMilliseconds(260);
                }
            }
            Rise(_demoReply, delay + TimeSpan.FromMilliseconds(160));
        }, DispatcherPriority.Loaded);
    }

    private static void Rise(Control? control, TimeSpan delay)
    {
        if (control is not null)
            SlideFadeEntrance.PlayComposited(control, 8, DemoRise, delay);
    }

    private void OnConversationContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (!MotionEnabled)
            return;

        var container = e.Container;
        Dispatcher.UIThread.Post(
            () => SlideFadeEntrance.PlayComposited(container, 10, TimeSpan.FromMilliseconds(320)),
            DispatcherPriority.Loaded);
    }

    private void OnConversationChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || _conversationScroller is null)
            return;

        // Keep the newest line in view once it has been laid out.
        Dispatcher.UIThread.Post(() => _conversationScroller.ScrollToEnd(), DispatcherPriority.Background);
    }
}
