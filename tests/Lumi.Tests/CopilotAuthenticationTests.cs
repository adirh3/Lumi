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
    // Verbatim mid-session 401s/403s observed in ~/.copilot/logs/process-*.log (CLI 1.0.60):
    [InlineData("401 unauthorized: authenticating token: twirp error internal: twirp error internal: failed to do request: Post \"http://usersd.usersd-production.svc.cluster.local:8080\"")]
    [InlineData("401 unauthorized: unauthorized: AuthenticateToken authentication failed")]
    [InlineData("CAPIError: 401 401 401 unauthorized: authenticating token: twirp error internal")]
    [InlineData("503 service unavailable: 401 unauthorized")]
    [InlineData("401 Unauthorized — backend temporarily unavailable")]
    // Bare 403 "forbidden" with an empty message body (spurious mid-session edge rejection):
    [InlineData("403 {\"message\":\"\",\"code\":\"forbidden\"}")]
    [InlineData("CAPIError: 403 {\"message\":\"\",\"code\":\"forbidden\"}")]
    public void IsTransientServerAuthError_TreatsServerSide401sAsRetryable(string error)
        => Assert.True(CopilotService.IsTransientServerAuthError(error));

    [Theory]
    // Genuine credential/authorization failures and unrelated errors must NOT be retried in a loop:
    [InlineData("401 unauthorized: Bad credentials")]
    [InlineData("403 Forbidden: token does not have the required scopes")]
    [InlineData("403 {\"message\":\"You do not have access to this model\",\"code\":\"model_not_supported\"}")]
    [InlineData("403 {\"message\":\"Resource not accessible by integration\",\"code\":\"forbidden\"}")]
    [InlineData("Session not found")]
    [InlineData("The operation was canceled.")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsTransientServerAuthError_DoesNotRetryGenuineOrUnrelatedFailures(string? error)
        => Assert.False(CopilotService.IsTransientServerAuthError(error));

    [Theory]
    // Structured session.error payloads: spurious mid-session 401/403 with no genuine-denial wording.
    [InlineData(403, "authorization", "")]
    [InlineData(401, "authentication", "")]
    [InlineData(403, "authorization", null)]
    [InlineData(401, "authentication", "401 unauthorized: authenticating token: twirp error internal")]
    [InlineData(null, "authorization", "")]
    [InlineData(403, null, "")]
    [InlineData(500, "query", "twirp error internal: failed to do request")]
    public void IsTransientServerAuthError_Structured_TreatsServerSideAuthBlipsAsRetryable(int? statusCode, string? errorType, string? message)
        => Assert.True(CopilotService.IsTransientServerAuthError(statusCode, errorType, message));

    [Theory]
    // Genuine denials and non-auth categories must NOT be retried in a loop.
    [InlineData(401, "authentication", "Bad credentials")]
    [InlineData(403, "authorization", "You do not have access to this model")]
    [InlineData(403, "authorization", "Resource not accessible by integration")]
    [InlineData(403, "authorization", "token does not have the required scopes")]
    [InlineData(429, "rate_limit", "You have been rate limited")]
    [InlineData(402, "quota", "quota_exceeded")]
    [InlineData(413, "context_limit", "context window exceeded")]
    [InlineData(500, "query", "internal server error")]
    public void IsTransientServerAuthError_Structured_DoesNotRetryGenuineOrNonAuthFailures(int? statusCode, string? errorType, string? message)
        => Assert.False(CopilotService.IsTransientServerAuthError(statusCode, errorType, message));
}
