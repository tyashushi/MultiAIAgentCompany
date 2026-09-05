using System.Text.Json;
using MultiAIAgentCompany.Core.Status;

namespace MultiAIAgentCompany.Core.Agents.Antigravity;

/// <summary>Antigravity stream-json の1行を表す、プロセス非依存のプロトコル型。</summary>
public abstract record AntigravityEvent
{
    public sealed record Init(string? Cwd, string? PermissionMode, IReadOnlyList<string> Tools) : AntigravityEvent;

    /// <remarks><c>tool_info.parameters</c> はツール入力を含むため、意図的に保持しない。</remarks>
    public sealed record StepUpdate(int? StepIndex, string? StepType, string? State, string? ToolName) : AntigravityEvent;

    public sealed record Finished(string? Status, string Response,
        IReadOnlyList<AntigravityDeniedAction> DeniedActions) : AntigravityEvent;

    public sealed record Unknown(string Reason, string RawFirst200) : AntigravityEvent;
}

public sealed record AntigravityDeniedAction(string Action, string? DisplayName);

/// <summary>Antigravity stream-json を例外を出さずに読む。</summary>
public static class AntigravityStreamReader
{
    public static AntigravityEvent ReadLine(string line)
    {
        if (line is null) return new AntigravityEvent.Unknown("行が null", string.Empty);

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Unknown("JSON の根がオブジェクトではない", line);

            var @event = GetString(root, "event");
            if (string.IsNullOrWhiteSpace(@event)) return Unknown("event が無い", line);

            return @event switch
            {
                "init" => ReadInit(root, line),
                "step_update" => ReadStepUpdate(root, line),
                "result" => ReadFinished(root, line),
                _ => Unknown($"未知のイベント event={@event}", line),
            };
        }
        catch (JsonException) { return Unknown("壊れた JSON", line); }
        catch (Exception exception) { return Unknown($"読取失敗: {exception.GetType().Name}", line); }
    }

    private static AntigravityEvent ReadInit(JsonElement root, string line)
    {
        if (!TryGetObject(root, "init", out var init)) return Unknown("init に init が無い", line);
        var tools = new List<string>();
        if (init.TryGetProperty("tools", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in values.EnumerateArray())
                if (value.ValueKind == JsonValueKind.String && value.GetString() is { } tool) tools.Add(tool);
        }
        return new AntigravityEvent.Init(GetString(init, "cwd"), GetString(init, "permission_mode"), tools);
    }

    private static AntigravityEvent ReadStepUpdate(JsonElement root, string line)
    {
        if (!TryGetObject(root, "step_update", out var update)) return Unknown("step_update に step_update が無い", line);
        int? index = update.TryGetProperty("step_index", out var value) && value.TryGetInt32(out var parsed) ? parsed : null;
        return new AntigravityEvent.StepUpdate(index, GetString(update, "step_type"), GetString(update, "state"), GetString(update, "tool_name"));
    }

    private static AntigravityEvent ReadFinished(JsonElement root, string line)
    {
        if (!TryGetObject(root, "result", out var result)) return Unknown("result に result が無い", line);
        var denied = new List<AntigravityDeniedAction>();
        if (result.TryGetProperty("denied_actions", out var actions) && actions.ValueKind == JsonValueKind.Array)
        {
            foreach (var action in actions.EnumerateArray())
            {
                if (action.ValueKind == JsonValueKind.Object && GetString(action, "action") is { } name)
                    denied.Add(new AntigravityDeniedAction(name, GetString(action, "display_name")));
            }
        }
        return new AntigravityEvent.Finished(GetString(result, "status"), GetString(result, "response") ?? string.Empty, denied);
    }

    private static bool TryGetObject(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static AntigravityEvent.Unknown Unknown(string reason, string raw) => new(reason, raw.Length <= 200 ? raw : raw[..200]);
}

public static class AntigravityTurnOutcome
{
    public static OutcomeSignals ToSignals(AntigravityEvent.Finished finished, bool sawStepError)
    {
        ArgumentNullException.ThrowIfNull(finished);
        return new OutcomeSignals(
            Protocol: LayerObservation.Ok,
            Tool: sawStepError ? LayerObservation.Failed : LayerObservation.NotObserved,
            Payload: string.Equals(finished.Status, "SUCCESS", StringComparison.Ordinal) && finished.DeniedActions.Count == 0
                ? LayerObservation.Ok : LayerObservation.Failed,
            DeniedActions: finished.DeniedActions.Select(action => action.DisplayName ?? action.Action).ToArray());
    }
}
