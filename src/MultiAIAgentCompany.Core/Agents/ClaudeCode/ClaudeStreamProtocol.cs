using System.Text.Json;
using MultiAIAgentCompany.Core.Status;

namespace MultiAIAgentCompany.Core.Agents.ClaudeCode;

/// <summary>Claude Code stream-json の1行を表す、プロセス非依存のプロトコル型。</summary>
public abstract record ClaudeEvent
{
    public sealed record Init(string? Version, string? SessionId, string? Model,
        string? PermissionMode, IReadOnlyList<ClaudeMcpServer> McpServers) : ClaudeEvent;

    public sealed record ApprovalAsked(ClaudeApprovalRequest Request) : ClaudeEvent;

    /// <param name="Subtype">CLI が言う結末（<c>success</c> ほか）。</param>
    /// <param name="Denials">握りつぶされたツール実行。</param>
    /// <param name="IsError">
    /// <c>is_error</c>。<b>`subtype` とは別の手がかり</b>（設計 §13 の3層）——
    /// 片方だけを見ていると、**もう片方が失敗と言っているのに成功にしてしまう。**
    /// </param>
    /// <param name="ApiErrorStatus">
    /// <c>api_error_status</c>。**人間に見せる理由がここにしか無いことがある**
    /// （2026-09-09 に実機で踏んだ: 秘書が「API Error: 400 status code (no body)」と
    /// 発言だけして、アプリは理由を持てなかった）。
    /// </param>
    public sealed record TurnFinished(string? Subtype,
        IReadOnlyList<ClaudePermissionDenial> Denials,
        bool IsError = false,
        string? ApiErrorStatus = null) : ClaudeEvent;

    public sealed record Passthrough(string Type, string? Subtype) : ClaudeEvent;

    /// <summary>
    /// エージェントの発言。<b>ライブ表示専用</b>（設計 §17-5）。
    /// <c>text</c> ブロックだけで、<c>thinking</c> も <c>tool_use</c> も含まない。
    /// </summary>
    public sealed record AssistantSpoke(IReadOnlyList<string> TextBlocks) : ClaudeEvent;

    /// <summary>未知または壊れた行。RawFirst200 は永続する Evidence に流してはいけない。</summary>
    public sealed record Unknown(string Reason, string RawFirst200) : ClaudeEvent;
}

/// <summary>
/// <c>system/init</c> が申告する MCP サーバ1つ。設計 §13-5a。
/// </summary>
/// <remarks>
/// <b>名前だけにしない。</b> 実測（2026-09-06、2.1.261）で要素は
/// <c>{"name":"probe","status":"failed"}</c> というオブジェクトだった。
/// 名前だけを拾うと、<b>繋がらなかったサーバと、そもそも設定が無い状態が同じに見える。</b>
/// §13-5a は「MCP 未設定」を部門の状態として表示すると決めているので、
/// これを潰すと未設定でないものを未設定と表示することになる。
/// </remarks>
public sealed record ClaudeMcpServer(string Name, string? Status);

/// <summary>拒否されたツール操作。RawToolInputJson は突き合わせ専用で、要約へ流してはいけない。</summary>
public sealed record ClaudePermissionDenial(string ToolName, string? ToolUseId, string RawToolInputJson);

/// <summary>Claude が提示した権限設定の提案。ツール入力やパスを表示用要約に含めない。</summary>
public abstract record PermissionSuggestion(string? Destination)
{
    public sealed record AddRules(string? Destination, string? Behavior,
        IReadOnlyList<PermissionRule> Rules) : PermissionSuggestion(Destination)
    {
        public override string SafeSummary() =>
            $"{(Rules.Count == 1 ? Rules[0].ToolName : "ツール")} のルール{Rules.Count}件を常に許可（{DestinationLabel(Destination)}）";
    }

    public sealed record AddDirectories(string? Destination,
        IReadOnlyList<string> Directories) : PermissionSuggestion(Destination)
    {
        public override string SafeSummary() =>
            $"ディレクトリ{Directories.Count}件を作業対象に追加（{DestinationLabel(Destination)}）";
    }

    public sealed record Unrecognized(string? Destination, string Type, string RawJson)
        : PermissionSuggestion(Destination)
    {
        public override string SafeSummary() => $"未知の提案 type={Type}";
    }

