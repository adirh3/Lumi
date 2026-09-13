namespace Lumi.ViewModels;

/// <summary>
/// Background task handling is fully automatic via the Copilot SDK:
/// - <c>AssistantIdleEvent</c> makes the main assistant ready without releasing the session
/// - <c>SessionBackgroundTasksChangedEvent</c> marks that shells/agents are still running
/// - <c>SessionIdleEvent</c> is only emitted once background work is drained
/// - <c>HasPendingBackgroundWork</c> is set on the runtime state while background work is in flight
/// - <c>IsChatRuntimeActive</c> prevents session cleanup while background work is pending
/// - <c>IsBusy</c> / <c>Chat.IsRunning</c> are assistant UX, never session ownership predicates
/// - The SDK auto-triggers follow-up turns when background tasks complete
/// - No custom tools or tracking needed
/// </summary>
public partial class ChatViewModel;
