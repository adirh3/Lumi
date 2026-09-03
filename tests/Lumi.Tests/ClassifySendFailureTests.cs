using System;
using System.Collections.Generic;
using System.Text.Json;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Guards <see cref="CopilotService.ClassifySendFailure"/> — the single decision now shared by both
/// send-failure handlers (the exception path <c>HandleSendError</c> and the structured
/// <c>session.error</c> path <c>SessionErrorEvent</c>). Extracting it removed a duplicated recompute
/// that drifted apart across earlier reviews; these tests both lock the verdict down in one place and
/// prove it still equals the per-overload composition it replaced.
/// </summary>
public sealed class ClassifySendFailureTests
{
    // ── Terminal override short-circuit ─────────────────────────────────────────────────────────
    [Theory]
    // A synthetic terminal state ("start a new chat" / "restart Lumi") is authored as unrecoverable by
    // its call site: never recoverable, never image — even when the text would otherwise match.
    [InlineData(null, null, "Could not process image")]
    [InlineData(500, "query", "internal server error")]
    [InlineData(null, null, "Please start a new chat")]
    public void TerminalOverride_IsFatal(int? status, string? type, string? message)
    {
        var result = CopilotService.ClassifySendFailure(status, type, message, hasTerminalOverride: true);
        Assert.False(result.Recoverable);
        Assert.False(result.IsImageError);
        Assert.Equal(SessionFailureDisposition.Fatal, result.Disposition);
    }

    // ── Recoverable unprocessable-image (the feature's whole point) ─────────────────────────────
    [Theory]
    // The exception path (string only) AND the structured generic-type path both classify an
    // unprocessable image as recoverable WITH the image copy, so the card tells the user a reset will
    // drop the bad asset.
    [InlineData(null, null, "Could not process image")]
    [InlineData(null, null, "The image data you provided does not represent a valid image.")]
    [InlineData(400, "invalid_request_error", "Could not process image")]
    public void UnprocessableImage_IsRecoverableImage(int? status, string? type, string? message)
    {
        var result = CopilotService.ClassifySendFailure(status, type, message, hasTerminalOverride: false);
        Assert.True(result.Recoverable);
        Assert.True(result.IsImageError);
        Assert.True(result.RequiresSessionRebuild);
        Assert.Equal(SessionFailureDisposition.RebuildSession, result.Disposition);
    }

    // ── Fatal errors: no Retry and no image copy ────────────────────────────────────────────────
    [Theory]
    [InlineData(401, "authentication", "unauthorized")]
    [InlineData(403, "authorization", "forbidden")]
    [InlineData(429, "rate_limit", "slow down")]
    [InlineData(413, "context_limit", "too long")]
    [InlineData(null, null, "You have exceeded your monthly quota")]
    [InlineData(null, null, "Please log in again")]
    public void Fatal_IsNotRecoverableAndNotImage(int? status, string? type, string? message)
    {
        var result = CopilotService.ClassifySendFailure(status, type, message, hasTerminalOverride: false);
        Assert.False(result.Recoverable);
        Assert.False(result.IsImageError);
        Assert.Equal(SessionFailureDisposition.Fatal, result.Disposition);
    }

    [Fact]
    // The critical overlap: a content-policy IMAGE block matches BOTH the image detector AND the fatal
    // classifier. Fatal must win — no Retry, and (crucially) NO image copy, so a reopen cannot re-derive
    // a false Retry from recovery-implying text. This is the invariant the two handlers must never break.
    public void FatalImageBlock_FatalWins_NoImageCopy()
    {
        var result = CopilotService.ClassifySendFailure(
            statusCode: 400, errorType: "content_policy", message: "Could not process image",
            hasTerminalOverride: false);
        Assert.False(result.Recoverable);
        Assert.False(result.IsImageError);
        Assert.False(result.RequiresSessionRebuild);
        Assert.Equal(SessionFailureDisposition.Fatal, result.Disposition);
    }

    [Theory]
    [InlineData(404, "query", "Session not found")]
    [InlineData(null, "session_not_found", "missing")]
    [InlineData(null, null, "Session file not found")]
    [InlineData(null, null, "no persisted or in-memory events found")]
    public void MissingSession_RebuildsWithoutImageCopy(int? status, string? type, string? message)
    {
        var result = CopilotService.ClassifySendFailure(status, type, message, hasTerminalOverride: false);
        Assert.True(result.Recoverable);
        Assert.False(result.IsImageError);
        Assert.True(result.RequiresSessionRebuild);
        Assert.Equal(SessionFailureDisposition.RebuildSession, result.Disposition);
    }

