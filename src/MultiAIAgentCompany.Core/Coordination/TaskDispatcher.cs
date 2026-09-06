using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Sessions;
using MultiAIAgentCompany.Core.Workspace;

namespace MultiAIAgentCompany.Core.Coordination;

/// <summary>
/// 指示書を担当部門へ渡す調整基盤の配線。設計 §14-1 ～ §14-3。
/// </summary>
public sealed class TaskDispatcher
{
    private readonly CompanyPaths _paths;
    private readonly TaskStore _tasks;
    private readonly LeaseStore _leases;
    private readonly TimeProvider _clock;

    public TaskDispatcher(CompanyPaths paths, TaskStore tasks, LeaseStore leases, TimeProvider clock)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _leases = leases ?? throw new ArgumentNullException(nameof(leases));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<DispatchResult> DispatchAsync(
        TaskState expected,
        DepartmentDefinition department,
        IStructuredSession? session,
        TimeSpan leaseDuration,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(department);
        ct.ThrowIfCancellationRequested();

        if (!string.Equals(department.Id, expected.DepartmentId, StringComparison.Ordinal))
        {
            return new DispatchResult.Rejected("送り先の部門がタスクの担当部門と一致しません");
        }

        // **前提の検査は lease と状態の書き込みより前に済ませる。**
        // Dispatched は「送ったかもしれない」を意味し、復旧時に自動再送されない（§14-1）。
        // 送れないと分かっている呼び出しでそれを書くと、誰も進められない状態が残る。
        if (department.Mode is DriveMode.Structured && session is null)
        {
            return new DispatchResult.Rejected("Structured 部門には構造化セッションが必要です");
        }

        var instruction = await ReadInstructionAsync(expected.Slug, ct);
        if (string.IsNullOrWhiteSpace(instruction))
        {
            return new DispatchResult.Rejected("instruction.md が無いか空です");
        }

        var leaseResult = await AcquireOrRenewWriteLeaseAsync(expected, department, leaseDuration, ct);
        if (leaseResult is DispatchResult leaseFailure)
        {
            return leaseFailure;
        }

        var transition = await _tasks.TransitionAsync(
            expected, TaskStatus.Dispatched, TransitionOrigin.Automation, "dispatch を開始した", ct);
        switch (transition)
        {
            // 遷移が通らなかったなら dispatch は始まっていない。
            // 取った lease を握ったままにすると、他の部門が失効まで待たされる（§14-2）。
            case TaskWriteResult.Rejected rejected:
                await ReleaseWriteLeaseAsync(department, ct);
                return new DispatchResult.Rejected(rejected.Reason);
            case TaskWriteResult.Conflicted conflicted:
                await ReleaseWriteLeaseAsync(department, ct);
                return new DispatchResult.Conflicted(conflicted.Reason);
            case TaskWriteResult.Written written:
                if (department.Mode is DriveMode.Tui)
                {
                    return new DispatchResult.NeedsHuman(written.State, "TUI 部門にはアプリが自動送信しません");
                }

                // Structured なのに session が無い場合は最初に弾いてある（この上）。
                // ここに来た時点で必ず非 null なので、コンパイラにもそう伝える。
                ArgumentNullException.ThrowIfNull(session);

                try
                {
                    await session.SendUserMessageAsync(instruction, ct);
                    return new DispatchResult.Dispatched(written.State);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return new DispatchResult.SentUncertain(written.State, $"送信中に例外が発生しました: {exception.Message}");
                }
            default:
                throw new InvalidOperationException("未知の TaskWriteResult です");
        }
    }

