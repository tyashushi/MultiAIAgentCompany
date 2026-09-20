using MultiAIAgentCompany.Core.Sessions;
using MultiAIAgentCompany.Core.Workspace;

namespace MultiAIAgentCompany.Core.Agents.CodexCli;

/// <summary>Codex app-server の構造化セッションを起動するアダプタ。</summary>
public sealed class CodexCliAdapter : IAgentAdapter
{
    private static readonly string[] Arguments = ["app-server", "--stdio"];
    private readonly Func<string, IReadOnlyList<string>, string, CancellationToken, Task<IAgentProcessChannel>> _channelFactory;
    private readonly string _model;
    private readonly string? _effort;
    private readonly AgentPermissionMode? _permissionMode;
    private CodexAppServerSession? _lastSession;

    /// <param name="effort">渡す思考の強さ。null / 空なら渡さない（設計 §46）。</param>
    /// <param name="permissionMode">
    /// 起動時の権限モード（設計 §62-22）。Codex が構造化で持つのは<b>「自動」だけ</b>（§51-2）——
    /// そのときだけ承認の判断を CLI 側に任せる（<c>approvalsReviewer</c>）。
    /// </param>
    public CodexCliAdapter(
        string model = "gpt-5.6-terra", string? effort = null,
        Func<string, IReadOnlyList<string>, string, CancellationToken, Task<IAgentProcessChannel>>? channelFactory = null,
        AgentPermissionMode? permissionMode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        _model = model;
        _effort = effort;
        _permissionMode = permissionMode;
        _channelFactory = channelFactory ?? ((file, args, cwd, ct) => ChildProcessChannel.StartAsync(file, args, cwd, ct: ct));
    }

    public AgentKind Kind => AgentKind.CodexCli;
    public AgentCapabilities Capabilities => AgentCapabilities.For(Kind);
    public string? DetectedVersion => _lastSession?.DetectedVersion;

    public async Task<IAgentSession> StartAsync(
        WorkspaceRef workspace, string departmentId, DriveMode mode, CancellationToken ct)
    {
        // **聞ける相手には聞く**（設計 §3 / §30-4）。Codex CLI は承認の往復を持つので、
        // 危険モードはここに来ない。黙って無視すると「危険モードにしたのに効いていない」と
        // 「安全なのに危険と表示する」が両方起きる。

        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(departmentId);
        if (mode != DriveMode.Structured) throw new ArgumentOutOfRangeException(nameof(mode), mode, null);

        var channel = await _channelFactory("codex", Arguments, workspace.Root, ct).ConfigureAwait(false);
        try
        {
            var session = new CodexAppServerSession(channel, departmentId, workspace.Root, _model, _effort, _permissionMode);
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
