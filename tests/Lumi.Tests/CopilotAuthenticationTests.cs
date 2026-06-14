using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

public sealed class CopilotAuthenticationTests
{
    [Fact]
    public void ParseStoredCopilotIdentity_SupportsCliConfigCommentsAndCamelCaseKey()
    {
        var identity = CopilotService.ParseStoredCopilotIdentity("""
            // User settings belong in settings.json.
            {
              "lastLoggedInUser": {
                "host": "https://github.com",
                "login": "octocat"
              },
            }
            """);

        Assert.Equal("octocat", identity.Login);
        Assert.Equal("https://github.com", identity.Host);
    }

    [Theory]
    // PROVABLY transient server-side failures: an internal twirp/usersd RPC failure (observed
    // verbatim in ~/.copilot/logs, CLI 1.0.60), or a plain 5xx-style "unavailable"/"timeout" phrase.
    [InlineData("401 unauthorized: authenticating token: twirp error internal: twirp error internal: failed to do request: Post \"http://usersd.usersd-production.svc.cluster.local:8080\"")]
    [InlineData("CAPIError: 401 401 401 unauthorized: authenticating token: twirp error internal")]
    [InlineData("503 service unavailable: 401 unauthorized")]
    [InlineData("401 Unauthorized — backend temporarily unavailable")]
    [InlineData("502 bad gateway: gateway timeout")]
    public void IsTransientServerAuthError_RetriesOnlyProvableServerInternalMarkers(string error)
        => Assert.True(CopilotService.IsTransientServerAuthError(error));

    [Theory]
    // CRITICAL login-path guard: anything that could be a genuine logout / expired-token / SSO /
    // denial — including a BARE 401/403, an empty body, or "AuthenticateToken authentication failed"
    // on its own — must NOT be auto-retried; it has to surface so the user can re-authenticate.
    [InlineData("401 unauthorized: Bad credentials")]
    [InlineData("403 Forbidden: token does not have the required scopes")]
    [InlineData("403 {\"message\":\"You do not have access to this model\",\"code\":\"model_not_supported\"}")]
    [InlineData("403 {\"message\":\"Resource not accessible by integration\",\"code\":\"forbidden\"}")]
    [InlineData("401 unauthorized: unauthorized: AuthenticateToken authentication failed")]
    [InlineData("403 {\"message\":\"\",\"code\":\"forbidden\"}")]
    [InlineData("CAPIError: 403 {\"message\":\"\",\"code\":\"forbidden\"}")]
    [InlineData("401 Unauthorized")]
    [InlineData("403 Forbidden")]
    [InlineData("401 unauthorized: authenticating token: token expired")]
    [InlineData("403 forbidden: SSO authorization required")]
    // Genuine denials that happen to embed a generic "try again later" must NOT be auto-retried:
    // the phrase is not proof of a transient server-internal failure. Regression guard for the
    // string overload, which has no error-category to fall back on (rate-limit/quota/SSO wording).
    [InlineData("403 forbidden: SSO authorization required, please try again later")]
    [InlineData("401 unauthorized: Bad credentials. Please try again later.")]
    [InlineData("You have been rate limited, try again later")]
    [InlineData("Session not found")]
    [InlineData("The operation was canceled.")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsTransientServerAuthError_DoesNotRetryGenuineOrUnrelatedFailures(string? error)
        => Assert.False(CopilotService.IsTransientServerAuthError(error));

    [Theory]
    // Structured session.error payloads: retry ONLY when the message carries a provable backend-
    // internal marker, regardless of the auth status/category.
    [InlineData(401, "authentication", "401 unauthorized: authenticating token: twirp error internal")]
    [InlineData(500, "query", "twirp error internal: failed to do request")]
    [InlineData(503, "authentication", "service temporarily unavailable")]
    public void IsTransientServerAuthError_Structured_RetriesOnlyProvableServerInternalMarkers(int? statusCode, string? errorType, string? message)
        => Assert.True(CopilotService.IsTransientServerAuthError(statusCode, errorType, message));

    [Theory]
    // CRITICAL login-path guard (structured): a 401/403 status or an authentication/authorization
    // category is NOT transient on its own. Bare/empty auth errors and genuine denials must surface,
    // and quota/rate-limit/context categories (even when worded like "try again later") are never
    // routed through the auth-retry path.
    [InlineData(403, "authorization", "")]
    [InlineData(401, "authentication", "")]
    [InlineData(403, "authorization", null)]
    [InlineData(null, "authorization", "")]
    [InlineData(403, null, "")]
    [InlineData(401, "authentication", "Unauthorized")]
    [InlineData(401, "authentication", "Bad credentials")]
    [InlineData(401, "authentication", "your token has expired")]
    [InlineData(403, "authorization", "You do not have access to this model")]
    [InlineData(403, "authorization", "Resource not accessible by integration")]
    [InlineData(403, "authorization", "token does not have the required scopes")]
    [InlineData(403, "authorization", "SSO authorization required")]
    // A genuine auth-category denial whose message embeds "try again later" must stay non-transient
    // (it would have wrongly matched the old allow-list and looped the user instead of re-auth):
    [InlineData(403, "authorization", "SSO authorization required. Please try again later.")]
    [InlineData(429, "rate_limit", "You have been rate limited, try again later")]
    [InlineData(402, "quota", "quota_exceeded")]
    [InlineData(413, "context_limit", "context window exceeded")]
    [InlineData(500, "query", "internal server error")]
    public void IsTransientServerAuthError_Structured_DoesNotRetryGenuineOrNonAuthFailures(int? statusCode, string? errorType, string? message)
        => Assert.False(CopilotService.IsTransientServerAuthError(statusCode, errorType, message));
}
