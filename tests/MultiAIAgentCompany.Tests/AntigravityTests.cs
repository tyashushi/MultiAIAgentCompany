using System.Text.Json;
using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Agents.Antigravity;
using MultiAIAgentCompany.Core.Sessions;
using MultiAIAgentCompany.Core.Status;
using MultiAIAgentCompany.Core.Workspace;
using Xunit;

namespace MultiAIAgentCompany.Tests;

public sealed class AntigravityTests
{
    [Theory]
    [InlineData("denied.stdout.jsonl", 6)]
    [InlineData("allowed.stdout.jsonl", 4)]
    public void 実測_stdoutの全行はUnknownにならない(string fileName, int count)
    {
        var events = ReadFixture(fileName).Select(AntigravityStreamReader.ReadLine).ToArray();
        Assert.Equal(count, events.Length);
        Assert.DoesNotContain(events, @event => @event is AntigravityEvent.Unknown);
    }

    [Fact]
    public async Task stderrは分類と中身の両方を出す()
    {
        // 分類は永続してよい要約、診断は中身（設計 §22）。**両方要る。**
        var channel = new FakeChannel([]);
        await using var session = new AntigravitySession(channel, "review");
        var observations = new List<Evidence>();
        var diagnostics = new List<LiveDiagnostic>();
        session.Observed += (_, evidence) => observations.Add(evidence);
        session.Diagnosed += (_, line) => diagnostics.Add(line);

        channel.RaiseStandardError("warning: ignoring unsupported stream input message event \"foo\"");

        Assert.Contains("未知の event", Assert.Single(observations).RedactedSummary, StringComparison.Ordinal);
        Assert.Contains("ignoring unsupported", Assert.Single(diagnostics).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void 壊れた行と未知eventは例外ではなくUnknownになる()
    {
        Assert.IsType<AntigravityEvent.Unknown>(AntigravityStreamReader.ReadLine("{"));
        var unknown = Assert.IsType<AntigravityEvent.Unknown>(AntigravityStreamReader.ReadLine("{\"event\":\"future\"}"));
        Assert.Contains("future", unknown.Reason);
    }

    [Fact]
    public void initからpermission_modeを読む()
    {
        var init = Assert.IsType<AntigravityEvent.Init>(AntigravityStreamReader.ReadLine(ReadFixture("allowed.stdout.jsonl").First()));
        Assert.Equal("request-review", init.PermissionMode);
    }

    [Fact]
    public void deniedはSUCCESSでも失敗で拒否理由を持つ()
    {
        var (finished, sawError) = Finished("denied.stdout.jsonl");
        var signals = AntigravityTurnOutcome.ToSignals(finished, sawError);
        var verdict = signals.Judge(OutcomeRequirement.For(AgentKind.AntigravityCli));

        Assert.Equal("SUCCESS", finished.Status);
        Assert.False(verdict.Succeeded);
        Assert.Contains("RunCommand", verdict.Reason);
        Assert.Equal(LayerObservation.Failed, signals.Tool);
    }

    [Fact]
    public void allowedは成功になる()
    {
        var (finished, sawError) = Finished("allowed.stdout.jsonl");
        Assert.True(AntigravityTurnOutcome.ToSignals(finished, sawError)
            .Judge(OutcomeRequirement.For(AgentKind.AntigravityCli)).Succeeded);
    }

    [Fact]
    public void ERRORが無いことはToolの成功確認ではない()
    {
        var (finished, sawError) = Finished("allowed.stdout.jsonl");
        Assert.False(sawError);
        Assert.Equal(LayerObservation.NotObserved, AntigravityTurnOutcome.ToSignals(finished, sawError).Tool);
    }

    [Fact]
    public async Task duplexは同じプロセスで2回TurnFinishedを発火し成功と失敗を分ける()
    {
        var channel = new FakeChannel(ReadFixture("duplex.stdout.jsonl"));
        await using var session = new AntigravitySession(channel, "research");
        var verdicts = new List<OutcomeVerdict>();
        session.TurnFinished += (_, verdict) => verdicts.Add(verdict);

        channel.Release();
        await channel.Completed;

        Assert.Equal(2, verdicts.Count);
        Assert.True(verdicts[0].Succeeded);
        Assert.False(verdicts[1].Succeeded);
    }

    [Fact]
    public async Task ユーザーメッセージはtypeではなくeventで書く()
    {
        var channel = new FakeChannel([]);
        await using var session = new AntigravitySession(channel, "research");

        await session.SendUserMessageAsync("<prompt>", CancellationToken.None);

        using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDirectory, "duplex.stdin-shape.txt")));
        using var actual = JsonDocument.Parse(Assert.Single(channel.Written));
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
        Assert.False(actual.RootElement.TryGetProperty("type", out _));
    }

    [Fact]
    public async Task RespondAsyncはランタイム承認が無いため未対応である()
    {
        var channel = new FakeChannel([]);
        await using var session = new AntigravitySession(channel, "research");
        var request = new ApprovalRequest("id", ApprovalKind.Runtime, AgentKind.AntigravityCli, null, null, "title", "", [], []);
        await Assert.ThrowsAsync<NotSupportedException>(() => session.RespondAsync(request, new ApprovalDecision("allow", "許可"), null, CancellationToken.None));
    }

    [Fact]
    public async Task DetectedVersionはinitに版が無いためnullである()
    {
        var channel = new FakeChannel(ReadFixture("allowed.stdout.jsonl"));
        await using var session = new AntigravitySession(channel, "research");
        channel.Release();
        await channel.Completed;
        Assert.Null(session.DetectedVersion);
    }

    [Fact]
    public async Task stderrの未知event警告はObservedに出す()
    {
        var channel = await ChildProcessChannel.StartAsync("/bin/sh",
            ["-c", "read line; echo 'warning: ignoring unsupported stream input message event \\\"future\\\"' >&2"],
            Path.GetTempPath());
        await using var session = new AntigravitySession(channel, "research");
        var observed = new TaskCompletionSource<Evidence>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Observed += (_, evidence) =>
        {
            if (evidence.RedactedSummary.Contains("未知の event", StringComparison.Ordinal))
                observed.TrySetResult(evidence);
        };

        await session.SendUserMessageAsync("<prompt>", CancellationToken.None);
        var evidence = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 送ったのに何も起きない、を人間に見せる。ただし stderr の行はそのまま持ち出さない（§10）。
        Assert.Contains("future", evidence.RedactedSummary);
        Assert.DoesNotContain("warning: ignoring", evidence.RedactedSummary);
    }

    [Fact]
    public async Task adapterは空のpとstream_json常駐引数で起動する()
    {
        IReadOnlyList<string>? actualArguments = null;
        var adapter = new AntigravityAdapter(channelFactory: (_, arguments, _, _) =>
        {
            actualArguments = arguments;
            return Task.FromResult<IAgentProcessChannel>(new FakeChannel([]));
        });
        await using var session = await adapter.StartAsync(new WorkspaceRef(Path.GetTempPath()), "research", DriveMode.Structured, CancellationToken.None);
        Assert.Equal(["--input-format", "stream-json", "--output-format", "stream-json", "-p="], actualArguments);
    }

    [Fact]
    public async Task 空の部門IDは起動前に弾く()
    {
        var started = false;
        var adapter = new AntigravityAdapter(channelFactory: (_, _, _, _) => { started = true; return Task.FromResult<IAgentProcessChannel>(new FakeChannel([])); });
        await Assert.ThrowsAnyAsync<ArgumentException>(() => adapter.StartAsync(new WorkspaceRef(Path.GetTempPath()), " ", DriveMode.Structured, CancellationToken.None));
        Assert.False(started);
    }

    private static (AntigravityEvent.Finished Finished, bool SawError) Finished(string fileName)
    {
        var events = ReadFixture(fileName).Select(AntigravityStreamReader.ReadLine).ToArray();
        return (Assert.IsType<AntigravityEvent.Finished>(events.Single(@event => @event is AntigravityEvent.Finished)),
            events.OfType<AntigravityEvent.StepUpdate>().Any(update => update.State == "ERROR"));
    }

    private static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "fixtures", "antigravity");
    private static IEnumerable<string> ReadFixture(string name) => File.ReadLines(Path.Combine(FixtureDirectory, name));

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
    public void stderrは分類だけを出し中身を持ち出さない()
    {
        // RedactedSummary は「秘密値を含まない、永続しうる要約」（§10）。
        // stderr には作業パスもコマンドも混ざるので、行をそのまま流さない。
        var autoDenied = ReadFixture("denied.stderr.jsonl").First();

        var summary = AntigravitySession.ClassifyStandardError(autoDenied);

        Assert.Contains("自動拒否", summary);
        Assert.DoesNotContain("/Volumes/", summary);
        Assert.DoesNotContain("settings.json", summary);
    }

    [Fact]
    public void 未知のeventが捨てられたことは名前つきで見せる()
    {
        // event 名はこちらが送った値なので、含めても外から来た秘密ではない。
        var summary = AntigravitySession.ClassifyStandardError(
            "warning: ignoring unsupported stream input message event \"user_input\"");

        Assert.Contains("user_input", summary);
    }

    [Fact]
    public void 分類できないstderrは中身を出さない()
    {
        var summary = AntigravitySession.ClassifyStandardError("panic: secret-token-abc123 at /Users/x/y");

        Assert.DoesNotContain("secret-token-abc123", summary);
        Assert.DoesNotContain("/Users/", summary);
    }
    [Fact]
    public async Task 構造化でも起動できる()
    {
        // **既定は 2026-09-09 に ExternalTerminal へ移した**（§32-3）が、
        // **構造化の経路は残っている** —— 秘書が使うし、部門で使えないと決める理由も無い。
        // ここはその経路が生きていることを見張る。
        Assert.Equal(
            DriveMode.ExternalTerminal, AgentCapabilities.For(AgentKind.AntigravityCli).DefaultDriveMode);
        Assert.True(AgentCapabilities.For(AgentKind.AntigravityCli).SupportsStructuredConversation);

        // 起動経路が生きていること（プロセスは偽物で確かめる）。
        var started = 0;
        var adapter = new AntigravityAdapter(channelFactory: (_, _, _, _) =>
        {
            started++;
            return Task.FromResult<IAgentProcessChannel>(new FakeChannel([]));
        });

        await using var session = await adapter.StartAsync(
            new MultiAIAgentCompany.Core.Workspace.WorkspaceRef(Path.GetTempPath()),
            "調査",

            // **既定は ExternalTerminal になった**（§32-3）ので、構造化の経路を試すには明示する。
            DriveMode.Structured,
            CancellationToken.None);

        Assert.Equal(1, started);
    }

    [Fact]
    public async Task 全自動承認の引数は渡さない()
    {
        // **§30-4 の危険モードは廃止した**（設計 §32-3）。外部ターミナルという
        // 「人間に聞く手段」ができたので要らない —— **聞けるのに聞かない、を残さない。**
        // 消したものが復活しないように、**引数に綴りが現れないこと**で見張る。
        string[] passed = [];
        var adapter = new AntigravityAdapter(channelFactory: (_, args, _, _) =>
        {
            passed = [.. args];
            return Task.FromResult<IAgentProcessChannel>(new FakeChannel([]));
        });

        await using var session = await adapter.StartAsync(
            new MultiAIAgentCompany.Core.Workspace.WorkspaceRef(Path.GetTempPath()),
            "調査", DriveMode.Structured, CancellationToken.None);

        Assert.DoesNotContain("--dangerously-skip-permissions", passed);
    }

    [Fact]
    public async Task 知らない駆動モードは明示的に断る()
    {
        // 黙って何もしない実装にしない。v1 の駆動モードは構造化だけ（設計 §22-4）。
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new AntigravityAdapter().StartAsync(
                new MultiAIAgentCompany.Core.Workspace.WorkspaceRef(Path.GetTempPath()),
                "調査", (DriveMode)99, CancellationToken.None));
    }


}
