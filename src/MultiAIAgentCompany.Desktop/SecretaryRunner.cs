using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Agents.ClaudeCode;
using MultiAIAgentCompany.Core.Sessions;
using MultiAIAgentCompany.Core.Status;
using MultiAIAgentCompany.Core.Workspace;

namespace MultiAIAgentCompany.Desktop;

/// <summary>秘書の状態。<b>3軸は持たない</b>（設計 §17-2）。</summary>
public enum SecretaryState
{
    NotStarted,
    Starting,
    Running,
    Failed,
}

/// <summary>
/// 秘書のセッション。人間が中央ペインで会話する唯一の相手（設計 §0）。
/// </summary>
/// <remarks>
/// <b>秘書は部門ではない</b>（§17-2）—— 右ペインのタイルにも <c>TaskStatus</c> にも入らない。
/// 承認の仕組みは部門と共通だが、<b>出す場所が違う</b>（中央ペインに inline。§1）。
/// <para>
/// <b>会話は正本ではない</b>（§17-3）。落ちたら失われてよい。
/// </para>
/// </remarks>
public sealed class SecretaryRunner(ApprovalQueue approvals) : IAsyncDisposable
{
    /// <summary>秘書の予約 ID（§17-2）。</summary>
    public const string DepartmentLabel = "秘書";

    private IStructuredSession? _session;
    private bool _starting;

    /// <summary>
    /// 「いまの秘書」の世代（設計 §17-7）。<b>起動中の破棄を成立させるためにある。</b>
    /// </summary>
    /// <remarks>
    /// 破棄が <c>_session</c> しか見ていないと、<b>起動中の破棄が何もしない</b> ——
    /// そのあと起動が完了して、誰も閉じないセッションが残る。
    /// </remarks>
    private int _generation;

    /// <summary>走っている起動。<b>破棄はこれを待つ</b>（設計 §17-7）。</summary>
    private Task? _startTask;

    /// <summary>いま動いている秘書のワークスペース。<b>切り替えたら別人</b>。</summary>
    public string? WorkspaceRoot { get; private set; }

    /// <summary>
    /// 起動中のものも含めた、この秘書の宛先（設計 §17-7）。
    /// </summary>
    /// <remarks>
    /// <b><see cref="WorkspaceRoot"/> だけでは足りない。</b> 起動が終わるまで null なので、
    /// 起動中に別のフォルダへ切り替えると「切り替わっていない」と判定してしまい、
    /// **画面は B なのに秘書は A で動く**（レビューで発覚、2026-09-06）。
    /// 選択が自動で起動を伴うようになって、この窓が現実的な幅になった。
    /// </remarks>
    public string? PendingWorkspaceRoot { get; private set; }

    /// <summary>
    /// この秘書がいま向いているフォルダ。起動中も含む（設計 §17-7）。
    /// <b>切り替えの判定はこちらで見る。</b>
    /// </summary>
    /// <remarks>
    /// <b>起動中の宛先が優先</b>（レビューで直した）。前の秘書が落ちても
    /// <see cref="WorkspaceRoot"/> は残るので、そちらを先に見ると
    /// **B を起動している最中に A へ戻ったとき「変わっていない」と誤判定する**。
    /// </remarks>
    public string? TargetWorkspaceRoot => PendingWorkspaceRoot ?? WorkspaceRoot;

    public SecretaryState State { get; private set; } = SecretaryState.NotStarted;

    public string? FailureReason { get; private set; }

    public event EventHandler? StateChanged;

    /// <summary>秘書の発言。<b>ライブ表示だけ</b> —— 永続しない（§17-3 / §10）。</summary>
    public event EventHandler<string>? Said;

    public bool IsRunning => _session is not null;

    /// <summary>
    /// 起動する。<b>最初の送信で呼ばれる</b>（設計 §17-4）——
    /// ワークスペース選択に起動という副作用を隠さない。
    /// </summary>
    public async Task<string?> StartAsync(WorkspaceRef workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        // **await の前に印を付ける。** 起動中に2度押されると2つ目のプロセスが立ち、
        // 後勝ちで先のセッションが誰にも閉じられなくなる。
        if (_session is not null || _starting)
        {
            return null;
        }

        _starting = true;
        PendingWorkspaceRoot = workspace.Root;
        SetState(SecretaryState.Starting, null);

        // **走っている起動を持っておく**（設計 §17-7）。破棄がこれを待てるようにするため ——
        // 待たないと、切り替え直後の起動が「まだ起動中」で弾かれ、
        // **新しいフォルダに秘書が居ないまま**になる。
        var task = StartCoreAsync(workspace, _generation, ct);
        _startTask = task;
        return await task;
    }

