using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Agents.Antigravity;
using MultiAIAgentCompany.Core.Agents.ClaudeCode;
using MultiAIAgentCompany.Core.Agents.CodexCli;
using MultiAIAgentCompany.Core.Sessions;
using MultiAIAgentCompany.Core.Status;
using MultiAIAgentCompany.Core.Workspace;

namespace MultiAIAgentCompany.Desktop;

/// <summary>
/// 部門の実セッションを起動し、観測を検出器と承認キューへ流す。
/// </summary>
/// <remarks>
/// <b>ここに判定を書かない。</b> 状態を決めるのは <see cref="DepartmentStatusTracker"/>（§7）、
/// 承認の語彙を決めるのは CLI（§5）。この型がするのは配線だけ。
/// <para>
/// 終了は §9 の契約に従う —— ウィンドウを閉じたら全部門を終了する。
/// </para>
/// </remarks>
public sealed class DepartmentRunner(ShellComposer composer) : IAsyncDisposable
{
    private readonly Dictionary<string, IAgentSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>
    /// 部門を起動する。既に動いていれば何もしない。
    /// </summary>
    /// <returns>起動できなかった理由。成功なら null。</returns>
    public async Task<string?> StartAsync(
        DepartmentDefinition department, WorkspaceRef workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(department);
        ArgumentNullException.ThrowIfNull(workspace);

        if (_sessions.ContainsKey(department.Id))
        {
            return null;
        }

        var tracker = composer.TrackerOf(department.Id);
        tracker.OnStarting();

        try
        {
            var adapter = AdapterFor(department);
            var session = await adapter.StartAsync(workspace, department.Id, department.Mode, ct);
            _sessions[department.Id] = session;
            Wire(department, session, tracker);
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 起動できなかったことを、起動したことにしない。
            // trust が無い・CLI が入っていない・モードが未対応、いずれもここに来る。
            tracker.OnExited(-1);
            return $"{department.DisplayName} を起動できなかった: {exception.Message}";
        }
    }

    private void Wire(DepartmentDefinition department, IAgentSession session, DepartmentStatusTracker tracker)
    {
        session.Observed += (_, evidence) =>
        {
            tracker.OnObserved(evidence);
            Observed?.Invoke(this, (department.Id, evidence));
        };
        session.Exited += (_, exitCode) => tracker.OnExited(exitCode);

        // 診断は**ライブ専用**（設計 §22）。観測（永続してよい要約）と別の経路で運ぶ。
        session.Diagnosed += (_, diagnostic) => Diagnosed?.Invoke(this, (department.Id, diagnostic));

        // **購読より前に出た分を流し込む**（§22-2）。trust・login・ハンドシェイクの失敗は
        // ここに出るのに、アダプタはハンドシェイクを終えてからセッションを返す。
        // 購読を先にしてから取り置きを流すので、その間の1行が二重に出ることはある ——
        // **重複は害が無いが、取りこぼしは害がある。**
        foreach (var diagnostic in session.RecentDiagnostics(50).Reverse())
        {
            Diagnosed?.Invoke(this, (department.Id, diagnostic));
        }

        if (session is not IStructuredSession structured)
        {
            return;
        }

        structured.TurnFinished += (_, verdict) => tracker.OnTurnFinished(verdict);
        structured.ApprovalRequested += (_, request) =>
        {
            tracker.OnApprovalRequested(request);

            // 承認の返事はこのセッションへ返す。決定は CLI が提示したものだけ（§5）。
            composer.Approvals.Add(new PendingApproval(
                department.DisplayName, request, tracker,
                (decision, reason, token) => structured.RespondAsync(request, decision, reason, token)));
        };
    }

    /// <summary>この部門へ直接1メッセージ送る。</summary>
    /// <remarks>
    /// <b>これは調整基盤の経路ではない。</b> 本来は秘書が <c>instruction.md</c> を書き、
    /// アプリが dispatch する（§6 / §14-1）。ここは配線を実物で確かめるための直通路。
    /// </remarks>
    public Task SendAsync(string departmentId, string text, CancellationToken ct) =>
        _sessions.TryGetValue(departmentId, out var session) && session is IStructuredSession structured
            ? structured.SendUserMessageAsync(text, ct)
            : Task.CompletedTask;

    public bool IsRunning(string departmentId) => _sessions.ContainsKey(departmentId);

    /// <summary>dispatch の宛先。動いていない、または構造化でなければ null。</summary>
    public IStructuredSession? StructuredSessionOf(string departmentId) =>
        _sessions.TryGetValue(departmentId, out var session) ? session as IStructuredSession : null;

    /// <summary>観測が来たことを画面へ知らせる（部門ごとの一覧に控えるため）。</summary>
    public event EventHandler<(string DepartmentId, Evidence Evidence)>? Observed;

    /// <summary>
    /// 診断の生の行（設計 §22）。<b>保存しない。</b>
    /// <see cref="Observed"/> と混ぜない —— あちらは redact 済みの要約で永続しうる。
    /// </summary>
    public event EventHandler<(string DepartmentId, LiveDiagnostic Diagnostic)>? Diagnosed;

    private static IAgentAdapter AdapterFor(DepartmentDefinition department) => department.Agent switch
    {
        AgentKind.ClaudeCode => new ClaudeCodeAdapter(),
        AgentKind.CodexCli => new CodexCliAdapter(department.Model ?? "gpt-5.6-terra"),
        AgentKind.AntigravityCli => new AntigravityAdapter(),
        _ => throw new NotSupportedException($"担当できるアダプタが無い: {department.Agent}"),
    };

    /// <summary>
    /// 全部門を終了する。<b>ウィンドウを閉じたときにここへ来る</b>（設計 §9）——
    /// v1 はバックグラウンド継続を持たない。無人運転に近づくため。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            try
            {
                await session.DisposeAsync();
            }
            catch (Exception)
            {
                // 1つ落とせなくても残りの後始末を続ける。
            }
        }

        _sessions.Clear();
    }
}