    /// <summary>人間に見せてよい、入力・パスを含まない1行。</summary>
    public abstract string SafeSummary();

    private static string DestinationLabel(string? destination) =>
        string.IsNullOrWhiteSpace(destination) ? "宛先不明" : destination;
}

/// <param name="RuleContent">ツール入力そのもの。承認 UI だけが使い、永続しうる経路へ流さない。</param>
public sealed record PermissionRule(string ToolName, string RuleContent);

public sealed record ClaudeApprovalRequest(
    string RequestId,
    string ToolName,
    string? DisplayName,
    string? Description,
    string? BlockedPath,
    string ToolUseId,
    IReadOnlyList<PermissionSuggestion> PermissionSuggestions,
    string RawInputJson)
{
    /// <summary>
    /// ツールが触ろうとしている場所（設計 §35）。<b>読めなければ null。</b>
    /// </summary>
    /// <remarks>
    /// <c>BlockedPath</c> が無いツール（<c>Write</c> など）でも、
    /// 入力の <c>file_path</c> から分かる。<b>分からないなら自動承認しない</b>ので、
    /// ここで無理に埋めない。
    /// </remarks>
    private string? PathFromInput()
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(RawInputJson);
            if (document.RootElement.ValueKind is not System.Text.Json.JsonValueKind.Object)
            {
                return null;
            }

            foreach (var name in (string[])["file_path", "path", "notebook_path"])
            {
                if (document.RootElement.TryGetProperty(name, out var value)
                    && value.ValueKind is System.Text.Json.JsonValueKind.String)
                {
                    return value.GetString();
                }
            }

            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>共通の承認 UI に渡す。提案は安全な表示用要約だけを渡す。</summary>
    public ApprovalRequest ToApprovalRequest() => new(
        RequestId,
        ApprovalKind.Runtime,
        AgentKind.ClaudeCode,
        SessionId: null,
        TurnId: null,
        Title: DisplayName ?? ToolName,
        Details: Description ?? string.Empty,
        AvailableDecisions:
        [
            new ApprovalDecision("allow", "許可"),
            new ApprovalDecision("deny", "拒否"),
        ],
        SuggestedRules: PermissionSuggestions.Select(suggestion => suggestion.SafeSummary()).ToArray(),

        // **判定に使うものは、表示用と分けて渡す**（設計 §35）。
        ToolName: ToolName,
        TargetPath: BlockedPath ?? PathFromInput());
}

public static class ClaudeStreamReader
{
    public static ClaudeEvent ReadLine(string line)
    {
        if (line is null)
        {
            return new ClaudeEvent.Unknown("行が null", string.Empty);
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Unknown("JSON の根がオブジェクトではない", line);
            }

            var type = GetString(root, "type");
            if (string.IsNullOrWhiteSpace(type))
            {
                return Unknown("type が無い", line);
            }

            return type switch
            {
                "system" when GetString(root, "subtype") == "init" => ReadInit(root, line),
                "control_request" => ReadApproval(root, line),
                "result" => ReadResult(root, line),
                "assistant" when TryReadSpoken(root, out var spoken) => new ClaudeEvent.AssistantSpoke(spoken),
                "system" or "assistant" or "user" or "rate_limit_event" =>
                    new ClaudeEvent.Passthrough(type, GetString(root, "subtype")),
                _ => Unknown($"未知のイベント type={type}", line),
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

    /// <summary>
    /// <c>assistant</c> の <c>text</c> ブロックだけを取り出す（設計 §17-5）。
    /// <c>thinking</c> は発言ではない。<c>tool_use</c> は「行動」なので混ぜない ——
    /// 承認 UI や成否判定との境界が曖昧になる。
    /// </summary>
    private static bool TryReadSpoken(JsonElement root, out IReadOnlyList<string> textBlocks)
    {
        var blocks = new List<string>();
        if (root.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object
                    && GetString(block, "type") == "text"
                    && GetString(block, "text") is { Length: > 0 } text)
                {
                    blocks.Add(text);
                }
            }
        }

        textBlocks = blocks;
        return blocks.Count > 0;
    }

