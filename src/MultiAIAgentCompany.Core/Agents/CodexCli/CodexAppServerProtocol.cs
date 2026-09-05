using System.Text.Json;
using MultiAIAgentCompany.Core.Status;

namespace MultiAIAgentCompany.Core.Agents.CodexCli;

/// <summary>Codex app-server の JSON-RPC 1行を表す、プロセス非依存のプロトコル型。</summary>
public abstract record CodexMessage
{
    public sealed record Response(long Id, string? ErrorJson) : CodexMessage;
    public sealed record ApprovalAsked(long Id, CodexApprovalRequest Request) : CodexMessage;
    public sealed record UnknownServerRequest(long Id, string Method, string RawFirst200) : CodexMessage;
    public sealed record ThreadStatusChanged(bool? WaitingOnApproval) : CodexMessage;
    public sealed record ItemCompleted(string? ItemId, string? Status) : CodexMessage;
    public sealed record TurnFinished(string? Status) : CodexMessage;
    public sealed record McpServerStatus(string? Name, string? Status, string? FailureReason) : CodexMessage;
    public sealed record Notification(string Method) : CodexMessage;
    public sealed record Unknown(string Reason, string RawFirst200) : CodexMessage;
}

public sealed record CodexApprovalRequest(
    long RpcId,
    string? Kind,
    string? ThreadId,
    string? TurnId,
    string? ItemId,
    string? Reason,
    string Command,
    string? Cwd,
    IReadOnlyList<CodexDecision> AvailableDecisions)
{
    /// <summary>共通 UI へ渡す。コマンド本体は永続しうる Details へ流さない。</summary>
    public ApprovalRequest ToApprovalRequest() => new(
        RpcId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ApprovalKind.Runtime,
        AgentKind.CodexCli,
        ThreadId,
        TurnId,
        Reason ?? Kind ?? string.Empty,
        Details: string.Empty,
        AvailableDecisions.Select(decision => new ApprovalDecision(decision.Id, decision.Id, decision.RawJson)).ToArray(),
        SuggestedRules: []);
}

/// <summary>Codex が提示した決定。オブジェクト形式は値を含む生 JSON を保持する。</summary>
public sealed record CodexDecision(string Id, string? RawJson);

