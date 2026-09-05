using System.Text.Json;
using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Agents.ClaudeCode;
using MultiAIAgentCompany.Core.Status;
using Xunit;

namespace MultiAIAgentCompany.Tests;

public sealed class ClaudeStreamTests
{
    [Theory]
    [InlineData("allow.stdout.jsonl", 12)]
    [InlineData("deny.stdout.jsonl", 15)]
    public void 実測_stdout_の全行は_Unknown_にならない(string fileName, int expectedCount)
    {
        var events = ReadFixture(fileName).Select(ClaudeStreamReader.ReadLine).ToArray();

        Assert.Equal(expectedCount, events.Length);
        Assert.DoesNotContain(events, @event => @event is ClaudeEvent.Unknown);
    }

    [Fact]
    public void 壊れた行は例外ではなく_Unknown_になる() =>
        Assert.IsType<ClaudeEvent.Unknown>(ClaudeStreamReader.ReadLine("{"));

    [Fact]
    public void 知らない_type_は理由つき_Unknown_になる()
    {
        var unknown = Assert.IsType<ClaudeEvent.Unknown>(ClaudeStreamReader.ReadLine("{\"type\":\"future_event\"}"));

        Assert.Contains("future_event", unknown.Reason);
    }

    [Fact]
    public void init_から観測済みのメタデータを読む()
    {
        var init = Assert.IsType<ClaudeEvent.Init>(ClaudeStreamReader.ReadLine(ReadFixture("allow.stdout.jsonl").First()));

        Assert.Equal("2.1.261", init.Version);
        Assert.NotNull(init.SessionId);
        Assert.Equal("claude-sonnet-5", init.Model);
        Assert.Equal("default", init.PermissionMode);
        Assert.Empty(init.McpServers);
    }

    [Fact]
    public void allow_の承認要求と権限提案を構造化して読む()
    {
        var request = AllowRequest();

        Assert.Equal("Bash", request.ToolName);
        Assert.Equal(2, request.PermissionSuggestions.Count);
        var addRules = Assert.IsType<PermissionSuggestion.AddRules>(request.PermissionSuggestions[0]);
        Assert.Equal("Bash", addRules.Rules[0].ToolName);
        Assert.Equal("allow", addRules.Behavior);
        Assert.Equal("localSettings", addRules.Destination);
        var addDirectories = Assert.IsType<PermissionSuggestion.AddDirectories>(request.PermissionSuggestions[1]);
        Assert.Single(addDirectories.Directories);
        Assert.Equal("session", addDirectories.Destination);
    }

    [Fact]
    public void 未知の提案_type_は_RawJson_を保持する()
    {
        // fixture の control_request 行の type だけを差し替え、fixture に無い未知 type を検証する。
        var line = ReadFixture("allow.stdout.jsonl").First(line => line.Contains("\"type\":\"control_request\""));
        var changed = line.Replace("\"type\":\"addRules\"", "\"type\":\"futureRules\"");
        var request = Assert.IsType<ClaudeEvent.ApprovalAsked>(ClaudeStreamReader.ReadLine(changed)).Request;
        var unknown = Assert.IsType<PermissionSuggestion.Unrecognized>(request.PermissionSuggestions[0]);

        Assert.Equal("futureRules", unknown.Type);
        Assert.Contains("futureRules", unknown.RawJson);
    }

    [Fact]
    public void 安全な提案要約は_ルール入力やディレクトリパスを含まない()
    {
        var request = AllowRequest();
        var rawRuleContent = ((PermissionSuggestion.AddRules)request.PermissionSuggestions[0]).Rules[0].RuleContent;
        var directory = ((PermissionSuggestion.AddDirectories)request.PermissionSuggestions[1]).Directories[0];
        var summaries = request.PermissionSuggestions.Select(suggestion => suggestion.SafeSummary());

        Assert.DoesNotContain(summaries, summary => summary.Contains(rawRuleContent, StringComparison.Ordinal));
        Assert.DoesNotContain(summaries, summary => summary.Contains(directory, StringComparison.Ordinal));
    }