    private static ClaudeEvent ReadInit(JsonElement root, string line)
    {
        var mcpServers = new List<ClaudeMcpServer>();
        if (root.TryGetProperty("mcp_servers", out var servers) && servers.ValueKind == JsonValueKind.Array)
        {
            foreach (var server in servers.EnumerateArray())
            {
                // 実測はオブジェクト（name/status）。将来 文字列だけになっても取りこぼさない。
                if (server.ValueKind == JsonValueKind.Object && GetString(server, "name") is { } objectName)
                {
                    mcpServers.Add(new ClaudeMcpServer(objectName, GetString(server, "status")));
                }
                else if (server.ValueKind == JsonValueKind.String && server.GetString() is { } stringName)
                {
                    mcpServers.Add(new ClaudeMcpServer(stringName, null));
                }
            }
        }

        return new ClaudeEvent.Init(GetString(root, "claude_code_version"), GetString(root, "session_id"),
            GetString(root, "model"), GetString(root, "permissionMode"), mcpServers);
    }

    private static ClaudeEvent ReadApproval(JsonElement root, string line)
    {
        if (!root.TryGetProperty("request", out var request) || request.ValueKind != JsonValueKind.Object)
        {
            return Unknown("control_request に request が無い", line);
        }

        // §13-1 で往復が閉じたのは subtype=can_use_tool。それ以外の control_request は
        // 形が違ううえ、応答を待っている可能性がある。推定して承認要求に見立てない。
        var subtype = GetString(request, "subtype");
        if (subtype != "can_use_tool")
        {
            return Unknown($"未知の control_request subtype={subtype ?? "(無し)"}", line);
        }

        var requestId = GetString(root, "request_id");
        var toolName = GetString(request, "tool_name");
        var toolUseId = GetString(request, "tool_use_id");
        if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(toolName) || string.IsNullOrWhiteSpace(toolUseId) ||
            !request.TryGetProperty("input", out var input))
        {
            return Unknown("control_request の必須キーが無い", line);
        }

        return new ClaudeEvent.ApprovalAsked(new ClaudeApprovalRequest(
            requestId, toolName, GetString(request, "display_name"), GetString(request, "description"),
            GetString(request, "blocked_path"), toolUseId, ReadSuggestions(request), input.GetRawText()));
    }

    private static ClaudeEvent ReadResult(JsonElement root, string line)
    {
        var denials = new List<ClaudePermissionDenial>();
        if (root.TryGetProperty("permission_denials", out var denialArray) && denialArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var denial in denialArray.EnumerateArray())
            {
                if (denial.ValueKind != JsonValueKind.Object)
                {
                    return Unknown("result.permission_denials の要素がオブジェクトではない", line);
                }

                var toolName = GetString(denial, "tool_name");
                if (string.IsNullOrWhiteSpace(toolName))
                {
                    return Unknown("result.permission_denials に tool_name が無い", line);
                }

                denials.Add(new ClaudePermissionDenial(toolName, GetString(denial, "tool_use_id"),
                    denial.TryGetProperty("tool_input", out var input) ? input.GetRawText() : "null"));
            }
        }

        // **`subtype` だけを見ない**（設計 §13 の3層）。`is_error` と `api_error_status` は
        // 別の手がかりで、**API エラーのときは理由がここにしか無い。**
        var isError = root.TryGetProperty("is_error", out var errorFlag)
            && errorFlag.ValueKind is JsonValueKind.True;
        var apiErrorStatus = root.TryGetProperty("api_error_status", out var status)
            && status.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? status.ToString()
            : null;

        return new ClaudeEvent.TurnFinished(GetString(root, "subtype"), denials, isError, apiErrorStatus);
    }

    private static IReadOnlyList<PermissionSuggestion> ReadSuggestions(JsonElement request)
    {
        var suggestions = new List<PermissionSuggestion>();
        if (!request.TryGetProperty("permission_suggestions", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return suggestions;
        }

        foreach (var suggestion in array.EnumerateArray())
        {
            if (suggestion.ValueKind != JsonValueKind.Object)
            {
                suggestions.Add(new PermissionSuggestion.Unrecognized(null, "(非オブジェクト)", suggestion.GetRawText()));
                continue;
            }

            var destination = GetString(suggestion, "destination");
            var type = GetString(suggestion, "type") ?? "(type 不明)";
            switch (type)
            {
                case "addRules":
                    var rules = new List<PermissionRule>();
                    if (suggestion.TryGetProperty("rules", out var ruleArray) && ruleArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var rule in ruleArray.EnumerateArray())
                        {
                            if (rule.ValueKind == JsonValueKind.Object)
                            {
                                rules.Add(new PermissionRule(GetString(rule, "toolName") ?? string.Empty,
                                    GetString(rule, "ruleContent") ?? string.Empty));
                            }
                        }
                    }
                    suggestions.Add(new PermissionSuggestion.AddRules(destination, GetString(suggestion, "behavior"), rules));
                    break;
                case "addDirectories":
                    var directories = new List<string>();
                    if (suggestion.TryGetProperty("directories", out var directoryArray) && directoryArray.ValueKind == JsonValueKind.Array)
                    {
                        directories.AddRange(directoryArray.EnumerateArray()
                            .Where(directory => directory.ValueKind == JsonValueKind.String)
                            .Select(directory => directory.GetString()!));
                    }
                    suggestions.Add(new PermissionSuggestion.AddDirectories(destination, directories));
                    break;
                default:
                    suggestions.Add(new PermissionSuggestion.Unrecognized(destination, type, suggestion.GetRawText()));
                    break;
            }
        }

        return suggestions;
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static ClaudeEvent.Unknown Unknown(string reason, string raw) =>
        new(reason, raw.Length <= 200 ? raw : raw[..200]);
}

