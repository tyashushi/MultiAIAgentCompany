using MultiAIAgentCompany.Core.Sessions;
using MultiAIAgentCompany.Core.Workspace;

namespace MultiAIAgentCompany.Core.Agents.Antigravity;

/// <summary>Antigravity の常駐 stream-json セッションを起動するアダプタ。</summary>
public sealed class AntigravityAdapter : IAgentAdapter
{
    private static readonly string[] Arguments = ["--input-format", "stream-json", "--output-format", "stream-json", "-p="];
    private readonly Func<string, IReadOnlyList<string>, string, CancellationToken, Task<IAgentProcessChannel>> _channelFactory;
    private AntigravitySession? _lastSession;

    public AntigravityAdapter(Func<string, IReadOnlyList<string>, string, CancellationToken, Task<IAgentProcessChannel>>? channelFactory = null) =>
        _channelFactory = channelFactory ?? ((file, args, cwd, ct) => ChildProcessChannel.StartAsync(file, args, cwd, ct: ct));

    public AgentKind Kind => AgentKind.AntigravityCli;
    public AgentCapabilities Capabilities => AgentCapabilities.For(Kind);
    public string? DetectedVersion => _lastSession?.DetectedVersion;

    public async Task<IAgentSession> StartAsync(WorkspaceRef workspace, string departmentId, DriveMode mode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(departmentId);
        if (mode != DriveMode.Structured) throw new ArgumentOutOfRangeException(nameof(mode), mode, null);

        var channel = await _channelFactory("agy", Arguments, workspace.Root, ct).ConfigureAwait(false);
        try { return _lastSession = new AntigravitySession(channel, departmentId); }
        catch { await channel.DisposeAsync().ConfigureAwait(false); throw; }
    }
}
