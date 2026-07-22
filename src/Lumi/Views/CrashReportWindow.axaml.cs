using System;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Lumi.Localization;
using Lumi.Services;

namespace Lumi.Views;

public partial class CrashReportWindow : Window
{
    private static readonly TimeSpan EmailActivationGracePeriod = TimeSpan.FromSeconds(2);

    private readonly CrashReportData? _report;
    private TextBox _detailsTextBox = null!;
    private TextBlock _progressText = null!;
    private TextBlock _statusText = null!;
    private Button _restartButton = null!;
    private Button _sendButton = null!;

    public CrashReportWindow()
    {
        InitializeComponent();
        WindowChromeInterop.EnableNativeMinMaxAnimations(this);
        AppIcon.ApplyWindowIcon(this);
        AutomationProperties.SetName(this, Loc.Get("Crash_WindowTitle"));
    }

    internal CrashReportWindow(CrashReportData report, string? reporterLaunchError = null)
        : this()
    {
        _report = report;
        _detailsTextBox.Text = report.BuildDiagnosticText();

        if (!string.IsNullOrWhiteSpace(reporterLaunchError))
        {
            ShowError(Loc.Get(
                "Crash_ReporterLaunchFailed",
                reporterLaunchError));
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _detailsTextBox = this.FindControl<TextBox>("CrashReportDetails")
            ?? throw new InvalidOperationException("Crash report details control was not found.");
        _progressText = this.FindControl<TextBlock>("CrashReportProgress")
            ?? throw new InvalidOperationException("Crash report progress control was not found.");
        _statusText = this.FindControl<TextBlock>("CrashReportStatus")
            ?? throw new InvalidOperationException("Crash report status control was not found.");
        _restartButton = this.FindControl<Button>("RestartLumiButton")
            ?? throw new InvalidOperationException("Restart button was not found.");
        _sendButton = this.FindControl<Button>("SendFeedbackButton")
            ?? throw new InvalidOperationException("Send feedback button was not found.");
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(() => _sendButton.Focus(), DispatcherPriority.Input);
    }

    private void OnRestartLumiClick(object? sender, RoutedEventArgs e)
    {
        SetBusy(true);
        if (!LumiProcessLauncher.TryLaunch(out var error))
        {
            ShowError(Loc.Get("Crash_ActionFailed", error));
            SetBusy(false);
            return;
        }

        Close();
    }

    private async void OnSendFeedbackClick(object? sender, RoutedEventArgs e)
    {
        if (_report is null)
            return;

        SetBusy(true);
        if (!CrashReportService.TryOpenFeedbackEmail(_report, out var emailError))
        {
            ShowError(Loc.Get("Crash_ActionFailed", emailError));
            SetBusy(false);
            return;
        }

        ShowProgress(Loc.Get("Crash_OpeningEmail"));
        await Task.Delay(EmailActivationGracePeriod);

        if (!LumiProcessLauncher.TryLaunch(out var restartError))
        {
            ShowError(Loc.Get("Crash_ActionFailed", restartError));
            SetBusy(false);
            return;
        }

        Close();
    }

    private void SetBusy(bool isBusy)
    {
        _restartButton.IsEnabled = !isBusy;
        _sendButton.IsEnabled = !isBusy;
    }

    private void ShowError(string message)
    {
        _progressText.IsVisible = false;
        _statusText.Text = message;
        _statusText.IsVisible = true;
    }

    private void ShowProgress(string message)
    {
        _statusText.IsVisible = false;
        _progressText.Text = message;
        _progressText.IsVisible = true;
    }
}