public static class ClaudeControlResponse
{
    public static string Build(ClaudeApprovalRequest request, string decisionId, string? denyMessage = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (decisionId is not ("allow" or "deny"))
        {
            throw new ArgumentException("Claude が提示していない決定です", nameof(decisionId));
        }

        using var input = JsonDocument.Parse(request.RawInputJson);
        var response = decisionId == "allow"
            ? new Dictionary<string, object?> { ["behavior"] = "allow", ["updatedInput"] = input.RootElement.Clone() }
            : new Dictionary<string, object?> { ["behavior"] = "deny", ["message"] = denyMessage };

        return JsonSerializer.Serialize(new
        {
            type = "control_response",
            response = new
            {
                subtype = "success",
                request_id = request.RequestId,
                response,
            },
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    }
}

public static class ClaudeTurnOutcome
{
    /// <summary>
    /// 人間に見せる、失敗の理由（設計 §13）。<b>分かっている手がかりを全部並べる。</b>
    /// </summary>
    /// <remarks>
    /// <b>「うまくいかなかった」だけでは動けない</b>（2026-09-09 に実機で踏んだ）——
    /// 秘書が API エラーで止まったとき、画面に出たのは発言の1行だけで、
    /// <c>api_error_status</c> はアプリの中で捨てられていた。
    /// </remarks>
    public static string? DescribeFailure(ClaudeEvent.TurnFinished finished)
    {
        ArgumentNullException.ThrowIfNull(finished);

        var parts = new List<string>();
        if (finished.ApiErrorStatus is { Length: > 0 } status)
        {
            parts.Add($"API エラー {status}");
        }

        if (finished.Subtype is { Length: > 0 } subtype
            && !string.Equals(subtype, "success", StringComparison.Ordinal))
        {
            parts.Add($"結末 {subtype}");
        }

        if (finished.IsError)
        {
            parts.Add("is_error");
        }

        return parts.Count is 0 ? null : string.Join(" / ", parts);
    }

    public static OutcomeSignals ToSignals(ClaudeEvent.TurnFinished finished)
    {
        ArgumentNullException.ThrowIfNull(finished);
        // **どちらか一方でも失敗と言っていたら失敗**（設計 §13 の3層 / §14-4）。
        // `subtype` だけを見ていたので、`is_error` を見落としていた（2026-09-09）。
        var succeeded = string.Equals(finished.Subtype, "success", StringComparison.Ordinal)
            && !finished.IsError;

        return new OutcomeSignals(
            Protocol: LayerObservation.Ok,
            Tool: LayerObservation.NotApplicable,
            Payload: succeeded ? LayerObservation.Ok : LayerObservation.Failed,
            DeniedActions: finished.Denials.Select(denial => denial.ToolName).ToArray());
    }
}