    [Fact]
    public void 共通承認要求は_allow_と_deny_だけを提示する()
    {
        var approval = AllowRequest().ToApprovalRequest();

        Assert.True(approval.Offers("allow"));
        Assert.True(approval.Offers("deny"));
        Assert.False(approval.Offers("acceptForSession"));
    }

    [Theory]
    [InlineData("allow", "allow.stdin.jsonl")]
    [InlineData("deny", "deny.stdin.jsonl")]
    public void control_response_は_fixture_の承認応答と_JSONとして一致する(string decision, string stdinFixture)
    {
        var request = decision == "allow" ? AllowRequest() : DenyRequest();
        var message = decision == "deny" ? "captured: denied on purpose" : null;

        AssertJsonEqual(ReadFixture(stdinFixture).Skip(1).First(), ClaudeControlResponse.Build(request, decision, message));
    }

    [Fact]
    public void 提示していない決定は組み立てない() =>
        Assert.Throws<ArgumentException>(() => ClaudeControlResponse.Build(AllowRequest(), "acceptForSession"));

    [Fact]
    public void allow_result_は成功で_deny_result_は失敗になる()
    {
        var allow = ClaudeTurnOutcome.ToSignals(Result("allow.stdout.jsonl"));
        var deny = ClaudeTurnOutcome.ToSignals(Result("deny.stdout.jsonl"));

        Assert.True(allow.Judge(OutcomeRequirement.For(AgentKind.ClaudeCode)).Succeeded);
        Assert.False(deny.Judge(OutcomeRequirement.For(AgentKind.ClaudeCode)).Succeeded);
    }

    [Fact]
    public void deny_result_の_subtype_も_success_である() =>
        Assert.Equal("success", Result("deny.stdout.jsonl").Subtype);

    private static ClaudeApprovalRequest AllowRequest() => Request("allow.stdout.jsonl");
    private static ClaudeApprovalRequest DenyRequest() => Request("deny.stdout.jsonl");
    private static ClaudeApprovalRequest Request(string fixture) =>
        Assert.IsType<ClaudeEvent.ApprovalAsked>(ReadFixture(fixture).Select(ClaudeStreamReader.ReadLine)
            .Single(@event => @event is ClaudeEvent.ApprovalAsked)).Request;
    private static ClaudeEvent.TurnFinished Result(string fixture) =>
        Assert.IsType<ClaudeEvent.TurnFinished>(ReadFixture(fixture).Select(ClaudeStreamReader.ReadLine)
            .Single(@event => @event is ClaudeEvent.TurnFinished));
    private static IEnumerable<string> ReadFixture(string fileName) =>
        File.ReadLines(Path.Combine(AppContext.BaseDirectory, "fixtures", "claude", fileName));

    private static void AssertJsonEqual(string expected, string actual)
    {
        using var expectedDocument = JsonDocument.Parse(expected);
        using var actualDocument = JsonDocument.Parse(actual);
        Assert.True(JsonElement.DeepEquals(expectedDocument.RootElement, actualDocument.RootElement));
    }

    [Fact]
    public void MCPが設定されているinitはサーバ名と状態を落とさない()
    {
        // 実測 fixture。要素は {"name":"probe","status":"failed"} というオブジェクト。
        // 名前だけを拾う実装だと空になり、「繋がらなかった」と「未設定」が同じに見えた（§13-5a）。
        var line = ReadFixture("init-with-mcp.stdout.jsonl").First();

        var init = Assert.IsType<ClaudeEvent.Init>(ClaudeStreamReader.ReadLine(line));

        var server = Assert.Single(init.McpServers);
        Assert.Equal("probe", server.Name);
        Assert.Equal("failed", server.Status);
    }

    [Fact]
    public void 未知のcontrol_request_subtypeは承認要求に見立てない()
    {
        // §13-1 で往復が閉じたのは can_use_tool だけ。他は形が違い、応答待ちの可能性もある。
        var line = """{"type":"control_request","request_id":"r1","request":{"subtype":"initialize"}}""";

        var unknown = Assert.IsType<ClaudeEvent.Unknown>(ClaudeStreamReader.ReadLine(line));

        Assert.Contains("initialize", unknown.Reason);
    }
}
