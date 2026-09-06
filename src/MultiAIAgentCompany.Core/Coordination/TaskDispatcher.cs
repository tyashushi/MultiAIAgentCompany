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
                await _leases.ReleaseAsync(found.Leases, LeaseKind.Write, department.Id, ct);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
        }
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
        if (leases.IsHeldBy(LeaseKind.Write, department.Id, _clock.GetUtcNow()))
        {
            write = await _leases.RenewAsync(leases, LeaseKind.Write, department.Id, leaseDuration, ct);
        }
        else
        {
            write = await _leases.AcquireAsync(leases, LeaseKind.Write, department.Id, expected.Slug,
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