    /// <summary>
    /// dispatch が始まらなかったときに Write lease を返す。
    /// <b>ここで失敗しても外へ投げない</b> —— 呼び出し元に返すべきは元の理由であって、
    /// 後始末の失敗ではない。lease は時間でも解ける（§14-2）。
    /// </summary>
    private async Task ReleaseWriteLeaseAsync(DepartmentDefinition department, CancellationToken ct)
    {
        try
        {
            if (await _leases.ReadAsync(ct) is LeaseReadResult.Found found)
            {
                await _leases.ReleaseAsync(found.Leases, LeaseKind.Write, Actor.OfDepartment(department.Id), ct);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// 既に <c>Dispatched</c> の仕事を、<b>同じ試行のまま</b>もう一度送る（設計 §16-4）。
    /// </summary>
    /// <remarks>
    /// <b>状態を動かさない。</b> <c>Dispatched → Dispatched</c> は同状態遷移として
    /// 拒否されるうえ、再送しても「送ったかもしれない」であることは変わらない。
    /// <para>
    /// <b>人間が明示的に選んだときだけ呼ぶ。</b> §14-1 が禁じているのは自動再送。
    /// 既に届いていた場合、同じ指示が二重に実行される。
    /// </para>
    /// <para>
    /// <b>TUI 部門では送らない</b>（§14-3 / §16-2）。人間が手で送る。
    /// </para>
    /// </remarks>
    public async Task<DispatchResult> RetryDeliveryAsync(
        TaskState expected,
        DepartmentDefinition department,
        Sessions.IStructuredSession? session,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(department);

        if (expected.Status is not TaskStatus.Dispatched)
        {
            return new DispatchResult.Rejected("送ったかもしれない仕事だけを再送できる");
        }

        if (!string.Equals(department.Id, expected.DepartmentId, StringComparison.Ordinal))
        {
            return new DispatchResult.Rejected("送り先の部門がタスクの担当部門と一致しません");
        }

        if (department.Mode is DriveMode.Tui)
        {
            return new DispatchResult.NeedsHuman(expected, "TUI 部門にはアプリが自動送信しません");
        }

        if (session is null)
        {
            return new DispatchResult.Rejected("Structured 部門には構造化セッションが必要です");
        }

        var instruction = await ReadInstructionAsync(expected.Slug, ct);
        if (string.IsNullOrWhiteSpace(instruction))
        {
            return new DispatchResult.Rejected("instruction.md が無いか空です");
        }

        try
        {
            await session.SendUserMessageAsync(instruction, ct);

            // 状態は Dispatched のまま。再送しても「送ったかもしれない」は変わらない。
            return new DispatchResult.Dispatched(expected);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new DispatchResult.SentUncertain(expected, $"再送中に例外が発生しました: {exception.Message}");
        }
    }

    /// <summary>
    /// <c>answer.md</c> を部門へ届け、<b>届いたときだけ</b> <c>AwaitingAnswer → InProgress</c> を書く
    /// （設計 §16-5）。
    /// </summary>
    /// <remarks>
    /// <b><c>InProgress</c> を先に書かない。</b> 送る前に落ちたら「部門が作業中」が嘘になる。
    /// 送れていない間の <c>AwaitingAnswer</c> は嘘ではない —— 部門はまだ受け取っていない。
    /// </remarks>
    public async Task<DispatchResult> DeliverAnswerAsync(
        TaskState expected,
        DepartmentDefinition department,
        Sessions.IStructuredSession? session,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(department);

        if (expected.Status is not TaskStatus.AwaitingAnswer)
        {
            return new DispatchResult.Rejected("回答を待っている仕事だけに届けられる");
        }

        if (department.Mode is DriveMode.Tui)
        {
            return new DispatchResult.NeedsHuman(expected, "TUI 部門にはアプリが自動送信しません");
        }

        if (session is null)
        {
            return new DispatchResult.Rejected("部門が動いていないので届けられない（先に起動する）");
        }

        var answerPath = _paths.Answer(expected.Slug);
        if (!File.Exists(answerPath))
        {
            return new DispatchResult.Rejected("answer.md がまだ無い");
        }

        var answer = await File.ReadAllTextAsync(answerPath, ct);
        if (string.IsNullOrWhiteSpace(answer))
        {
            return new DispatchResult.Rejected("answer.md が空");
        }

        // 本文だけでは弱い。セッションの文脈が残っているとは限らないので、何への回答かを明示する。
        var message = $"""
            これは {_paths.Question(expected.Slug)} への人間の回答です。
            task: {expected.Slug}
            attemptId: {expected.AttemptId}

            {answer.Trim()}

            この回答に従って作業を再開してください。
            迷う場合は新しい question.md を publish し、完了したら report.md を publish してください。
            """;

        try
        {
            await session.SendUserMessageAsync(message, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 届いたかもしれない。**自動で再送しない**（§14-1 と同じ姿勢）。
            return new DispatchResult.SentUncertain(expected, $"回答の送信中に例外が発生しました: {exception.Message}");
        }

        var transition = await _tasks.TransitionAsync(
            expected, TaskStatus.InProgress, TransitionOrigin.Automation, "回答を届けた", ct);

        return transition switch
        {
            TaskWriteResult.Written written => new DispatchResult.Dispatched(written.State),
            TaskWriteResult.Conflicted conflicted => new DispatchResult.Conflicted(conflicted.Reason),
            TaskWriteResult.Rejected rejected => new DispatchResult.Rejected(rejected.Reason),
            _ => new DispatchResult.Rejected("状態を進められなかった"),
        };
    }

    private async Task<string?> ReadInstructionAsync(string slug, CancellationToken ct)
    {
        try
        {
            return File.Exists(_paths.Instruction(slug))
                ? await File.ReadAllTextAsync(_paths.Instruction(slug), ct)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<DispatchResult?> AcquireOrRenewWriteLeaseAsync(
        TaskState expected, DepartmentDefinition department, TimeSpan leaseDuration, CancellationToken ct)
    {
        var read = await _leases.ReadAsync(ct);
        if (read is LeaseReadResult.Unreadable unreadable)
        {
            return new DispatchResult.Conflicted($"lease.json を検証できません: {unreadable.Reason}");
        }

        var leases = ((LeaseReadResult.Found)read).Leases;
        LeaseWriteResult write;
        var actor = Actor.OfDepartment(department.Id);
        if (leases.IsHeldBy(LeaseKind.Write, actor, _clock.GetUtcNow()))
        {
            write = await _leases.RenewAsync(leases, LeaseKind.Write, actor, leaseDuration, ct);
        }
        else
        {
            write = await _leases.AcquireAsync(leases, LeaseKind.Write, actor, expected.Slug,
                leaseDuration, LeaseTakeover.Deny, ct);
        }

        return write switch
        {
            LeaseWriteResult.Written => null,
            LeaseWriteResult.Denied denied => new DispatchResult.Blocked(denied.Reason, denied.Holder),
            LeaseWriteResult.Conflicted conflicted => new DispatchResult.Conflicted(conflicted.Reason),
            LeaseWriteResult.NotHeld notHeld => new DispatchResult.Conflicted(notHeld.Reason),
            _ => throw new InvalidOperationException("未知の LeaseWriteResult です"),
        };
    }
}

public abstract record DispatchResult
{
    public sealed record Dispatched(TaskState State) : DispatchResult;

    public sealed record NeedsHuman(TaskState State, string Reason) : DispatchResult;

    public sealed record Blocked(string Reason, LeaseHolder Holder) : DispatchResult;

    public sealed record Rejected(string Reason) : DispatchResult;

    public sealed record Conflicted(string Reason) : DispatchResult;

    public sealed record SentUncertain(TaskState State, string Reason) : DispatchResult;
}