    private async Task<string?> StartCoreAsync(WorkspaceRef workspace, int generation, CancellationToken ct)
    {
        try
        {
            var adapter = new ClaudeCodeAdapter();
            var session = (IStructuredSession)await adapter.StartAsync(
                workspace, DepartmentLabel, DriveMode.Structured, ct);

            // **起動している間に切り替えられていたら、これは別人**（設計 §17-7）。
            // ここで公開すると、画面は B なのに秘書は A のフォルダで動く。
            if (generation != _generation)
            {
                await session.DisposeAsync();

                // **Starting のまま残さない。** 画面が「起動中」で止まると、
                // 人間は待てば立ち上がると思う（§7 の「沈黙を正常にしない」の裏返し）。
                SetState(SecretaryState.NotStarted, null);
                return null;
            }

            _session = session;
            WorkspaceRoot = workspace.Root;
            Wire(session);
            SetState(SecretaryState.Running, null);
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 起動できなかったことを、起動したことにしない。
            SetState(SecretaryState.Failed, exception.Message);
            return $"秘書を起動できなかった: {exception.Message}";
        }
        finally
        {
            _starting = false;
            if (_session is null)
            {
                PendingWorkspaceRoot = null;
            }
        }
    }

    public Task SendAsync(string text, CancellationToken ct) =>
        _session?.SendUserMessageAsync(text, ct) ?? Task.CompletedTask;

    private void Wire(IStructuredSession session)
    {
        // **発言はライブ経路から**（設計 §17-5）。Observed が運ぶのは観測の要約であって、
        // 会話の中身ではない —— 繋ぎ間違えると、画面にイベントの型名しか出ない。
        session.Spoke += (_, message) => Said?.Invoke(this, message.Text);
        session.Exited += (_, exitCode) =>
        {
            _session = null;
            WorkspaceRoot = null;
            SetState(exitCode == 0 ? SecretaryState.NotStarted : SecretaryState.Failed,
                exitCode == 0 ? null : $"終了コード {exitCode}");
        };

        // 承認の仕組みは部門と共通。**出す場所だけが違う**（§1 / §17-2）。
        session.ApprovalRequested += (_, request) => approvals.Add(new PendingApproval(
            DepartmentLabel, request, tracker: null,
            (decision, reason, token) => session.RespondAsync(request, decision, reason, token),
            ApprovalSource.Secretary));

        session.TurnFinished += (_, verdict) =>
        {
            if (!verdict.Succeeded)
            {
                Said?.Invoke(this, $"（うまくいかなかった: {verdict.Reason}）");
            }
        };
    }

    private void SetState(SecretaryState state, string? reason)
    {
        State = state;
        FailureReason = reason;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// ウィンドウを閉じたら終了する。<b>秘書も対象</b>（設計 §9、2026-09-06 に訂正）——
    /// 「全部門」と書いていたので、部門ではない秘書が責務から漏れていた。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // **起動中でも「もう要らない」と伝わるようにする**（設計 §17-7）。
        // 世代を進めておけば、走っている StartAsync が自分で閉じる。
        _generation++;
        PendingWorkspaceRoot = null;

        // **走っている起動を待つ。** 世代を進めてあるので、向こうは自分で閉じる。
        // 待たずに戻ると、次の起動が「まだ起動中」で弾かれる。
        if (_startTask is { } starting)
        {
            _startTask = null;
            try
            {
                await starting;
            }
            catch (Exception)
            {
                // 起動の失敗はもう関係ない。破棄はここで止まらない。
            }
        }

        if (_session is { } session)
        {
            _session = null;
            WorkspaceRoot = null;
            SetState(SecretaryState.NotStarted, null);
            try
            {
                await session.DisposeAsync();
            }
            catch (Exception)
            {
            }
        }
    }
}
