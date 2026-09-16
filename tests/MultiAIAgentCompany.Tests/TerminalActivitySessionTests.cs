using MultiAIAgentCompany.Core.Activity;
using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Status;
using MultiAIAgentCompany.Core.Terminal;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>窓の起動前の準備と、購読・消滅時の片付けを確認する（設計 §61-1 / §61-2）。</summary>
public sealed class TerminalActivitySessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maac-session-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task 起動前に準備し購読後に観測して消滅で片付ける()
    {
        var launch = ActivityLaunch.Create(_root, "/workspace", "dev", DateTimeOffset.UtcNow);
        var launcher = new Launcher(Path.Combine(_root, "pid"), int.MaxValue);
        var request = new TerminalLaunchRequest("title", "/workspace", "/cli", ["prompt"], Activity: launch);
        var result = await TerminalDepartmentSession.StartAsync("dev", AgentKind.CodexCli, request, launcher, TimeProvider.System, CancellationToken.None);
        await using var session = Assert.IsType<TerminalStartResult.Started>(result).Session;
        Assert.True(launcher.WasPrepared);
        Assert.True(session.ObservesActivity);
        Assert.False(session.HasHookEvent);
        var observed = new TaskCompletionSource<Observed<ActivityState>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disappeared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ActivityObserved += (_, activity) => observed.TrySetResult(activity);
        session.Disappeared += (_, _) => disappeared.TrySetResult();
        session.ReplayObservations();
        Assert.Equal(ActivityState.Working, (await observed.Task.WaitAsync(TimeSpan.FromSeconds(4))).Value);
        Assert.True(session.HasHookEvent);
        await disappeared.Task.WaitAsync(TimeSpan.FromSeconds(7));
        Assert.False(Directory.Exists(launch.DirectoryPath));
    }

    [Fact]
    public async Task 付け直した窓は観測の保存先を新たに作らない()
    {
        Directory.CreateDirectory(_root);
        var launcher = new Launcher(Path.Combine(_root, "pid"), Environment.ProcessId);
        File.WriteAllText(launcher.Handle.PidFilePath, Environment.ProcessId.ToString());
        await using var session = await TerminalDepartmentSession.ReattachAsync("dev", AgentKind.CodexCli,
            launcher.Handle, launcher, TimeProvider.System, CancellationToken.None);
        Assert.False(session.ObservesActivity);
        Assert.False(session.HasHookEvent);
        session.ReplayObservations();
        Assert.Empty(Directory.GetDirectories(_root));
    }

    private sealed class Launcher(string path, int pid) : ITerminalLauncher
    {
        public TerminalHandle Handle { get; } = new("42", 1, path);
        public bool WasPrepared { get; private set; }
        public Task<TerminalLaunchResult> LaunchAsync(TerminalLaunchRequest request, CancellationToken ct)
        {
            WasPrepared = Directory.Exists(request.Activity!.DirectoryPath);
            File.WriteAllText(request.Activity.EventsPath, "1700000000 codex UserPromptSubmit\n");
            File.WriteAllText(Handle.PidFilePath, pid.ToString());
            return Task.FromResult<TerminalLaunchResult>(new TerminalLaunchResult.Launched(Handle));
        }
        public Task<bool> FocusAsync(TerminalHandle handle, CancellationToken ct) => Task.FromResult(true);
        public Task<TerminalTerminateResult> TerminateAsync(TerminalHandle handle, CancellationToken ct) =>
            Task.FromResult<TerminalTerminateResult>(new TerminalTerminateResult.NotRunning("テスト"));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
