using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Services;

namespace Lumi.ViewModels;

/// <summary>
/// Shared ViewModel for GitHub sign-in with device code flow.
/// Used by OnboardingView, SettingsView, and any other surface that needs login.
/// </summary>
public partial class GitHubLoginViewModel : ObservableObject
{
    private readonly CopilotService _copilotService;
    private CancellationTokenSource? _signInCts;

    // ── State ──
    [ObservableProperty] private bool _isSigningIn;
    [ObservableProperty] private bool _isAuthenticated;
    [ObservableProperty] private string _gitHubLogin = "";
    [ObservableProperty] private string? _errorText;

    // ── Device code flow ──
    [ObservableProperty] private string _deviceCode = "";
    [ObservableProperty] private string _verificationUrl = "";
    [ObservableProperty] private bool _hasDeviceCode;
    [ObservableProperty] private bool _deviceCodeCopied;

    /// <summary>Raised when text should be copied to the clipboard (View handles actual clipboard access).</summary>
    public event Action<string>? CopyToClipboardRequested;

    /// <summary>Raised when IsAuthenticated changes (for parent VMs to react).</summary>
    public event Action<bool>? AuthenticationChanged;

    public GitHubLoginViewModel(CopilotService copilotService)
    {
        _copilotService = copilotService;
    }

    partial void OnIsAuthenticatedChanged(bool value) => AuthenticationChanged?.Invoke(value);

    /// <summary>Refreshes auth status from the CopilotService without triggering a new login flow.</summary>
    public async Task RefreshAsync()
    {
        try
        {
            var status = await _copilotService.GetAuthStatusAsync();
            IsAuthenticated = status.IsAuthenticated == true;
            GitHubLogin = status.Login ?? _copilotService.GetStoredLogin() ?? "";
            if (IsAuthenticated)
                ErrorText = null;
        }
        catch
        {
            IsAuthenticated = false;
            GitHubLogin = "";
        }
    }

    [RelayCommand]
    private async Task SignIn()
    {
        if (IsSigningIn || IsAuthenticated) return;

        IsSigningIn = true;
        ErrorText = null;
        HasDeviceCode = false;
        DeviceCode = "";
        VerificationUrl = "";
        DeviceCodeCopied = false;
        _signInCts?.Dispose();
        _signInCts = new CancellationTokenSource();

        try
        {
            var result = await _copilotService.SignInAsync(
                onDeviceCode: (code, url) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        DeviceCode = code;
                        VerificationUrl = url;
                        HasDeviceCode = true;
                    });
                },
                ct: _signInCts.Token);

            if (result != CopilotSignInResult.Success)
            {
                ErrorText = result switch
                {
                    CopilotSignInResult.CliNotFound => Loc.Status_GitHubSignInCliMissing,
                    _ => Loc.Status_GitHubSignInFailed,
                };
                return;
            }

            await RefreshAsync();

            if (!IsAuthenticated)
                ErrorText = Loc.Status_GitHubSignInRefreshFailed;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorText = string.Format(Loc.Status_GitHubSignInUnexpectedError, ex.Message);
        }
        finally
        {
            IsSigningIn = false;
            HasDeviceCode = false;
        }
    }

    [RelayCommand]
    private void CopyDeviceCode()
    {
        if (string.IsNullOrEmpty(DeviceCode)) return;
        CopyToClipboardRequested?.Invoke(DeviceCode);
        DeviceCodeCopied = true;
    }

    [RelayCommand]
    private void OpenVerificationUrl()
    {
        if (string.IsNullOrEmpty(VerificationUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo(VerificationUrl) { UseShellExecute = true });
        }
        catch { /* best-effort */ }
    }

    [RelayCommand]
    private async Task SignOut()
    {
        if (!IsAuthenticated) return;

        ErrorText = null;
        try
        {
            await _copilotService.SignOutAsync();
            IsAuthenticated = false;
            GitHubLogin = "";
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    public void CancelSignIn()
    {
        _signInCts?.Cancel();
    }
}
