using System.Text.Json;
using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Agents.CodexCli;
using MultiAIAgentCompany.Core.Sessions;
using MultiAIAgentCompany.Core.Status;
using Xunit;

namespace MultiAIAgentCompany.Tests;

public sealed class CodexSessionTests
{
    [Fact]
    public async Task stderrは中身のまま診断へ流す()
    {
        var channel = new FakeChannel([]);
        await using var session = new CodexAppServerSession(channel, "implementation", FixtureWorkspace, "gpt-5.6-terra");
        var diagnostics = new List<LiveDiagnostic>();
        session.Diagnosed += (_, line) => diagnostics.Add(line);

        channel.RaiseStandardError("rmcp::transport::worker: worker quit");

        Assert.Equal("rmcp::transport::worker: worker quit", Assert.Single(diagnostics).Text);
    }

    [Fact]
    public async Task acceptは握手を1から3の順に進め承認を一度だけ発火する()
    {
        var channel = new FakeChannel(ReadFixture("accept.stdout.jsonl"));
        await using var session = new CodexAppServerSession(channel, "engineering", FixtureWorkspace, "gpt-5.6-terra");
        var approvals = new List<ApprovalRequest>();
        session.ApprovalRequested += (_, request) => approvals.Add(request);

        channel.Release();
        await session.CompleteHandshakeAsync(CancellationToken.None);
        await session.SendUserMessageAsync(FixturePrompt, CancellationToken.None);
        await channel.Completed;

        Assert.Single(approvals);
        var written = channel.Written.Take(4).ToArray();
        Assert.Equal(new long[] { 1, 2, 3 }, written.Where(line => HasId(line)).Select(Id).ToArray());
        Assert.Equal(new[] { "initialize", "initialized", "thread/start", "turn/start" }, written.Select(Method).ToArray());
    }

    [Theory]
    [InlineData("accept", true)]
    [InlineData("acceptWithExecpolicyAmendment", true)]
    [InlineData("cancel", false)]
    public async Task 承認応答はfixtureの提示値そのままでありturnを正しく判定する(string decision, bool succeeded)
    {
        var channel = new FakeChannel(ReadFixture($"{decision}.stdout.jsonl"));
        await using var session = new CodexAppServerSession(channel, "engineering", FixtureWorkspace, "gpt-5.6-terra");
        var approval = new TaskCompletionSource<ApprovalRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var verdict = new TaskCompletionSource<OutcomeVerdict>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ApprovalRequested += (_, request) => approval.TrySetResult(request);
        session.TurnFinished += (_, item) => verdict.TrySetResult(item);

        channel.Release();
        await session.CompleteHandshakeAsync(CancellationToken.None);
        await session.SendUserMessageAsync(FixturePrompt, CancellationToken.None);
        var request = await approval.Task;
        await session.RespondAsync(request, request.AvailableDecisions.Single(item => item.Id == decision), null, CancellationToken.None);
        AssertJsonEqual(ReadFixture($"{decision}.stdin.jsonl").Last(), channel.Written.Last());
        Assert.Equal(succeeded, (await verdict.Task).Succeeded);
    }

    [Fact]
    public async Task 提示していない決定は一行も書かない()
    {
        var channel = new FakeChannel(ReadFixture("accept.stdout.jsonl"));
        await using var session = new CodexAppServerSession(channel, "engineering", FixtureWorkspace, "gpt-5.6-terra");
        var approval = new TaskCompletionSource<ApprovalRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ApprovalRequested += (_, request) => approval.TrySetResult(request);
        channel.Release();
        await session.CompleteHandshakeAsync(CancellationToken.None);
        await session.SendUserMessageAsync(FixturePrompt, CancellationToken.None);
        var request = await approval.Task;
        var before = channel.Written.Count;

        await Assert.ThrowsAsync<ArgumentException>(() => session.RespondAsync(request, new ApprovalDecision("other", "other"), null, CancellationToken.None));
        Assert.Equal(before, channel.Written.Count);
    }

