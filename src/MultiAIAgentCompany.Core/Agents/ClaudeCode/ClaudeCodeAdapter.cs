using MultiAIAgentCompany.Core.Sessions;
using MultiAIAgentCompany.Core.Workspace;

namespace MultiAIAgentCompany.Core.Agents.ClaudeCode;

/// <summary>Claude Code の構造化セッションを起動するアダプタ。</summary>
public sealed class ClaudeCodeAdapter : IAgentAdapter
{
    private static readonly string[] Arguments =
        ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--permission-prompt-tool", "stdio"];
    private readonly Func<string, IReadOnlyList<string>, string, CancellationToken, Task<IAgentProcessChannel>> _channelFactory;
    private ClaudeCodeStructuredSession? _lastSession;

    public ClaudeCodeAdapter(Func<string, IReadOnlyList<string>, string, CancellationToken, Task<IAgentProcessChannel>>? channelFactory = null)
    {
        _channelFactory = channelFactory ?? ((file, args, cwd, ct) => ChildProcessChannel.StartAsync(file, args, cwd, ct: ct));
    }

    public AgentKind Kind => AgentKind.ClaudeCode;
    public AgentCapabilities Capabilities => AgentCapabilities.For(Kind);
    public string? DetectedVersion => _lastSession?.DetectedVersion;

    public async Task<IAgentSession> StartAsync(
        WorkspaceRef workspace, string departmentId, DriveMode mode, CancellationToken ct, bool approveAllTools = false)
    {
        // **聞ける相手には聞く**（設計 §3 / §30-4）。Claude Code は承認の往復を持つので、
        // 危険モードはここに来ない。黙って無視すると「危険モードにしたのに効いていない」と
        // 「安全なのに危険と表示する」が両方起きる。
        if (approveAllTools) throw new ArgumentOutOfRangeException(nameof(approveAllTools), approveAllTools,
            "Claude Code は承認の往復を持つので、危険モードは使えません");

        ArgumentNullException.ThrowIfNull(workspace);

        // 先に検査する。起動してから弾くと、セッションを返せないまま子プロセスが残る（設計 §9）。
        ArgumentException.ThrowIfNullOrWhiteSpace(departmentId);

        if (mode != DriveMode.Structured)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }

        var channel = await _channelFactory("claude", Arguments, workspace.Root, ct).ConfigureAwait(false);
        try
        {
            return _lastSession = new ClaudeCodeStructuredSession(channel, departmentId);
        }
        catch
        {
            // セッションを返せないなら、開いたチャネルは自分で閉じる。孤児を作らない（設計 §9）。
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
