using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Copilot;
using Microsoft.Copilot.Rpc;

namespace Lumi.Services;

internal static partial class CopilotSessionTasks
{
    // CLI 1.0.114 writes detached-shell PIDs as JSON numbers like 1234.0, which SDK 2.0.1
    // rejects as Int64. Read the task snapshots on the SDK connection and omit the unused PID.
    private static readonly Func<SessionRpc, string, object[], CancellationToken, Task<JsonElement>>? Invoke =
        typeof(CopilotClient).GetMethod("InvokeRpcAsync", 1, BindingFlags.NonPublic | BindingFlags.Static,
                binder: null, types: [typeof(SessionRpc), typeof(string), typeof(object[]), typeof(CancellationToken)],
                modifiers: null)
            ?.MakeGenericMethod(typeof(JsonElement))
            .CreateDelegate<Func<SessionRpc, string, object[], CancellationToken, Task<JsonElement>>>();

    private static readonly JsonSerializerOptions TaskJson = CreateTaskJson();
    private static readonly JsonTypeInfo<IList<TaskInfo>> TaskListType =
        (JsonTypeInfo<IList<TaskInfo>>)TaskJson.GetTypeInfo(typeof(IList<TaskInfo>));
    private static readonly JsonTypeInfo<TaskProgress> TaskProgressType =
        (JsonTypeInfo<TaskProgress>)TaskJson.GetTypeInfo(typeof(TaskProgress));

    internal static async Task<IList<TaskInfo>> ListAsync(CopilotSession session, CancellationToken ct = default)
    {
        if (Invoke is null)
            return (await session.Rpc.Tasks.ListAsync(ct)).Tasks;

        var args = SerializeRequest(session);
        var result = await Invoke(session.Rpc, "session.tasks.list", [args], ct);
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("tasks", out var tasks))
            throw new JsonException("Copilot did not return a session task list.");

        return tasks.Deserialize(TaskListType)
            ?? throw new JsonException("Copilot returned a null session task list.");
    }

    internal static async Task<TaskProgress?> GetProgressAsync(CopilotSession session, string taskId, CancellationToken ct = default)
    {
        if (Invoke is null)
            return (await session.Rpc.Tasks.GetProgressAsync(taskId, ct)).Progress;

        var result = await Invoke(session.Rpc, "session.tasks.getProgress", [SerializeRequest(session, taskId)], ct);
        return result.TryGetProperty("progress", out var progress) ? progress.Deserialize(TaskProgressType) : null;
    }

    private static JsonElement SerializeRequest(CopilotSession session, string? taskId = null) =>
        JsonSerializer.SerializeToElement(new SessionTaskRequest(session.SessionId, taskId), TaskSnapshotJsonContext.Default.SessionTaskRequest);

    private static JsonSerializerOptions CreateTaskJson()
    {
        var options = new JsonSerializerOptions(TaskSnapshotJsonContext.Default.Options)
        {
            TypeInfoResolver = TaskSnapshotJsonContext.Default.WithAddedModifier(info =>
            {
                if (info.Type == typeof(TaskInfoShell) || info.Type == typeof(TaskProgressShell))
                    info.Properties.Remove(info.Properties.Single(property => property.Name == "pid"));
            })
        };
        options.MakeReadOnly();
        return options;
    }

    private sealed record SessionTaskRequest(string SessionId,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Id);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(SessionTaskRequest))]
    [JsonSerializable(typeof(IList<TaskInfo>))]
    [JsonSerializable(typeof(TaskProgress))]
    private sealed partial class TaskSnapshotJsonContext : JsonSerializerContext;
}
