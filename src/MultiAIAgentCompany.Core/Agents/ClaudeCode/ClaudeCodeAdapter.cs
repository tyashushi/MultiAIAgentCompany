using MultiAIAgentCompany.Core.Sessions;
using MultiAIAgentCompany.Core.Workspace;

namespace MultiAIAgentCompany.Core.Agents.ClaudeCode;

/// <summary>Claude Code の構造化セッションを起動するアダプタ。</summary>
public sealed class ClaudeCodeAdapter : IAgentAdapter
{
    private static readonly string[] BaseArguments =
        ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--permission-prompt-tool", "stdio"];
    private readonly Func<string, IReadOnlyList<string>, string, CancellationToken, Task<IAgentProcessChannel>> _channelFactory;
    private readonly string[] _arguments;
    private ClaudeCodeStructuredSession? _lastSession;

    /// <param name="model">渡すモデル。null / 空なら渡さない（設計 §46）。</param>
    /// <param name="effort">渡す思考の強さ。null / 空なら渡さない。</param>
    /// <param name="permissionMode">
    /// 起動時の権限モード（設計 §62-22）。null なら渡さない。
    /// <b>構造化でも同じ4つが効く</b>（2026-09-20 に実機で確かめた。`manual` は init で `default` と申告される）。
    /// </param>
    public ClaudeCodeAdapter(
        string? model = null, string? effort = null,
        Func<string, IReadOnlyList<string>, string, CancellationToken, Task<IAgentProcessChannel>>? channelFactory = null,
        AgentPermissionMode? permissionMode = null)
    {
        _channelFactory = channelFactory ?? ((file, args, cwd, ct) => ChildProcessChannel.StartAsync(file, args, cwd, ct: ct));

        // **指定が無いものは渡さない。** 空で渡すと CLI 側の設定を空で上書きしかねない（§7）。
        var arguments = new List<string>(BaseArguments);
        if (model?.Trim() is { Length: > 0 } trimmedModel) arguments.AddRange(["--model", trimmedModel]);
        if (effort?.Trim() is { Length: > 0 } trimmedEffort) arguments.AddRange(["--effort", trimmedEffort]);

        // **持っていないモードは渡さない**（§51-2）。Claude は4つとも持っている。
        if (permissionMode is { } mode && AgentPermissionModes.For(AgentKind.ClaudeCode).Contains(mode))
        {
            arguments.AddRange(["--permission-mode", mode switch
            {
                AgentPermissionMode.Auto => "auto",
                AgentPermissionMode.Manual => "manual",
                AgentPermissionMode.AcceptEdits => "acceptEdits",
                AgentPermissionMode.Plan => "plan",
                _ => throw new ArgumentOutOfRangeException(nameof(permissionMode)),
            }]);
        }

        _arguments = [.. arguments];
    }

    public AgentKind Kind => AgentKind.ClaudeCode;
    public AgentCapabilities Capabilities => AgentCapabilities.For(Kind);
    public string? DetectedVersion => _lastSession?.DetectedVersion;

    public async Task<IAgentSession> StartAsync(
        WorkspaceRef workspace, string departmentId, DriveMode mode, CancellationToken ct)
    {
        // **聞ける相手には聞く**（設計 §3 / §30-4）。Claude Code は承認の往復を持つので、
        // 危険モードはここに来ない。黙って無視すると「危険モードにしたのに効いていない」と
        // 「安全なのに危険と表示する」が両方起きる。

        ArgumentNullException.ThrowIfNull(workspace);

        // 先に検査する。起動してから弾くと、セッションを返せないまま子プロセスが残る（設計 §9）。
        ArgumentException.ThrowIfNullOrWhiteSpace(departmentId);

        if (mode != DriveMode.Structured)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }

        var channel = await _channelFactory("claude", _arguments, workspace.Root, ct).ConfigureAwait(false);
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
