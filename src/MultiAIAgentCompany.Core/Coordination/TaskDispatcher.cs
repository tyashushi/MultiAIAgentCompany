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

        // **読むだけの部門は書き込み権を「取らない」。ただし「無視しない」**
        // （設計 §29-1、レビューで発覚）。取らないだけにすると、
        // **書いている最中の作業ツリーを読む**ことになり、途中の状態を読んで誤った指摘を出す。
        // 避けたかったのは「読む部門どうしの直列化」であって、書き手との排他ではない。
        var leaseResult = department.ReadsOnly
            ? await BlockWhileWritingAsync(ct)
            : await AcquireOrRenewWriteLeaseAsync(expected, department, leaseDuration, ct);
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
                await ReleaseIfHeldAsync(department, ct);
                return new DispatchResult.Rejected(rejected.Reason);
            case TaskWriteResult.Conflicted conflicted:
                await ReleaseIfHeldAsync(department, ct);
                return new DispatchResult.Conflicted(conflicted.Reason);
            case TaskWriteResult.Written written:
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
    /// 書いている部門が居る間は渡さない（設計 §29-1）。<b>権利は取らない。</b>
    /// </summary>
    /// <remarks>
    /// 読むだけの部門どうしは**互いに待たない**が、**書き手とは待つ** ——
    /// 書き換え中の作業ツリーを読むと、途中の状態を読んで誤った指摘を出す。
    /// <b>失効した保持者は書いていない</b>ので、そこは通す（§24 と同じ判定）。
    /// </remarks>
    private async Task<DispatchResult?> BlockWhileWritingAsync(CancellationToken ct)
    {
        // 「読めない」以外を Found と決めつけない。**種類が増えたときに落ちる**（レビューで指摘）。
        if (await _leases.ReadAsync(ct) is not LeaseReadResult.Found found)
        {
            return new DispatchResult.Conflicted("lease.json を検証できません");
        }

        return found.Leases.Holders.TryGetValue(LeaseKind.Write, out var holder)
                && holder.IsValidAt(_clock.GetUtcNow())
            ? new DispatchResult.Blocked("書き込み中の部門がいます（読むだけの仕事も、書き終わるまで待つ）", holder)
            : null;
    }

    /// <summary>
    /// dispatch が始まらなかったときに Write lease を返す。
    /// <b>ここで失敗しても外へ投げない</b> —— 呼び出し元に返すべきは元の理由であって、
    /// 後始末の失敗ではない。lease は時間でも解ける（§14-2）。
    /// </summary>
    /// <remarks>取っていない部門（<c>ReadsOnly</c>）では何もしない（設計 §29-1）。</remarks>
    private Task ReleaseIfHeldAsync(DepartmentDefinition department, CancellationToken ct) =>
        department.ReadsOnly ? Task.CompletedTask : ReleaseWriteLeaseAsync(department, ct);

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

        if (session is null)
        {
            return new DispatchResult.Rejected("部門が動いていないので届けられない（先に起動する）");
        }

        var answerPath = _paths.Answer(expected.Slug);
        if (!File.Exists(answerPath))
        {
            return new DispatchResult.Rejected("answer.md がまだ無い");
        }

        // **1回だけ読む**（設計 §20-2 は回答側にも掛かる）。読み直して hash を取ると、
        // その間に人間が書き換えたとき「送っていない bytes を送った」記録ができ、
        // 次の質問への回答が §20-4 の番人に「前回と同じ」と誤判定される。
        var answerBytes = await File.ReadAllBytesAsync(answerPath, ct);
        var answerDigest = CompanyDigest.OfBytes(answerBytes);
        var answer = DecodeUtf8(answerBytes);
        if (string.IsNullOrWhiteSpace(answer))
        {
            return new DispatchResult.Rejected("answer.md が空");
        }

        // **送る前に読む。読み直さない**（設計 §20-2）。送信後に question.md を読み直すと、
        // その間に publish された2度目の質問を「回答済み」として記録してしまう。
        if (await CompanyDigest.OfFileAsync(_paths.Question(expected.Slug), ct) is not { } questionDigest)
        {
            return new DispatchResult.Rejected("question.md が無い（何への回答か分からない）");
        }

        // **古い回答を新しい質問への回答として送らない**（設計 §20-4）。
        // 送れてしまうと、部門には噛み合わない回答が届くだけで、失敗にも見えない。
        if (expected.AnswerDelivery is { } previous
            && previous.AttemptId == expected.AttemptId
            && string.Equals(previous.AnswerSha256, answerDigest, StringComparison.Ordinal)
            && !string.Equals(previous.QuestionSha256, questionDigest, StringComparison.Ordinal))
        {
            return new DispatchResult.Rejected("answer.md が前回届けた回答のままです（新しい質問への回答を書く）");
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

        var transition = await _tasks.RecordAnswerDeliveryAsync(
            expected,
            new AnswerDelivery(expected.AttemptId, questionDigest, answerDigest, _clock.GetUtcNow()),
            ct);

        return transition switch
        {
            TaskWriteResult.Written written => new DispatchResult.Dispatched(written.State),
            TaskWriteResult.Conflicted conflicted => new DispatchResult.Conflicted(conflicted.Reason),
            TaskWriteResult.Rejected rejected => new DispatchResult.Rejected(rejected.Reason),
            _ => new DispatchResult.Rejected("状態を進められなかった"),
        };
    }

    /// <summary>
    /// 差し戻された仕事を次の試行へ進めて送る（設計 §19-1）。
    /// </summary>
    /// <remarks>
    /// <b><see cref="DispatchAsync"/> を使えない。</b> あちらは <c>instruction.md</c> を
    /// <b>読んでから</b>遷移するが、<c>Rejected → Dispatched</c> はその <c>instruction.md</c> を
    /// <c>attempts/</c> へ封じる —— 古い指示を送ったうえ、新しい試行に指示書が残らない（§15-10）。
    /// <para>
    /// <b><see cref="RetryDeliveryAsync"/> とも違う。</b> あれは同じ試行の再送。
    /// </para>
    /// </remarks>
    public async Task<DispatchResult> RedispatchAsync(
        TaskState expected,
        DepartmentDefinition department,
        IStructuredSession? session,
        TimeSpan leaseDuration,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(department);

        if (!string.Equals(department.Id, expected.DepartmentId, StringComparison.Ordinal))
        {
            return new DispatchResult.Rejected("送り先の部門がタスクの担当部門と一致しません");
        }

        if (department.Mode is DriveMode.Structured && session is null)
        {
            return new DispatchResult.Rejected("Structured 部門には構造化セッションが必要です");
        }

        // 読むだけの部門は取らないが、書き手が居る間は待つ（設計 §29-1）。送り直しでも同じ。
        var leaseResult = department.ReadsOnly
            ? await BlockWhileWritingAsync(ct)
            : await AcquireOrRenewWriteLeaseAsync(expected, department, leaseDuration, ct);
        if (leaseResult is DispatchResult leaseFailure)
        {
            return leaseFailure;
        }

        // 封じる → 昇格する → 状態を書く、までを TaskStore が1つの操作でやる（§19-1 / §14-1）。
        var transition = await _tasks.RedispatchAsync(expected, ct);
        switch (transition)
        {
            case TaskWriteResult.Rejected rejected:
                await ReleaseIfHeldAsync(department, ct);
                return new DispatchResult.Rejected(rejected.Reason);
            case TaskWriteResult.Conflicted conflicted:
                await ReleaseIfHeldAsync(department, ct);
                return new DispatchResult.Conflicted(conflicted.Reason);
        }

        var written = ((TaskWriteResult.Written)transition).State;

        // ここで読むのは**昇格したあとの** instruction.md。
        var instruction = await ReadInstructionAsync(written.Slug, ct);
        if (string.IsNullOrWhiteSpace(instruction))
        {
            return new DispatchResult.SentUncertain(written, "昇格した instruction.md を読めなかった");
        }

        try
        {
            await session!.SendUserMessageAsync(instruction, ct);
            return new DispatchResult.Dispatched(written);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 状態は巻き戻さない（§14-1）。Dispatched は「送ったかもしれない」。
            return new DispatchResult.SentUncertain(written, $"送信中に例外が発生しました: {exception.Message}");
        }
    }

    /// <summary>BOM 付きで publish されても本文だけを送る。</summary>
    private static string DecodeUtf8(byte[] bytes) =>
        System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');

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

            // **待っても空かない**（設計 §24-1）。UI に「待つ」と言わせないため型で分ける。
            LeaseWriteResult.DeniedExpired expired =>
                new DispatchResult.BlockedByExpiredLease(expired.Reason, expired.Holder),
            LeaseWriteResult.Conflicted conflicted => new DispatchResult.Conflicted(conflicted.Reason),
            LeaseWriteResult.NotHeld notHeld => new DispatchResult.Conflicted(notHeld.Reason),
            _ => throw new InvalidOperationException("未知の LeaseWriteResult です"),
        };
    }
}

public abstract record DispatchResult
{
    public sealed record Dispatched(TaskState State) : DispatchResult;

    // **`NeedsHuman` は消した**（設計 §22-4、2026-09-06）。
    // 「TUI 部門なのでアプリは送らない」という唯一の用途が無くなった。
    // 人間の出番は `DepartmentCallToAction.NeedsHuman`（§15-6）が受け持つ ——
    // 同じ名前で意味の違うものを2つ置かない。

    /// <summary>有効な保持者がいて渡せない。<b>待てば空く可能性がある。</b></summary>
    public sealed record Blocked(string Reason, LeaseHolder Holder) : DispatchResult;

    /// <summary>
    /// 失効した保持者がいて渡せない（設計 §24）。<b>待っても空かない。</b>
    /// 人間が「確かめてほしいこと」から外すまで、このワークスペースでは誰にも渡せない。
    /// </summary>
    public sealed record BlockedByExpiredLease(string Reason, LeaseHolder Holder) : DispatchResult;

    public sealed record Rejected(string Reason) : DispatchResult;

    public sealed record Conflicted(string Reason) : DispatchResult;

    public sealed record SentUncertain(TaskState State, string Reason) : DispatchResult;
}
