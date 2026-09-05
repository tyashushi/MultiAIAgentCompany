using System.Text.Json;
using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Agents.CodexCli;
using MultiAIAgentCompany.Core.Status;
using Xunit;

namespace MultiAIAgentCompany.Tests;

public sealed class CodexProtocolTests
{
    [Theory]
    [InlineData("accept.stdout.jsonl", 34)]
    [InlineData("cancel.stdout.jsonl", 40)]
    public void 実測_stdout_の全行は_Unknown_にならない(string fileName, int expectedCount)
    {
        var messages = ReadFixture(fileName).Select(CodexMessageReader.ReadLine).ToArray();
        Assert.Equal(expectedCount, messages.Length);
        Assert.DoesNotContain(messages, message => message is CodexMessage.Unknown);
    }

    [Fact]
    public void 壊れた行は例外ではなく_Unknown_になる() =>
        Assert.IsType<CodexMessage.Unknown>(CodexMessageReader.ReadLine("{"));

    [Fact]
    public void 未知_method_の通知とサーバ要求を区別する()
    {
        var notificationLine = ReadFixture("accept.stdout.jsonl").First(line => line.Contains("\"thread/started\""))
            .Replace("thread/started", "future/notification");
        var requestLine = ApprovalLine("accept.stdout.jsonl").Replace("item/commandExecution/requestApproval", "future/request");

        Assert.IsType<CodexMessage.Notification>(CodexMessageReader.ReadLine(notificationLine));
        Assert.IsType<CodexMessage.UnknownServerRequest>(CodexMessageReader.ReadLine(requestLine));
    }

    [Fact]
    public void activeFlags_の3形を区別する()
    {
        var statuses = ReadFixture("accept.stdout.jsonl").Select(CodexMessageReader.ReadLine)
            .OfType<CodexMessage.ThreadStatusChanged>().Select(message => message.WaitingOnApproval).Distinct().ToArray();
        Assert.Contains(true, statuses);
        Assert.Contains(false, statuses);
        Assert.Contains(null, statuses);
    }

    [Fact]
    public void 承認要求は提示された決定と識別子を保つ()
    {
        var request = Request("accept.stdout.jsonl");
        Assert.Equal(["accept", "acceptWithExecpolicyAmendment", "cancel"], request.AvailableDecisions.Select(x => x.Id));
        Assert.DoesNotContain(request.AvailableDecisions, decision => decision.Id == "decline");
        Assert.NotNull(request.AvailableDecisions[1].RawJson);

        var approval = request.ToApprovalRequest();
        Assert.Equal(request.ThreadId, approval.SessionId);
        Assert.Equal(request.TurnId, approval.TurnId);
        Assert.DoesNotContain(request.Command, approval.Details, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("accept", "accept.stdin.jsonl")]
    [InlineData("cancel", "cancel.stdin.jsonl")]
    public void 承認応答は_fixture_と_JSONとして一致する(string decision, string stdinFixture) =>
        AssertJsonEqual(ReadFixture(stdinFixture).Last(), CodexApprovalResponse.Build(Request(decision + ".stdout.jsonl"), decision));

    [Fact]
    public void 提示していない決定は組み立てない() =>
        Assert.Throws<ArgumentException>(() => CodexApprovalResponse.Build(Request("accept.stdout.jsonl"), "decline"));

    [Fact]
    public void accept_は成功で_cancel_は失敗になる()
    {
        var accept = CodexTurnOutcome.ToSignals(TurnStatus("accept.stdout.jsonl"), CommandStatus("accept.stdout.jsonl"));
        var cancel = CodexTurnOutcome.ToSignals(TurnStatus("cancel.stdout.jsonl"), CommandStatus("cancel.stdout.jsonl"));
        Assert.True(accept.Judge(OutcomeRequirement.For(AgentKind.CodexCli)).Succeeded);
        Assert.False(cancel.Judge(OutcomeRequirement.For(AgentKind.CodexCli)).Succeeded);
    }

    [Fact]
    public void status_の無い_item_completed_を_null_として読む() =>
        Assert.Contains(ReadFixture("accept.stdout.jsonl").Select(CodexMessageReader.ReadLine).OfType<CodexMessage.ItemCompleted>(),
            message => message.Status is null);

    private static CodexApprovalRequest Request(string fixture) =>
        Assert.IsType<CodexMessage.ApprovalAsked>(CodexMessageReader.ReadLine(ApprovalLine(fixture))).Request;
    private static string ApprovalLine(string fixture) => ReadFixture(fixture)
        .Single(line => line.Contains("\"item/commandExecution/requestApproval\""));
    private static string? TurnStatus(string fixture) => ReadFixture(fixture).Select(CodexMessageReader.ReadLine)
        .OfType<CodexMessage.TurnFinished>().Single().Status;
    private static string? CommandStatus(string fixture) => ReadFixture(fixture).Select(CodexMessageReader.ReadLine)
        .OfType<CodexMessage.ItemCompleted>().Single(message => message.Status is "completed" or "declined").Status;
    private static IEnumerable<string> ReadFixture(string fileName) =>
        File.ReadLines(Path.Combine(AppContext.BaseDirectory, "fixtures", "codex", fileName));
    private static void AssertJsonEqual(string expected, string actual)
    {
        using var expectedDocument = JsonDocument.Parse(expected);
        using var actualDocument = JsonDocument.Parse(actual);
        Assert.True(JsonElement.DeepEquals(expectedDocument.RootElement, actualDocument.RootElement));
    }

    [Fact]
    public void オブジェクト形式の決定は提示されたものをそのまま返す()
    {
        // 実測（§13-2 追記2）: 提示された object をそのまま返すと往復が閉じる。
        // 形を作り直すと通らない。fixture はその往復を実際に記録したもの。
        var request = Request("acceptWithExecpolicyAmendment.stdout.jsonl");

        var built = CodexApprovalResponse.Build(request, "acceptWithExecpolicyAmendment");

        AssertJsonEqual(ReadFixture("acceptWithExecpolicyAmendment.stdin.jsonl").Last(), built);
    }

    [Fact]
    public void 知らないサーバ要求にはJSON_RPCのerrorを返す()
    {
        // id を持つ要求に何も返さないと turn が止まる。かといって decision を作ると、
        // 理解していない質問に答えをでっち上げることになる。
        using var document = System.Text.Json.JsonDocument.Parse(
            CodexApprovalResponse.BuildMethodNotFound(7, "future/method"));

        var root = document.RootElement;
        Assert.Equal(7, root.GetProperty("id").GetInt64());
        Assert.Equal(-32601, root.GetProperty("error").GetProperty("code").GetInt32());
        Assert.False(root.TryGetProperty("result", out _));
    }
}
