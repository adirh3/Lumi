using Lumi.Services;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Guards <see cref="CopilotService.IsUnprocessableImageError(string?)"/> and its structured overload.
/// An image the backend refuses to process lives in the server-side session history and is re-sent on
/// every turn, permanently bricking the chat; detecting it lets Lumi rebuild the session as text.
/// </summary>
public sealed class UnprocessableImageErrorTests
{
    [Theory]
    // Verbatim payloads observed when the "Sub Agent Window Bug" chat was bricked (CLI 1.0.x).
    [InlineData("CAPIError: 400 {\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"Could not process image\"}}")]
    [InlineData("Could not process image")]
    [InlineData("The image data you provided does not represent a valid image. Please check your input and try again.")]
    [InlineData("400 {\"error\":{\"code\":\"bad_request\",\"type\":\"websocket_error\",\"message\":\"The image data you provided does not represent a valid image.\"}}")]
    // Close phrasings we also want to recover from.
    [InlineData("Unable to process image attachment")]
    [InlineData("The provided image is not a valid image")]
    [InlineData("Invalid image data")]
    [InlineData("The image could not be processed")]
    public void IsUnprocessableImageError_DetectsImageRejections(string error)
        => Assert.True(CopilotService.IsUnprocessableImageError(error));

    [Theory]
    // Must NOT hijack unrelated errors — those keep their normal (often retryable) handling.
    [InlineData("Could not process request")]           // not about an image
    [InlineData("500 internal server error")]
    [InlineData("Session not found")]
    [InlineData("401 unauthorized: Bad credentials")]
    [InlineData("context window exceeded")]
    [InlineData("You have been rate limited, try again later")]
    [InlineData("The image looks great!")]               // mentions image but not a failure
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsUnprocessableImageError_IgnoresUnrelatedFailures(string? error)
        => Assert.False(CopilotService.IsUnprocessableImageError(error));

    [Theory]
    // Structured session.error payloads: match on the image-rejection message regardless of the
    // (generic) request category/status.
    [InlineData(400, "invalid_request_error", "Could not process image")]
    [InlineData(400, "query", "The image data you provided does not represent a valid image.")]
    [InlineData(null, "websocket_error", "The image data you provided does not represent a valid image.")]
    public void IsUnprocessableImageError_Structured_DetectsImageRejections(int? statusCode, string? errorType, string? message)
        => Assert.True(CopilotService.IsUnprocessableImageError(statusCode, errorType, message));

    [Theory]
    // Auth / quota / rate-limit / context categories have dedicated handling and must never be
    // routed through image recovery, even in the unlikely event their message mentions an image.
    [InlineData(429, "rate_limit", "invalid image rate")]
    [InlineData(402, "quota", "could not process image quota")]
    [InlineData(413, "context_limit", "image not a valid image for context")]
    [InlineData(401, "authentication", "could not process image")]
    [InlineData(403, "authorization", "invalid image")]
    // Genuinely unrelated structured errors.
    [InlineData(500, "query", "internal server error")]
    [InlineData(404, "query", "Session not found")]
    public void IsUnprocessableImageError_Structured_IgnoresUnrelatedOrHandledCategories(int? statusCode, string? errorType, string? message)
        => Assert.False(CopilotService.IsUnprocessableImageError(statusCode, errorType, message));

    [Theory]
    // The image detector's structured overload only excludes {quota, rate_limit, context_limit,
    // authentication, authorization}. A FATAL error in a DIFFERENT category (content policy / filter,
    // permission, model access) — or a bare 401/403 status under a generic type — whose message ALSO
    // matches image phrasing is therefore BOTH image-like AND fatal. The display callers
    // (HandleSendError and SessionErrorEvent) MUST gate the "click Retry" image copy on `recoverable`;
    // otherwise such a fatal chat shows recovery copy with no button and re-derives a false Retry on
    // reopen. This locks in that overlap so the gating can never be quietly "simplified" away.
    [InlineData(400, "content_policy", "Could not process image")]
    [InlineData(400, "content_filter", "invalid image")]
    [InlineData(null, "permission", "The image data you provided does not represent a valid image.")]
    [InlineData(null, "model_not_supported", "could not process image")]
    [InlineData(403, "query", "could not process image")]
    public void ImageRejection_CanAlsoBeFatal_SoCallersMustGateOnRecoverable(int? statusCode, string? errorType, string? message)
    {
        Assert.True(CopilotService.IsUnprocessableImageError(statusCode, errorType, message));
        Assert.True(CopilotService.IsFatalNonRetryableError(statusCode, errorType, message));
    }
}
