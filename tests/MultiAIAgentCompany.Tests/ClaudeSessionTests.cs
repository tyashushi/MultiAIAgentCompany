using System.Text.Json;
using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Agents.ClaudeCode;
using MultiAIAgentCompany.Core.Sessions;
using MultiAIAgentCompany.Core.Status;
using Xunit;

namespace MultiAIAgentCompany.Tests;

public sealed class ClaudeSessionTests
{
    [Fact]
    public async Task allowは承認を一度だけ発火し_Bashを題にする()
    {
        var channel = new FakeChannel(ReadFixture("allow.stdout.jsonl"));
        await using var session = new ClaudeCodeStructuredSession(channel, "engineering");
        var approvals = new List<ApprovalRequest>();
        session.ApprovalRequested += (_, request) => approvals.Add(request);

        channel.Release();
        await channel.Completed;

        var approval = Assert.Single(approvals);
        Assert.Equal("Bash", approval.Title);
    }

    [Theory]
    [InlineData("allow.stdout.jsonl", true)]
    [InlineData("deny.stdout.jsonl", false)]
    public async Task resultは拒否記録も含めて判定する(string fixture, bool expected)
    {
        var channel = new FakeChannel(ReadFixture(fixture));
        await using var session = new ClaudeCodeStructuredSession(channel, "engineering");
        var verdicts = new List<OutcomeVerdict>();
        session.TurnFinished += (_, verdict) => verdicts.Add(verdict);

        channel.Release();
        await channel.Completed;

        Assert.Equal(expected, Assert.Single(verdicts).Succeeded);
    }

    [Fact]
    public async Task 承認応答とユーザーメッセージはfixtureとJSONとして一致する()
    {
        var channel = new FakeChannel(ReadFixture("allow.stdout.jsonl"));
        await using var session = new ClaudeCodeStructuredSession(channel, "engineering");
        var approval = new TaskCompletionSource<ApprovalRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ApprovalRequested += (_, request) => approval.TrySetResult(request);

        channel.Release();
        var request = await approval.Task;
        await session.SendUserMessageAsync("Run exactly this shell command and nothing else: echo HELLO > /Volumes/SSD/Developer/MultiAIAgentCompany/spikes/fixtures/claude/ws-allow/hello.txt", CancellationToken.None);
        await session.RespondAsync(request, request.AvailableDecisions.Single(decision => decision.Id == "allow"), null, CancellationToken.None);

        var expected = ReadFixture("allow.stdin.jsonl").ToArray();
        AssertJsonEqual(expected[0], channel.Written[0]);
        AssertJsonEqual(expected[1], channel.Written[1]);
    }

    [Fact]
    public async Task 購読側が例外を投げても読み取りは止まらない()
    {
        // 止まると stdout が排出されず、パイプが詰まって子が黙って停止する（設計 §5）。
        var channel = new FakeChannel(ReadFixture("allow.stdout.jsonl"));
        await using var session = new ClaudeCodeStructuredSession(channel, "engineering");
        var observed = new List<string>();
        var verdicts = new List<OutcomeVerdict>();
        session.ApprovalRequested += (_, _) => throw new InvalidOperationException("購読側の不具合");
        session.Observed += (_, evidence) => observed.Add(evidence.RedactedSummary);
        session.TurnFinished += (_, verdict) => verdicts.Add(verdict);

        channel.Release();
        await channel.Completed;

        // 承認の後ろにある result まで届いている。
        Assert.Single(verdicts);
        // 握りつぶさず、例外が出たことは観測に残る。ただしメッセージは入れない。
        Assert.Contains(observed, summary => summary.Contains("InvalidOperationException"));
        Assert.DoesNotContain(observed, summary => summary.Contains("購読側の不具合"));
    }

    [Fact]
    public async Task 部門IDが空なら起動する前に弾く()
    {
        // 起動してから弾くと、セッションを返せないまま claude が残る（設計 §9）。
        var started = 0;
        var adapter = new ClaudeCodeAdapter((_, _, _, _) =>
        {
            started++;
            return Task.FromResult<IAgentProcessChannel>(new FakeChannel([]));
        });

        await Assert.ThrowsAnyAsync<ArgumentException>(() => adapter.StartAsync(
            new MultiAIAgentCompany.Core.Workspace.WorkspaceRef(Path.GetTempPath()),
            "   ",
            DriveMode.Structured,
            CancellationToken.None));

        Assert.Equal(0, started);
    }

    [Fact]
    public async Task 拒否は理由をそのままエージェントへ渡す()
    {
        // 拒否理由はモデルが読む。無いと agent は理由の分からないまま盲目的に再試行する。
        // ApprovalDecision（提示された選択肢）ではなく、その回の応答に属する引数。
        var channel = new FakeChannel(ReadFixture("deny.stdout.jsonl"));
        await using var session = new ClaudeCodeStructuredSession(channel, "engineering");
        var approval = new TaskCompletionSource<ApprovalRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ApprovalRequested += (_, request) => approval.TrySetResult(request);

        channel.Release();
        var request = await approval.Task;
        await session.RespondAsync(
            request,
            request.AvailableDecisions.Single(decision => decision.Id == "deny"),
            "captured: denied on purpose",
            CancellationToken.None);

        AssertJsonEqual(ReadFixture("deny.stdin.jsonl").ToArray()[1], Assert.Single(channel.Written));
    }