    [Fact]
    public async Task 未知サーバ要求へはresultなしのMethodNotFoundを返す()
    {
        var unknown = "{\"jsonrpc\":\"2.0\",\"id\":77,\"method\":\"future/method\",\"params\":{}}";
        var channel = new FakeChannel([.. ReadFixture("accept.stdout.jsonl").Take(3), unknown]);
        await using var session = new CodexAppServerSession(channel, "engineering", FixtureWorkspace, "gpt-5.6-terra");
        channel.Release();
        await session.CompleteHandshakeAsync(CancellationToken.None);
        await channel.Completed;

        // 書き込み順序を仮定しない。握手の続き（thread/start）と未知要求への応答は、
        // どちらが先に書かれるか保証されていない。Last() に頼ると偶然で通ったり落ちたりする。
        var response = Assert.Single(channel.Written, line => line.Contains("\"id\":77"));
        using var document = JsonDocument.Parse(response);
        Assert.Equal(77, document.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(-32601, document.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task 購読側の例外でもturn完了まで読み続ける()
    {
        var channel = new FakeChannel(ReadFixture("accept.stdout.jsonl"));
        await using var session = new CodexAppServerSession(channel, "engineering", FixtureWorkspace, "gpt-5.6-terra");
        var evidence = new List<Evidence>();
        var verdicts = new List<OutcomeVerdict>();
        session.ApprovalRequested += (_, _) => throw new InvalidOperationException("subscriber secret");
        session.Observed += (_, item) => evidence.Add(item);
        session.TurnFinished += (_, item) => verdicts.Add(item);
        channel.Release();
        await session.CompleteHandshakeAsync(CancellationToken.None);
        await session.SendUserMessageAsync(FixturePrompt, CancellationToken.None);
        await channel.Completed;

        Assert.True(Assert.Single(verdicts).Succeeded);
        Assert.Contains(evidence, item => item.RedactedSummary.Contains("InvalidOperationException"));
        Assert.DoesNotContain(evidence, item => item.RedactedSummary.Contains("subscriber secret"));
    }

    [Fact]
    public async Task activeFlagsがnullでも承認待ちと断定しない()
    {
        const string status = "{\"method\":\"thread/status/changed\",\"params\":{\"status\":{\"activeFlags\":null}}}";
        var channel = new FakeChannel([.. ReadFixture("accept.stdout.jsonl").Take(3), status]);
        await using var session = new CodexAppServerSession(channel, "engineering", FixtureWorkspace, "gpt-5.6-terra");
        var evidence = new List<Evidence>();
        session.Observed += (_, item) => evidence.Add(item);
        channel.Release();
        await session.CompleteHandshakeAsync(CancellationToken.None);
        await channel.Completed;

        Assert.DoesNotContain(evidence, item => item.RedactedSummary.Contains("承認待ち"));
    }

    [Fact]
    public async Task reasonはCodexへ送らずObservedに残す()
    {
        var channel = new FakeChannel(ReadFixture("accept.stdout.jsonl"));
        await using var session = new CodexAppServerSession(channel, "engineering", FixtureWorkspace, "gpt-5.6-terra");
        var approval = new TaskCompletionSource<ApprovalRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var evidence = new List<Evidence>();
        session.ApprovalRequested += (_, request) => approval.TrySetResult(request);
        // **Observed は二つのスレッドから来る。** 承認要求の後も fixture の続きを読み取りループが
        // 観測し続け、理由の観測は RespondAsync を呼んだこのスレッドで出る。
        // 素の List に並行して Add すると、列挙中に書き換わって落ちる（まれにしか出ない）。
        session.Observed += (_, item) => { lock (evidence) evidence.Add(item); };
        channel.Release();
        await session.CompleteHandshakeAsync(CancellationToken.None);
        await session.SendUserMessageAsync(FixturePrompt, CancellationToken.None);
        var request = await approval.Task;
        await session.RespondAsync(request, request.AvailableDecisions.Single(item => item.Id == "accept"), "human reason", CancellationToken.None);
        await channel.Completed;

        Assert.DoesNotContain(channel.Written, line => line.Contains("human reason", StringComparison.Ordinal));
        Evidence[] observed;
        lock (evidence) observed = [.. evidence];
        Assert.Contains(observed, item => item.RedactedSummary.Contains("human reason"));
    }

    private const string FixtureWorkspace = "/Volumes/SSD/Developer/MultiAIAgentCompany/spikes/fixtures/codex/_scratch/ws";
    private const string FixturePrompt = "Run exactly this shell command and nothing else: echo HELLO > /Volumes/SSD/Developer/MultiAIAgentCompany/spikes/fixtures/codex/_scratch/outside/hello.txt";
    private static IEnumerable<string> ReadFixture(string name) => File.ReadLines(Path.Combine(AppContext.BaseDirectory, "fixtures", "codex", name));
    private static bool HasId(string line) { using var d = JsonDocument.Parse(line); return d.RootElement.TryGetProperty("id", out _); }
    private static long Id(string line) { using var d = JsonDocument.Parse(line); return d.RootElement.GetProperty("id").GetInt64(); }
    private static string Method(string line) { using var d = JsonDocument.Parse(line); return d.RootElement.GetProperty("method").GetString()!; }
    private static void AssertJsonEqual(string expected, string actual) { using var e = JsonDocument.Parse(expected); using var a = JsonDocument.Parse(actual); Assert.True(JsonElement.DeepEquals(e.RootElement, a.RootElement)); }

    private sealed class FakeChannel(IEnumerable<string> lines) : IAgentProcessChannel
    {
        private readonly IReadOnlyList<string> _lines = lines.ToArray();
        private readonly TaskCompletionSource _start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Written { get; } = [];
        public Task Completed => _completed.Task;
        public ProcessIdentity Identity { get; } = new(42, 0, 0, DateTimeOffset.UtcNow);
        public event EventHandler<int>? Exited;
        public event EventHandler<string>? StandardErrorLine;
        public void RaiseStandardError(string line) => StandardErrorLine?.Invoke(this, line);
        public void Release() => _start.TrySetResult();
        public async IAsyncEnumerable<string> ReadLinesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await _start.Task.WaitAsync(ct);
            foreach (var line in _lines) { ct.ThrowIfCancellationRequested(); yield return line; }
            _completed.TrySetResult();
        }
        public Task WriteLineAsync(string line, CancellationToken ct) { Written.Add(line); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { _start.TrySetResult(); _completed.TrySetResult(); Exited?.Invoke(this, 0); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    [Fact]
    public async Task 前のturnの拒否が次のturnの判定を汚さない()
    {
        // 拒否された turn の "declined" が残ると、次の成功した turn が失敗として表示される。
        var lines = ReadFixture("cancel.stdout.jsonl").ToList();
        lines.Add("{\"jsonrpc\":\"2.0\",\"method\":\"turn/completed\",\"params\":{\"turn\":{\"status\":\"completed\"}}}");
        var channel = new FakeChannel(lines);
        await using var session = new CodexAppServerSession(channel, "engineering", "/tmp", "gpt-5.6-terra");
        var verdicts = new List<OutcomeVerdict>();
        session.TurnFinished += (_, verdict) => verdicts.Add(verdict);

        channel.Release();
        await channel.Completed;

        Assert.Equal(2, verdicts.Count);
        Assert.False(verdicts[0].Succeeded);   // 拒否された turn
        Assert.True(verdicts[1].Succeeded);    // 次の turn は汚されていない
    }

    [Fact]
    public async Task 握手から版を取る()
    {
        // 検出器は版依存（§7）。取れるのに取らないと、どの版を読んでいるか分からないまま動く。
        var channel = new FakeChannel(ReadFixture("accept.stdout.jsonl"));
        await using var session = new CodexAppServerSession(channel, "engineering", "/tmp", "gpt-5.6-terra");

        channel.Release();
        await channel.Completed;

        Assert.Equal("0.153.4", session.DetectedVersion);
    }

    [Fact]
    public async Task turnで変わった強さを申告値として持つ()
    {
        // **強さは turn/start で渡す**（§48-2）ので、thread/start の応答は CLI の既定（high）を申告する。
        // 実際に使った値は `thread/settings/updated` で届く（2026-09-13 に実プロセスで確かめた）。
        const string threadId = "01a07273-df61-75c2-92c8-951ea3c10c78";
        var lines = ReadFixture("accept.stdout.jsonl").ToList();
        lines.Insert(4,
            "{\"method\":\"thread/settings/updated\",\"params\":{\"threadId\":\"" + threadId + "\","
            + "\"threadSettings\":{\"model\":\"gpt-5.6-terra\",\"effort\":\"low\"}},\"emittedAtMs\":1789291365259}");
        lines.Insert(5,
            "{\"method\":\"thread/settings/updated\",\"params\":{\"threadId\":\"other-thread\","
            + "\"threadSettings\":{\"model\":\"gpt-5.5\",\"effort\":\"xhigh\"}},\"emittedAtMs\":1789291365260}");
        var channel = new FakeChannel(lines);
        await using var session = new CodexAppServerSession(channel, "engineering", "/tmp", "gpt-5.6-terra", "low");

        channel.Release();
        await channel.Completed;

        // **別の thread の通知は拾わない。**
        Assert.Equal(new AgentModel("gpt-5.6-terra", "low"), session.ObservedModel);
    }

}
