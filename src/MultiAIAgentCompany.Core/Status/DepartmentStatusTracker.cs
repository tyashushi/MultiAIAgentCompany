using MultiAIAgentCompany.Core.Agents;

namespace MultiAIAgentCompany.Core.Status;

/// <summary>1部門の稼働・活動・仕事を混ぜずに保持する検出器。</summary>
public sealed class DepartmentStatusTracker
{
    private readonly AgentRef _agent;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _evidenceMaxAge;
    private readonly Dictionary<string, PendingApproval> _pendingApprovals = new(StringComparer.Ordinal);
    private DepartmentStatus _status;
    private readonly Evidence _notStarted;
    private bool _isUpdating;
    private bool _changedDuringUpdate;

    public DepartmentStatusTracker(AgentRef agent, TimeProvider clock, TimeSpan evidenceMaxAge)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (evidenceMaxAge < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(evidenceMaxAge));

        _agent = agent;
        _clock = clock;
        _evidenceMaxAge = evidenceMaxAge;
        var initial = NewEvidence(EvidenceSource.Dispatch, "状態検出器を初期化した");
        // **まだ何も起動していない部門は「手が空いている」**（2026-09-17、人間の要望）。
        // 観測ではなく「アプリがまだ起動していない」というアプリ自身の事実。起動したら外す（OnStarting）。
        _notStarted = NewEvidence(EvidenceSource.Dispatch, "まだ起動していない");
        _status = new DepartmentStatus(
            new Observed<RuntimeState>(RuntimeState.Starting, initial),
            new Observed<ActivityState>(ActivityState.Resting, _notStarted),
            Work: null);
    }

    /// <summary>現在の3軸。活動の根拠は読むたびに鮮度を再評価する。</summary>
    /// <remarks>
    /// <b>ただし未解決の承認・相談は時間で消えない。</b>
    /// §7 の「古い根拠は現在の証拠ではない」は<b>推定した状態</b>に効く規則であって、
    /// 「人間の返事を待っている」という<b>既知の事実</b>には効かない ——
    /// 人間が1時間悩むのは正常であり、そこで Unknown へ落とすと
    /// <b>人間がやるべき唯一のことが画面から消える</b>。
    /// </remarks>
    public DepartmentStatus Current
    {
        get
        {
            if (!HasPendingApproval
                && !NotStartedYet
                && _status.Activity.Evidence.Source is not EvidenceSource.LifecycleHook
                && !_status.Activity.Evidence.IsFreshAt(_clock.GetUtcNow(), _evidenceMaxAge))
            {
                Update(() => SetActivity(ActivityState.Unknown, _status.Activity.Evidence));
            }

            return _status;
        }
    }

    /// <summary>まだ人間が答えていない承認・相談があるか。</summary>
    private bool HasPendingApproval => _pendingApprovals.Count > 0;

    public event EventHandler<DepartmentStatus>? Changed;

    /// <summary>まだ一度も起動していないので「手が空いている」と出しているか。時間では消さない。</summary>
    public bool NotStartedYet => ReferenceEquals(_status.Activity.Evidence, _notStarted);

    public void OnStarting() => Update(() =>
    {
        var evidence = NewEvidence(EvidenceSource.Dispatch, "プロセスを開始した");
        SetRuntime(RuntimeState.Starting, evidence);
        // 起動したら「まだ起動していない」は事実でなくなる。次の観測までは分からない。
        if (NotStartedYet) SetActivity(ActivityState.Unknown, evidence);
    });

    public void OnObserved(Evidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        Update(() =>
        {
            // 何か観測が来たなら、もう「まだ起動していない」ではない。
            if (NotStartedYet) SetActivity(ActivityState.Unknown, evidence);
            if (_status.Runtime.Value is not (RuntimeState.Exited or RuntimeState.Failed)) SetRuntime(RuntimeState.Running, evidence);
            // **未解決の承認・相談が活動状態を握り続ける。**
            // 承認待ちの最中にも構造化イベントは流れてくるが、それで Working に戻すと、
            // 人間が動かないと進まないという事実が画面から消える（§3）。
            // 解除されるのは、答えたとき・turn が終わったとき・プロセスが落ちたときだけ。
            if (HasPendingApproval)
            {
                return;
            }

            // Dispatch は「投げた」だけで、Working の根拠にならない。画面解析も今回の対象外。
            if (_status.Runtime.Value is not (RuntimeState.Exited or RuntimeState.Failed)
                && evidence.Source is EvidenceSource.StructuredEvent)
            {
                SetActivity(ActivityState.Working, evidence);
            }
        });
    }

    /// <summary>起動ごとのフック未観測の案内。付け直しには立てない（設計 §61-5）。</summary>
    public bool AwaitingFirstLifecycleHook { get; private set; }

    public void OnLifecycleStarted() => Update(() =>
    {
        AwaitingFirstLifecycleHook = true;
        ClearPreviousLifecycleActivity();
        NotifyChanged();
    });

    /// <summary>付け直しで観測先が分からなければ前の起動の状態を引き継がない（設計 §61-2 / §61-5）。</summary>
    public void OnLifecycleUnavailable() => Update(() =>
    {
        AwaitingFirstLifecycleHook = false;
        ClearPreviousLifecycleActivity();
        NotifyChanged();
    });

    private void ClearPreviousLifecycleActivity()
    {
        if (!HasPendingApproval && _status.Activity.Evidence.Source is EvidenceSource.LifecycleHook)
            SetActivity(ActivityState.Unknown, NewEvidence(EvidenceSource.Dispatch, "起動後のフックの観測を待っている"));
    }

    /// <summary>外部ターミナル専用。構造化の承認・相談には触れない（設計 §61-3 / §61-4）。</summary>
    public void OnLifecycleActivity(Observed<ActivityState> observation, bool hasHookEvent = true)
    {
        if (observation.Evidence.Source != EvidenceSource.LifecycleHook)
            throw new ArgumentException("フックの根拠が必要です", nameof(observation));
        Update(() =>
        {
            if (_status.Runtime.Value is RuntimeState.Exited or RuntimeState.Failed) return;
            if (hasHookEvent) AwaitingFirstLifecycleHook = false;
            if (!HasPendingApproval)
            {
                SetRuntime(RuntimeState.Running, observation.Evidence);
                // 秒が同じでも、最後の行の根拠を採る（設計 §61-3 / §61-4）。
                _status = _status with { Activity = observation };
            }
            // 同じ状態でもイベント名・時刻と信頼の案内を更新する（設計 §61-4 / §61-5）。
            NotifyChanged();
        });
    }

    public void OnApprovalRequested(ApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var activity = request.Kind switch
        {
            ApprovalKind.Runtime => ActivityState.AwaitingApproval,
            ApprovalKind.Consultation => ActivityState.Consulting,
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
        var evidence = NewEvidence(EvidenceSource.StructuredEvent,
            request.Kind is ApprovalKind.Runtime ? "ランタイム承認を要求した" : "判断の相談を要求した",
            request.SessionId, request.TurnId);

        Update(() =>
        {
            _pendingApprovals[request.RequestId] = new PendingApproval(activity, evidence);
            if (_status.Runtime.Value is not (RuntimeState.Exited or RuntimeState.Failed)) SetActivity(activity, evidence);
        });
    }

    public void OnApprovalResolved(string requestId)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestId);
        Update(() =>
        {
            if (!_pendingApprovals.Remove(requestId) || _status.Runtime.Value is RuntimeState.Exited or RuntimeState.Failed) return;
            if (_pendingApprovals.Count == 0)
            {
                // 決定送信は、エージェントが再開した証拠ではない。
                SetActivity(ActivityState.Unknown, NewEvidence(EvidenceSource.Dispatch, "承認要求を解決した。次の観測を待っている"));
                return;
            }

            var remaining = _pendingApprovals.Values.Aggregate((best, candidate) =>
                Evidence.Stronger(best.Evidence, candidate.Evidence) == candidate.Evidence ? candidate : best);
            SetActivity(remaining.Activity, remaining.Evidence);
        });
    }

    public void OnTurnFinished(OutcomeVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        Update(() =>
        {
            _pendingApprovals.Clear();
            if (_status.Runtime.Value is not (RuntimeState.Exited or RuntimeState.Failed))
            {
                SetActivity(verdict.Succeeded ? ActivityState.Resting : ActivityState.Degraded,
                    NewEvidence(EvidenceSource.StructuredEvent, verdict.Succeeded ? "turn が成功して終了した" : "turn が失敗して終了した"));
            }
        });
    }

    public void OnExited(int exitCode)
    {
        Update(() =>
        {
            _pendingApprovals.Clear();
            var evidence = NewEvidence(EvidenceSource.ProcessExit, $"プロセスが exit code {exitCode} で終了した");
            SetRuntime(exitCode == 0 ? RuntimeState.Exited : RuntimeState.Failed, evidence);
            // プロセス終了は休止の根拠ではない。
            SetActivity(ActivityState.Unknown, evidence);
        });
    }

    /// <summary>
    /// プロセスがもう居ないと分かった（設計 §32）。<b>終了コードは観測していない。</b>
    /// </summary>
    /// <remarks>
    /// <b><see cref="OnExited"/> と分ける。</b> あちらに <c>0</c> を渡すと
    /// 「exit code 0 で終了した」と記録され、**観測していないことを言う**ことになる。
    /// 外部ターミナル（§32）では終了コードを受け取る経路が無い ——
    /// 分かるのは「もう居ない」だけである。
    /// </remarks>
    public void OnDisappeared() => Update(() =>
    {
        _pendingApprovals.Clear();
        var evidence = NewEvidence(EvidenceSource.ProcessExit, "プロセスがもう居ない（終了コードは観測していない）");

        // **意図した終了として扱う。** 人間が窓を閉じたのがふつうで、
        // 落ちたと決めつけると ⚠ が出て「原因を見る」を押させることになる（§15-4）。
        SetRuntime(RuntimeState.Exited, evidence);
        SetActivity(ActivityState.Unknown, evidence);
    });

    /// <summary>
    /// この部門に読める仕事が無くなった。<b>状態を消すのも観測である</b> ——
    /// 残しておくと、別のワークスペースに切り替えたあとも古い報告まちを指し続ける。
    /// </summary>
    public void OnWorkStateCleared() => Update(() =>
    {
        if (_status.Work is not null)
        {
            _status = _status with { Work = null };
        }
    });

    public void OnWorkStateChanged(Coordination.TaskStatus status, Evidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Source is not EvidenceSource.Document)
            throw new ArgumentException("仕事状態の根拠は Document でなければならない。", nameof(evidence));
        Update(() => SetWork(status, evidence));
    }

    private Evidence NewEvidence(EvidenceSource source, string summary, string? sessionId = null, string? turnId = null) =>
        new(source, _clock.GetUtcNow(), sessionId, turnId, _agent, null, null, summary);

    private void SetRuntime(RuntimeState state, Evidence evidence)
    {
        var same = _status.Runtime.Value == state;
        _status = _status with { Runtime = new Observed<RuntimeState>(state, same ? SelectEvidence(_status.Runtime, evidence) : evidence) };
        if (!same) NotifyChanged();
    }

    private void SetActivity(ActivityState state, Evidence evidence)
    {
        var same = _status.Activity.Value == state;
        _status = _status with { Activity = new Observed<ActivityState>(state, same ? SelectEvidence(_status.Activity, evidence) : evidence) };
        if (!same) NotifyChanged();
    }

    private void SetWork(Coordination.TaskStatus state, Evidence evidence)
    {
        var current = _status.Work;
        var same = current?.Value == state;
        _status = _status with { Work = new Observed<Coordination.TaskStatus>(state, current is null || !same ? evidence : SelectEvidence(current, evidence)) };
        if (!same) NotifyChanged();
    }

    private static Evidence SelectEvidence<T>(Observed<T> current, Evidence incoming) => Evidence.Stronger(current.Evidence, incoming);

    private void Update(Action action)
    {
        _isUpdating = true;
        try { action(); }
        finally
        {
            _isUpdating = false;
            if (_changedDuringUpdate)
            {
                _changedDuringUpdate = false;
                Changed?.Invoke(this, _status);
            }
        }
    }

    private void NotifyChanged()
    {
        if (_isUpdating) { _changedDuringUpdate = true; return; }
        Changed?.Invoke(this, _status);
    }

    private sealed record PendingApproval(ActivityState Activity, Evidence Evidence);
}
