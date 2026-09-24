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
    private int _authStateVersion;

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
    public async Task<bool> RefreshAsync()
    {
        var version = ++_authStateVersion;
        try
        {
            var status = await _copilotService.GetAuthStatusAsync();
            if (version != _authStateVersion)
                return false;

            var isAuthenticated = status.IsAuthenticated == true;
            GitHubLogin = isAuthenticated ? status.Login ?? _copilotService.GetStoredLogin() ?? "" : "";
            IsAuthenticated = isAuthenticated;
            if (isAuthenticated || ErrorText == Loc.Status_GitHubAuthCheckFailed)
                ErrorText = null;
            return true;
        }
        catch (Exception ex)
        {
            if (version != _authStateVersion)
                return false;

            Debug.WriteLine($"[Lumi] Could not check GitHub authentication ({ex.GetType().Name}).");
            ErrorText = Loc.Status_GitHubAuthCheckFailed;
            return false;
        }
    }

    [RelayCommand]
    private async Task SignIn()
    {
        if (IsSigningIn || IsAuthenticated) return;

        _authStateVersion++;
        IsSigningIn = true;
        ErrorText = null;
        HasDeviceCode = false;
        DeviceCode = "";
        VerificationUrl = "";
        DeviceCodeCopied = false;
        _signInCts?.Dispose();
        var signInCts = _signInCts = new CancellationTokenSource();

        try
        {
            var result = await _copilotService.SignInAsync(
                onDeviceCode: (code, url) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!ReferenceEquals(_signInCts, signInCts)
                            || signInCts.IsCancellationRequested || !IsSigningIn)
                            return;

                        DeviceCode = code;
                        VerificationUrl = url;
                        HasDeviceCode = true;
                    });
                },
                ct: signInCts.Token);

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
            DeviceCode = "";
            VerificationUrl = "";
            DeviceCodeCopied = false;
            if (ReferenceEquals(_signInCts, signInCts))
                _signInCts = null;
            signInCts.Dispose();
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

        _authStateVersion++;
        ErrorText = null;
        try
        {
            // SignOutAsync returns false when `copilot logout` did not complete. Honor that result:
            // never clear the signed-in UI state unless the credential was actually removed,
            // otherwise Lumi would show "signed out" while the token is still valid on disk.
            if (!await _copilotService.SignOutAsync())
            {
                ErrorText = Loc.Status_GitHubSignOutFailed;
                return;
            }
            _authStateVersion++;
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
