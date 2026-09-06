using MultiAIAgentCompany.Core.Sessions;
using MultiAIAgentCompany.Core.Workspace;

namespace MultiAIAgentCompany.Core.Agents.CodexCli;

/// <summary>Codex app-server の構造化セッションを起動するアダプタ。</summary>
public sealed class CodexCliAdapter : IAgentAdapter
{
    private static readonly string[] Arguments = ["app-server", "--stdio"];
    private readonly Func<string, IReadOnlyList<string>, string, CancellationToken, Task<IAgentProcessChannel>> _channelFactory;
    private readonly string _model;
    private CodexAppServerSession? _lastSession;

    public CodexCliAdapter(string model = "gpt-5.6-terra", Func<string, IReadOnlyList<string>, string, CancellationToken, Task<IAgentProcessChannel>>? channelFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        _model = model;
        _channelFactory = channelFactory ?? ((file, args, cwd, ct) => ChildProcessChannel.StartAsync(file, args, cwd, ct: ct));
    }

    public AgentKind Kind => AgentKind.CodexCli;
    public AgentCapabilities Capabilities => AgentCapabilities.For(Kind);
    public string? DetectedVersion => _lastSession?.DetectedVersion;

    public async Task<IAgentSession> StartAsync(WorkspaceRef workspace, string departmentId, DriveMode mode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(departmentId);
        if (mode != DriveMode.Structured) throw new ArgumentOutOfRangeException(nameof(mode), mode, null);

        var channel = await _channelFactory("codex", Arguments, workspace.Root, ct).ConfigureAwait(false);
        try
        {
            var session = new CodexAppServerSession(channel, departmentId, workspace.Root, _model);
            await session.CompleteHandshakeAsync(ct).ConfigureAwait(false);
            return _lastSession = session;
        }
        catch
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