public static class CodexMessageReader
{
    /// <summary>例外を投げない。壊れた行は Unknown にする。</summary>
    public static CodexMessage ReadLine(string line)
    {
        if (line is null)
        {
            return Unknown("行が null", string.Empty);
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Unknown("JSON の根がオブジェクトではない", line);
            }

            var hasId = TryGetLong(root, "id", out var id);
            var method = GetString(root, "method");
            if (method is null)
            {
                return hasId
                    ? new CodexMessage.Response(id, GetRawJsonOrNull(root, "error"))
                    : Unknown("id も method も無い", line);
            }

            if (hasId)
            {
                return method == "item/commandExecution/requestApproval"
                    ? ReadApproval(id, root, line)
                    : new CodexMessage.UnknownServerRequest(id, method, First200(line));
            }

            return method switch
            {
                "thread/status/changed" => ReadThreadStatus(root),
                "item/completed" => ReadItemCompleted(root),
                "turn/completed" => ReadTurnCompleted(root),
                "mcpServer/startupStatus/updated" => ReadMcpServerStatus(root),
                _ => new CodexMessage.Notification(method),
            };
        }
        catch (JsonException)
        {
            return Unknown("壊れた JSON", line);
        }
        catch (Exception exception)
        {
            return Unknown($"読取失敗: {exception.GetType().Name}", line);
        }
    }

    private static CodexMessage ReadApproval(long id, JsonElement root, string line)
    {
        if (!root.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
        {
            return Unknown("承認要求の params がオブジェクトではない", line);
        }

        var command = GetString(parameters, "command");
        if (command is null || !parameters.TryGetProperty("availableDecisions", out var decisions)
            || decisions.ValueKind != JsonValueKind.Array)
        {
            return Unknown("承認要求に command または availableDecisions が無い", line);
        }

        var available = new List<CodexDecision>();
        foreach (var decision in decisions.EnumerateArray())
        {
            if (decision.ValueKind == JsonValueKind.String && decision.GetString() is { } decisionId)
            {
                available.Add(new CodexDecision(decisionId, null));
                continue;
            }

            if (decision.ValueKind == JsonValueKind.Object && decision.EnumerateObject().ToArray() is [var property])
            {
                available.Add(new CodexDecision(property.Name, decision.GetRawText()));
                continue;
            }

            return Unknown("承認要求の availableDecisions に未観測の形がある", line);
        }

        return new CodexMessage.ApprovalAsked(id, new CodexApprovalRequest(
            id, GetString(parameters, "kind"), GetString(parameters, "threadId"), GetString(parameters, "turnId"),
            GetString(parameters, "itemId"), GetString(parameters, "reason"), command, GetString(parameters, "cwd"), available));
    }

    private static CodexMessage ReadThreadStatus(JsonElement root)
    {
        if (!TryGetObject(root, "params", out var parameters) || !TryGetObject(parameters, "status", out var status)
            || !status.TryGetProperty("activeFlags", out var flags) || flags.ValueKind == JsonValueKind.Null)
        {
            return new CodexMessage.ThreadStatusChanged(null);
        }

        if (flags.ValueKind != JsonValueKind.Array)
        {
            return new CodexMessage.ThreadStatusChanged(null);
        }

        return new CodexMessage.ThreadStatusChanged(flags.EnumerateArray()
            .Any(flag => flag.ValueKind == JsonValueKind.String && flag.GetString() == "waitingOnApproval"));
    }

    private static CodexMessage ReadItemCompleted(JsonElement root)
    {
        var item = TryGetObject(root, "params", out var parameters) && TryGetObject(parameters, "item", out var value)
            ? value : default;
        return new CodexMessage.ItemCompleted(GetString(item, "id"), GetString(item, "status"));
    }

    private static CodexMessage ReadTurnCompleted(JsonElement root)
    {
        var turn = TryGetObject(root, "params", out var parameters) && TryGetObject(parameters, "turn", out var value)
            ? value : default;
        return new CodexMessage.TurnFinished(GetString(turn, "status"));
    }

    private static CodexMessage ReadMcpServerStatus(JsonElement root)
    {
        var parameters = TryGetObject(root, "params", out var value) ? value : default;
        return new CodexMessage.McpServerStatus(GetString(parameters, "name"), GetString(parameters, "status"),
            GetString(parameters, "failureReason"));
    }

    private static bool TryGetObject(JsonElement element, string property, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out value)
            && value.ValueKind == JsonValueKind.Object;
    }

    private static bool TryGetLong(JsonElement element, string property, out long value)
    {
        value = default;
        return element.TryGetProperty(property, out var number) && number.ValueKind == JsonValueKind.Number
            && number.TryGetInt64(out value);
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string? GetRawJsonOrNull(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetRawText() : null;

    private static CodexMessage.Unknown Unknown(string reason, string raw) => new(reason, First200(raw));
    private static string First200(string raw) => raw.Length <= 200 ? raw : raw[..200];
}

public static class CodexApprovalResponse
{
    public static string Build(CodexApprovalRequest request, string decisionId)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.AvailableDecisions.Any(decision => string.Equals(decision.Id, decisionId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Codex が提示していない決定です", nameof(decisionId));
        }

        var decision = request.AvailableDecisions.First(d => string.Equals(d.Id, decisionId, StringComparison.Ordinal));

        // オブジェクト形式の決定は、提示されたものを**そのまま返す**（実測 §13-2 追記2）。
        // 形を作り直すと通らない。だから CodexDecision は RawJson を持っている。
        object payload = decision.RawJson is { } raw
            ? JsonSerializer.Deserialize<JsonElement>(raw)
            : decisionId;

        return JsonSerializer.Serialize(new { jsonrpc = "2.0", id = request.RpcId, result = new { decision = payload } });
    }

    /// <summary>
    /// 承認以外のサーバ要求への応答。<b>知らない method には JSON-RPC の error を返す。</b>
    /// </summary>
    /// <remarks>
    /// <c>id</c> を持つ要求に何も返さないと turn が止まる（実測 §13-2）。かといって
    /// <c>{"decision":"decline"}</c> のような答えを作ると、<b>理解していない質問に
    /// 答えをでっち上げる</b>ことになる —— 何を拒否したのかこちらが分かっていない。
    /// <para>
    /// JSON-RPC 自身が「知らない method」の答えを定義している（-32601）ので、
    /// これは推測ではなくプロトコルどおりの返答である。返したうえで、
    /// <b>知らない要求が来た事実は観測として人間に見せること。</b>
    /// </para>
    /// </remarks>
    public static string BuildMethodNotFound(long rpcId, string method) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = rpcId,
            error = new { code = -32601, message = $"Method not found: {method}" },
        });
}

public static class CodexTurnOutcome
{
    public static OutcomeSignals ToSignals(string? turnStatus, string? commandItemStatus) => new(
        Protocol: LayerObservation.Ok,
        Tool: commandItemStatus switch
        {
            "declined" => LayerObservation.Failed,
            "completed" => LayerObservation.Ok,
            _ => LayerObservation.NotObserved,
        },
        Payload: string.Equals(turnStatus, "completed", StringComparison.Ordinal)
            ? LayerObservation.Ok : LayerObservation.Failed);
}