    [Theory]
    [InlineData(500, "query", "internal server error")]
    [InlineData(null, null, "The JSON-RPC connection with the remote party was lost")]
    [InlineData(null, "query", "The request is too large to send through CAPI Responses. (5.5 MB request; 5.0 MB limit)")]
    [InlineData(null, null, "Failed to persist session events: There is not enough space on the disk. (os error 112)")]
    [InlineData(null, null, "The CancellationTokenSource has been disposed.")]
    public void NonPoisoningRecoverableError_RetriesSameSession(int? status, string? type, string? message)
    {
        var result = CopilotService.ClassifySendFailure(status, type, message, hasTerminalOverride: false);
        Assert.True(result.Recoverable);
        Assert.False(result.IsImageError);
        Assert.False(result.RequiresSessionRebuild);
        Assert.Equal(SessionFailureDisposition.RetrySameSession, result.Disposition);
    }

    [Theory]
    [InlineData("Session not found", true)]
    [InlineData("Session file not found", true)]
    [InlineData("There is not enough space on the disk", false)]
    [InlineData("The CancellationTokenSource has been disposed", false)]
    [InlineData("500 internal server error", false)]
    public void ResumeFallback_OnlyCreatesReplacementForMissingSession(string message, bool expected)
    {
        var error = new InvalidOperationException(
            "resume failed",
            new InvalidOperationException(message));

        Assert.Equal(
            expected,
            ChatViewModel.ShouldCreateSessionFallbackAfterResumeFailure(error));
    }

    [Fact]
    public void FailureDisposition_RoundTripsThroughChatPersistenceAndClone()
    {
        var original = new ChatMessage
        {
            Role = "error",
            Content = "Localized recovery text",
            FailureDisposition = SessionFailureDisposition.RebuildSession
        };

        var json = JsonSerializer.Serialize(
            new List<ChatMessage> { original },
            AppDataJsonContext.Default.ListChatMessage);
        var restored = Assert.Single(JsonSerializer.Deserialize(
            json,
            AppDataJsonContext.Default.ListChatMessage)!);

        Assert.Equal(SessionFailureDisposition.RebuildSession, restored.FailureDisposition);
        Assert.Equal(SessionFailureDisposition.RebuildSession, restored.Clone().FailureDisposition);
    }

    [Theory]
    // Refactor-equivalence: the exception path feeds (null, null, message) and must reproduce EXACTLY
    // the old inline composition — recoverable = !IsFatal(message); image = recoverable && IsUnprocessable(message).
    // Includes the request-ID false-positive guard ("req_401403abc" must not read as an auth failure).
    [InlineData("Could not process image")]
    [InlineData("500 internal server error")]
    [InlineData("401 Unauthorized")]
    [InlineData("You have exceeded your monthly quota")]
    [InlineData("Session not found")]
    [InlineData("Streaming failed (request id req_401403abc)")]
    public void StringPath_MatchesInlineComposition(string message)
    {
        var expectedRecoverable = !CopilotService.IsFatalNonRetryableError(message);
        var expectedImage = expectedRecoverable && CopilotService.IsUnprocessableImageError(message);

        var result = CopilotService.ClassifySendFailure(null, null, message, hasTerminalOverride: false);

        Assert.Equal(expectedRecoverable, result.Recoverable);
        Assert.Equal(expectedImage, result.IsImageError);
        Assert.Equal(ExpectedDisposition(expectedRecoverable, expectedImage, null, message), result.Disposition);
    }

    [Theory]
    // Refactor-equivalence for the structured path used by SessionErrorEvent (status + errorType known).
    [InlineData(400, "invalid_request_error", "Could not process image")]
    [InlineData(401, "authentication", "unauthorized")]
    [InlineData(400, "content_policy", "Could not process image")]
    [InlineData(500, "query", "internal server error")]
    [InlineData(null, "model_not_supported", "the selected model is unavailable")]
    public void StructuredPath_MatchesInlineComposition(int? status, string? type, string? message)
    {
        var expectedRecoverable = !CopilotService.IsFatalNonRetryableError(status, type, message);
        var expectedImage = expectedRecoverable && CopilotService.IsUnprocessableImageError(status, type, message);

        var result = CopilotService.ClassifySendFailure(status, type, message, hasTerminalOverride: false);

        Assert.Equal(expectedRecoverable, result.Recoverable);
        Assert.Equal(expectedImage, result.IsImageError);
        Assert.Equal(ExpectedDisposition(expectedRecoverable, expectedImage, type, message), result.Disposition);
    }

    private static SessionFailureDisposition ExpectedDisposition(
        bool recoverable,
        bool isImage,
        string? errorType,
        string? message)
        => !recoverable
            ? SessionFailureDisposition.Fatal
            : isImage || CopilotService.IsMissingSessionError(errorType, message)
                ? SessionFailureDisposition.RebuildSession
                : SessionFailureDisposition.RetrySameSession;
}
