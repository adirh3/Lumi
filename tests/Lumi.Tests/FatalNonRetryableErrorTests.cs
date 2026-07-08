using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Guards <see cref="CopilotService.IsFatalNonRetryableError(string?)"/> and its structured overload.
/// This is the single gate that decides whether Lumi even offers Retry on a terminal error: only a
/// hard credential / capacity / policy limit is fatal. EVERY other terminal error is treated as
/// recoverable by rebuilding the session from the transcript as text (which safely drops any poisoned
/// history, such as an image the backend can't process), so misclassifying here either hides a valid
/// Retry or dangles false hope on an unrecoverable error.
/// </summary>
public sealed class FatalNonRetryableErrorTests
{
    [Theory]
    // Hard limits where resending the same conversation cannot possibly help.
    [InlineData("You have exceeded your monthly quota")]
    [InlineData("429 rate limit reached, try again later")]
    [InlineData("error type: rate_limit")]
    [InlineData("This model's maximum context length is 128000 tokens")]
    [InlineData("context_length_exceeded")]
    [InlineData("The context window for this conversation is full")]
    [InlineData("context_limit reached")]
    [InlineData("maximum context exceeded")]
    [InlineData("Blocked by content policy")]
    [InlineData("content_policy_violation")]
    [InlineData("Response filtered by the content filter")]
    [InlineData("content_filter triggered")]
    [InlineData("Flagged by Responsible AI service")]
    [InlineData("Your session expired, please log in again")]
    [InlineData("You have been signed out — sign in again to continue")]
    [InlineData("You are logged out")]
    [InlineData("Please re-authenticate to continue")]
    // Genuine logout / permission failures — a bare 401/403 that the transient allow-list let through.
    [InlineData("401 Unauthorized")]
    [InlineData("403 Forbidden")]
    [InlineData("Bad credentials")]
    [InlineData("Request failed: Unauthorized")]
    [InlineData("authentication failed")]
    [InlineData("You are not authenticated")]
    // Permission / model-access denials whose only status marker (403) we don't substring-match.
    [InlineData("403 {\"message\":\"You do not have access to this model\",\"code\":\"model_not_supported\"}")]
    [InlineData("Access denied")]
    [InlineData("Permission denied")]
    [InlineData("Model not supported for your plan")]
    public void IsFatalNonRetryableError_DetectsHardLimits(string error)
        => Assert.True(CopilotService.IsFatalNonRetryableError(error));

    [Theory]
    // Everything else is recoverable via session-rebuild-as-text and must stay retryable —
    // crucially including the unprocessable-image failure, which is the whole point of the feature.
    [InlineData("Could not process image")]
    [InlineData("The image data you provided does not represent a valid image.")]
    [InlineData("Copilot request failed")]
    [InlineData("500 internal server error")]
    [InlineData("Session not found")]
    [InlineData("The JSON-RPC connection with the remote party was lost")]
    [InlineData("Communication error with Copilot CLI")]
    // Bare "401"/"403" digits collide with request IDs and must NOT be read as an auth failure.
    [InlineData("Streaming failed (request id req_401403abc)")]
    // A generic "access" mention that is NOT a permission denial stays recoverable (the access-denial
    // phrases require "not have access" / "access denied" / "permission denied", not bare "access").
    [InlineData("Temporarily unable to access the server, retrying")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsFatalNonRetryableError_TreatsEverythingElseAsRecoverable(string? error)
        => Assert.False(CopilotService.IsFatalNonRetryableError(error));

    [Theory]
    // Structured session.error categories that are fatal by TYPE alone, regardless of message.
    [InlineData(401, "authentication", "unauthorized")]
    [InlineData(403, "authorization", "forbidden")]
    [InlineData(403, "permission", "insufficient permissions")]
    [InlineData(402, "quota", "over quota")]
    [InlineData(402, "insufficient_quota", "no credits")]
    [InlineData(429, "rate_limit", "slow down")]
    [InlineData(413, "context_limit", "too long")]
    [InlineData(413, "context_length_exceeded", "too long")]
    [InlineData(400, "content_policy", "blocked")]
    [InlineData(400, "content_filter", "filtered")]
    // A model-access denial by type alone (null status, opaque message) — retrying can't grant access.
    [InlineData(null, "model_not_supported", "the selected model is unavailable")]
    public void IsFatalNonRetryableError_Structured_DetectsFatalCategories(int? statusCode, string? errorType, string? message)
        => Assert.True(CopilotService.IsFatalNonRetryableError(statusCode, errorType, message));

    [Theory]
    // Generic categories fall through to the message: an unprocessable image or a transient/server
    // failure stays recoverable, while a fatal phrase in the message is still caught.
    [InlineData(400, "invalid_request_error", "Could not process image")]
    [InlineData(400, "query", "The image data you provided does not represent a valid image.")]
    [InlineData(500, "query", "internal server error")]
    [InlineData(404, "query", "Session not found")]
    public void IsFatalNonRetryableError_Structured_KeepsRecoverableCategoriesRetryable(int? statusCode, string? errorType, string? message)
        => Assert.False(CopilotService.IsFatalNonRetryableError(statusCode, errorType, message));

    [Theory]
    // A fatal phrase in the message is caught even when the structured type is generic.
    [InlineData(400, "query", "You exceeded your quota for today")]
    [InlineData(400, "invalid_request_error", "maximum context length exceeded")]
    public void IsFatalNonRetryableError_Structured_DetectsFatalMessageUnderGenericType(int? statusCode, string? errorType, string? message)
        => Assert.True(CopilotService.IsFatalNonRetryableError(statusCode, errorType, message));

    [Theory]
    // A bare 401/403 status is a genuine logout / permission failure even when the type is generic/blank
    // and the message carries no fatal keyword — without the status check these would slip through as
    // recoverable and dangle a false Retry (the transient allow-list already ran upstream).
    [InlineData(401, null, "The server returned an error")]
    [InlineData(401, "query", "unexpected failure")]
    [InlineData(403, null, "denied")]
    [InlineData(403, "query", "no access")]
    public void IsFatalNonRetryableError_Structured_DetectsBareAuthStatus(int? statusCode, string? errorType, string? message)
        => Assert.True(CopilotService.IsFatalNonRetryableError(statusCode, errorType, message));
}