    [Fact]
    public async Task 提示していない決定は書き込まない()
    {
        var channel = new FakeChannel(ReadFixture("allow.stdout.jsonl"));
        await using var session = new ClaudeCodeStructuredSession(channel, "engineering");
        var approval = new TaskCompletionSource<ApprovalRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ApprovalRequested += (_, request) => approval.TrySetResult(request);

        channel.Release();
        var request = await approval.Task;
        await Assert.ThrowsAsync<ArgumentException>(() => session.RespondAsync(request, new ApprovalDecision("other", "その他"), null, CancellationToken.None));
        Assert.Empty(channel.Written);
    }

    [Fact]
    public async Task unknownの後もresultまで読み_内容をEvidenceに出さない()
    {
        const string secretLine = "{\"type\":\"future_event\",\"secret\":\"do-not-persist\"}";
        var result = ReadFixture("allow.stdout.jsonl").Single(line => line.Contains("\"type\":\"result\""));
        var channel = new FakeChannel([secretLine, result]);
        await using var session = new ClaudeCodeStructuredSession(channel, "engineering");
        var evidence = new List<Evidence>();
        var verdicts = new List<OutcomeVerdict>();
        session.Observed += (_, item) => evidence.Add(item);
        session.TurnFinished += (_, item) => verdicts.Add(item);

        channel.Release();
        await channel.Completed;

        Assert.True(Assert.Single(verdicts).Succeeded);
        Assert.DoesNotContain(evidence, item => item.RedactedSummary.Contains("do-not-persist", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StopAsyncは二度呼んでも壊れない()
    {
        var channel = new FakeChannel([]);
        await using var session = new ClaudeCodeStructuredSession(channel, "engineering");

        await session.StopAsync(CancellationToken.None);
        await session.StopAsync(CancellationToken.None);

        Assert.Equal(1, channel.StopCount);
    }

    private static IEnumerable<string> ReadFixture(string name) =>
        File.ReadLines(Path.Combine(AppContext.BaseDirectory, "fixtures", "claude", name));

    private static void AssertJsonEqual(string expected, string actual)
    {
        using var expectedDocument = JsonDocument.Parse(expected);
        using var actualDocument = JsonDocument.Parse(actual);
        Assert.True(JsonElement.DeepEquals(expectedDocument.RootElement, actualDocument.RootElement));
    }

    private sealed class FakeChannel(IEnumerable<string> lines) : IAgentProcessChannel
    {
        private readonly IReadOnlyList<string> _lines = lines.ToArray();
        private readonly TaskCompletionSource _start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Written { get; } = [];
        public int StopCount { get; private set; }
        public Task Completed => _completed.Task;
        public ProcessIdentity Identity { get; } = new(42, 0, 0, DateTimeOffset.UtcNow);
        public event EventHandler<int>? Exited;

        public void Release() => _start.TrySetResult();

        public async IAsyncEnumerable<string> ReadLinesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await _start.Task.WaitAsync(ct);
            foreach (var line in _lines)
            {
                ct.ThrowIfCancellationRequested();
                yield return line;
            }
            _completed.TrySetResult();
        }

        public Task WriteLineAsync(string line, CancellationToken ct)
        {
            Written.Add(line);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            StopCount++;
            _start.TrySetResult();
            _completed.TrySetResult();
            if (StopCount == 1) Exited?.Invoke(this, 0);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    [Fact]
    public async Task 発言はSpokeに出てObservedには出ない()
    {
        // §17-5 / §10: Observed は「秘密値を入れない要約」、Spoke は中身そのもの。
        // 橋渡しを完全には防げないので、**分かれていることをテストで固定する**。
        const string secret = "do-not-persist-marker";
        var line = "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + secret + "\"}]}}";
        var channel = new FakeChannel([line]);
        await using var session = new ClaudeCodeStructuredSession(channel, "engineering");
        var spoken = new List<string>();
        var observed = new List<string>();
        session.Spoke += (_, message) => spoken.Add(message.Text);
        session.Observed += (_, evidence) => observed.Add(evidence.RedactedSummary);

        channel.Release();
        await channel.Completed;

        Assert.Contains(secret, Assert.Single(spoken));
        Assert.DoesNotContain(observed, summary => summary.Contains(secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task thinkingとtool_useは発言にしない()
    {
        // 「発言」と「行動」を混ぜると、承認 UI や成否判定との境界が曖昧になる（§17-5）。
        var line = "{\"type\":\"assistant\",\"message\":{\"content\":["
            + "{\"type\":\"thinking\",\"thinking\":\"考え中\"},"
            + "{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{}},"
            + "{\"type\":\"text\",\"text\":\"これは発言\"}]}}";
        var channel = new FakeChannel([line]);
        await using var session = new ClaudeCodeStructuredSession(channel, "engineering");
        var spoken = new List<string>();
        session.Spoke += (_, message) => spoken.Add(message.Text);

        channel.Release();
        await channel.Completed;

        Assert.Equal("これは発言", Assert.Single(spoken));
    }

}
