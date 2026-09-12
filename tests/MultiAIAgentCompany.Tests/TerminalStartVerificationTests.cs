using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Status;
using MultiAIAgentCompany.Core.Terminal;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>
/// 外部ターミナルの起動を<b>確かめてから「渡した」と言う</b>（設計 §41）。
/// </summary>
/// <remarks>
/// <b>実機で1度、確かめずに言って止まった</b>（§41-1）—— 前の CLI がまだ前面に居るタブへ
/// 打ち込んだので文字が吸われ、<c>osascript</c> は成功を返し、
/// アプリは「送り直した」と記録したのに**誰も動いていなかった。**
/// </remarks>
public sealed class TerminalStartVerificationTests
{
    [Fact]
    public async Task PID_が現れなければ_渡したと言わない()
    {
        var pidPath = Path.Combine(Path.GetTempPath(), $"mac-pid-{Guid.NewGuid():N}.pid");
        var launcher = new FakeLauncher(new TerminalHandle("42", 1, pidPath));

        var started = await TerminalDepartmentSession.StartAsync(
            "design", AgentKind.ClaudeCode, Request, launcher, TimeProvider.System, CancellationToken.None);

        // **セッションは返る**（窓は開いているかもしれないので、捨てると
        // アプリの知らない窓が残る）。確かめられていないことだけを伝える。
        var uncertain = Assert.IsType<TerminalStartResult.StartedUnverified>(started);
        Assert.Contains("PID", uncertain.Reason, StringComparison.Ordinal);
        await uncertain.Session.DisposeAsync();
    }

    [Fact]
    public async Task PID_が書かれていれば_起動したと言う()
    {
        var pidPath = Path.Combine(Path.GetTempPath(), $"mac-pid-{Guid.NewGuid():N}.pid");
        await File.WriteAllTextAsync(pidPath, $"{Environment.ProcessId}\n");
        try
        {
            var launcher = new FakeLauncher(new TerminalHandle("42", 1, pidPath));

            var started = await TerminalDepartmentSession.StartAsync(
                "design", AgentKind.ClaudeCode, Request, launcher, TimeProvider.System, CancellationToken.None);

            await Assert.IsType<TerminalStartResult.Started>(started).Session.DisposeAsync();
        }
        finally
        {
            File.Delete(pidPath);
        }
    }

    [Fact]
    public async Task 窓が塞がっていたら_ふつうの失敗と分ける()
    {
        // **窓が生きていることは観測できている**ので、呼び出し側は handle を捨てない（§41-2b）。
        var launcher = new BusyLauncher();

        var started = await TerminalDepartmentSession.StartAsync(
            "design", AgentKind.ClaudeCode, Request, launcher, TimeProvider.System, CancellationToken.None);

        Assert.IsType<TerminalStartResult.WindowBusy>(started);
    }

    [Fact]
    public async Task 窓に付け直しても_起動したとは言わない()
    {
        var pidPath = Path.Combine(Path.GetTempPath(), $"mac-pid-{Guid.NewGuid():N}.pid");
        await File.WriteAllTextAsync(pidPath, Environment.ProcessId.ToString());
        try
        {
            var handle = new TerminalHandle("42", 1, pidPath);
            var session = await TerminalDepartmentSession.ReattachAsync(
                "design", AgentKind.ClaudeCode, handle, new FakeLauncher(handle),
                TimeProvider.System, CancellationToken.None);

            await using (session)
            {
                // **付け直したことは観測として出す** —— 出さないと、
                // 呼び出し側が立てた「起動中」の印を誰も降ろさない。
                var observed = new List<Evidence>();
                session.Observed += (_, evidence) => observed.Add(evidence);
                session.ReplayObservations();

                Assert.Contains(observed, e => e.RedactedSummary.Contains("付け直した", StringComparison.Ordinal));
                Assert.Equal(handle, session.Handle);
            }
        }
        finally
        {
            File.Delete(pidPath);
        }
    }

    private sealed class BusyLauncher : ITerminalLauncher
    {
        public Task<TerminalLaunchResult> LaunchAsync(TerminalLaunchRequest request, CancellationToken ct) =>
            Task.FromResult<TerminalLaunchResult>(new TerminalLaunchResult.WindowBusy("前の CLI がまだ動いている"));

        public Task<bool> FocusAsync(TerminalHandle target, CancellationToken ct) => Task.FromResult(true);

        public Task<TerminalTerminateResult> TerminateAsync(TerminalHandle target, CancellationToken ct) =>
            Task.FromResult<TerminalTerminateResult>(new TerminalTerminateResult.NotRunning("偽物"));
    }

    private static TerminalLaunchRequest Request =>
        new("MultiAI-design", Path.GetTempPath(), "/bin/echo", ["hello"]);

    private sealed class FakeLauncher(TerminalHandle handle) : ITerminalLauncher
    {
        public Task<TerminalLaunchResult> LaunchAsync(TerminalLaunchRequest request, CancellationToken ct) =>
            Task.FromResult<TerminalLaunchResult>(new TerminalLaunchResult.Launched(handle));

        public Task<bool> FocusAsync(TerminalHandle target, CancellationToken ct) => Task.FromResult(true);

        public Task<TerminalTerminateResult> TerminateAsync(TerminalHandle target, CancellationToken ct) =>
            Task.FromResult<TerminalTerminateResult>(new TerminalTerminateResult.NotRunning("偽物"));
    }
}
