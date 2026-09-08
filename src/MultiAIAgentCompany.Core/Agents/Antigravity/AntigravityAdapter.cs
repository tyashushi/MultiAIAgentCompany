using MultiAIAgentCompany.Core.Sessions;
using MultiAIAgentCompany.Core.Workspace;

namespace MultiAIAgentCompany.Core.Agents.Antigravity;

/// <summary>Antigravity の常駐 stream-json セッションを起動するアダプタ。</summary>
public sealed class AntigravityAdapter : IAgentAdapter
{
    private static readonly string[] Arguments = ["--input-format", "stream-json", "--output-format", "stream-json", "-p="];

    /// <summary>
    /// 危険モードで足す引数（設計 §30-4）。
    /// </summary>
    /// <remarks>
    /// headless の <c>agy</c> は<b>ツール権限を人間に聞けないので全部自動拒否する</b>
    /// （§30-1、実測）。<c>can_use_tool</c> に当たる往復が無いので、
    /// アプリが拒否を人間へ見せて許可をもらう経路が存在しない ——
    /// **人間が部門ごとに、事前に決めるしかない。**
    /// </remarks>
    // **`--dangerously-skip-permissions` は使わない**（設計 §32-3、2026-09-09）。
    // §30-4 の危険モードは、外部ターミナルという「人間に聞く手段」ができたので廃止した ——
    // **聞けるのに聞かない、を残さない。**
    private readonly Func<string, IReadOnlyList<string>, string, CancellationToken, Task<IAgentProcessChannel>> _channelFactory;
    private AntigravitySession? _lastSession;

    public AntigravityAdapter(Func<string, IReadOnlyList<string>, string, CancellationToken, Task<IAgentProcessChannel>>? channelFactory = null) =>
        _channelFactory = channelFactory ?? ((file, args, cwd, ct) => ChildProcessChannel.StartAsync(file, args, cwd, ct: ct));

    public AgentKind Kind => AgentKind.AntigravityCli;
    public AgentCapabilities Capabilities => AgentCapabilities.For(Kind);
    public string? DetectedVersion => _lastSession?.DetectedVersion;

    public async Task<IAgentSession> StartAsync(
        WorkspaceRef workspace, string departmentId, DriveMode mode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(departmentId);
        if (mode != DriveMode.Structured) throw new ArgumentOutOfRangeException(nameof(mode), mode, null);

        var channel = await _channelFactory("agy", Arguments, workspace.Root, ct).ConfigureAwait(false);
        try { return _lastSession = new AntigravitySession(channel, departmentId); }
        catch { await channel.DisposeAsync().ConfigureAwait(false); throw; }
    }
}
