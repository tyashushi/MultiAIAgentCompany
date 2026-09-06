using System.Text.Json;

namespace MultiAIAgentCompany.Core.Coordination;

/// <summary>
/// <c>.company/tasks</c> にある仕事状態の唯一の保存口。
/// 設計 §14-1 により、<c>state.json</c> を書くのはこの型だけである。
/// </summary>
public sealed class TaskStore
{
    private const string StateFileName = "state.json";
    // rejection.md も封じる（設計 §19-2）—— その試行の報告に対する人間の判断なので。
    // next-instruction.md は**入れない**（§19-1）—— 入れると新しい指示が過去試行へ移る。
    private static readonly string[] AttemptFiles =
        ["instruction.md", "report.md", "question.md", "answer.md", "rejection.md"];

    private readonly CompanyPaths _paths;
    private readonly TimeProvider _clock;

    public TaskStore(CompanyPaths paths, TimeProvider clock)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public Task<IReadOnlyList<string>> ListSlugsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!Directory.Exists(_paths.TasksRoot))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        // slug として使えない名前のディレクトリは仕事ではない。
        // 素通しすると ReadAsync が CompanyPaths の検査で例外を投げ、
        // 起動時の復旧走査が「迷子のディレクトリ1つ」で落ちる。
        IReadOnlyList<string> slugs = Directory.EnumerateDirectories(_paths.TasksRoot)
            .Select(Path.GetFileName)
            .Where(CompanyPaths.IsValidSlug)
            .Cast<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult(slugs);
    }

    public async Task<TaskReadResult> ReadAsync(string slug, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var directory = _paths.TaskDirectory(slug);
        var statePath = Path.Combine(directory, StateFileName);
        if (!Directory.Exists(directory) || !File.Exists(statePath))
        {
            return new TaskReadResult.Missing();
        }

        try
        {
            await using var stream = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, useAsync: true);
            var state = await JsonSerializer.DeserializeAsync<TaskState>(stream, TaskStateJson.Options, ct);
            if (state is null)
            {
                return new TaskReadResult.Unreadable("state.json が空です");
            }

            if (!string.Equals(state.Slug, slug, StringComparison.Ordinal))
            {
                return new TaskReadResult.Unreadable("state.json の Slug がディレクトリ名と一致しません");
            }

            if (!Enum.IsDefined(state.Status) || !Enum.IsDefined(state.LastTransitionOrigin))
            {
                return new TaskReadResult.Unreadable("state.json に未定義の列挙値があります");
            }

            return new TaskReadResult.Found(state);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new TaskReadResult.Unreadable($"state.json を読めません: {exception.Message}");
        }
    }

    public async Task<TaskWriteResult> CreateAsync(string slug, string departmentId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var directory = _paths.TaskDirectory(slug);
        var statePath = _paths.State(slug);
        Directory.CreateDirectory(directory);

        if (File.Exists(statePath))
        {
            return new TaskWriteResult.Rejected("既にある");
        }

        var state = new TaskState(slug, TaskStatus.Drafted, 0, 1, departmentId,
            TransitionOrigin.Automation, _clock.GetUtcNow());
        try
        {
            await WriteStateAtomicallyAsync(statePath, state, overwrite: false, ct);
            return new TaskWriteResult.Written(state);
        }
        catch (IOException) when (File.Exists(statePath))
        {
            // 確認から置換までの間に別のアプリが作成した場合も、既存状態を上書きしない。
            return new TaskWriteResult.Rejected("既にある");
        }
    }

    public async Task<TaskWriteResult> TransitionAsync(
        TaskState expected,
        TaskStatus to,
        TransitionOrigin origin,
        string? note,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ct.ThrowIfCancellationRequested();

        var read = await ReadAsync(expected.Slug, ct);
        if (read is TaskReadResult.Missing)
        {
            return new TaskWriteResult.Rejected("state.json が存在しません");
        }

        if (read is TaskReadResult.Unreadable unreadable)
        {
            return new TaskWriteResult.Conflicted($"state.json を検証できません: {unreadable.Reason}");
        }

        var current = ((TaskReadResult.Found)read).State;
        if (current.Revision != expected.Revision)
        {
            return new TaskWriteResult.Conflicted(
                $"Revision が一致しません（expected: {expected.Revision}, actual: {current.Revision}）");
        }

        var check = TaskTransitions.Check(current.Status, to, origin);
        if (!check.Allowed)
        {
            return new TaskWriteResult.Rejected(check.Reason);
        }

        // **差し戻しからの再送はここを通さない**（設計 §19-1）。
        // 封じるだけでは次の試行に指示書が残らない。昇格と対にする必要があるので、
        // 入口を <see cref="RedispatchAsync"/> ひとつに閉じる。
        if (current.Status is TaskStatus.Rejected && to is TaskStatus.Dispatched)
        {
            return new TaskWriteResult.Rejected("差し戻しから送り直すには RedispatchAsync を使う");
        }

        var next = current with
        {
            Status = to,
            AttemptId = current.AttemptId,
            Revision = checked(current.Revision + 1),
            LastTransitionOrigin = origin,
            UpdatedAt = _clock.GetUtcNow(),
            Note = note,
        };
        await WriteStateAtomicallyAsync(_paths.State(expected.Slug), next, overwrite: true, ct);
        return new TaskWriteResult.Written(next);
    }

    /// <summary>
    /// 差し戻された仕事を、次の試行として <c>Dispatched</c> にする（設計 §19-1）。
    /// </summary>
    /// <remarks>
    /// <b>順序が本体である。</b> 封じる → 昇格する → 状態を書く。
    /// 逆にすると、次の指示書が <c>attempts/</c> へ一緒に封じられて消える
    /// （<c>instruction.md</c> は封じる対象そのものだから）。
    /// <para>
    /// <c>next-instruction.md</c> が無いなら<b>何も書かない。</b> 指示書の無い
    /// <c>Dispatched</c> は、部門が受け取れないまま「送ったかもしれない」に見える。
    /// </para>
    /// </remarks>
    public async Task<TaskWriteResult> RedispatchAsync(TaskState expected, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ct.ThrowIfCancellationRequested();

        var read = await ReadAsync(expected.Slug, ct);
        if (read is TaskReadResult.Missing)
        {
            return new TaskWriteResult.Rejected("state.json が存在しません");
        }

        if (read is TaskReadResult.Unreadable unreadable)
        {
            return new TaskWriteResult.Conflicted($"state.json を検証できません: {unreadable.Reason}");
        }

        var current = ((TaskReadResult.Found)read).State;
        if (current.Revision != expected.Revision)
        {
            return new TaskWriteResult.Conflicted(
                $"Revision が一致しません（expected: {expected.Revision}, actual: {current.Revision}）");
        }

        if (current.Status is not TaskStatus.Rejected)
        {
            return new TaskWriteResult.Rejected($"差し戻された仕事ではありません（現在: {current.Status}）");
        }

        var check = TaskTransitions.Check(current.Status, TaskStatus.Dispatched, TransitionOrigin.Human);
        if (!check.Allowed)
        {
            return new TaskWriteResult.Rejected(check.Reason);
        }

        // **書き始める前に確かめる。** 昇格するものが無いなら、封じた時点で
        // 過去の試行だけが消えて次の指示は現れない。
        var staging = _paths.NextInstruction(expected.Slug);
        if (!File.Exists(staging) || (await File.ReadAllTextAsync(staging, ct)).Trim().Length is 0)
        {
            return new TaskWriteResult.Rejected("next-instruction.md が無いか空です");
        }

        MoveCurrentAttempt(expected.Slug, current.AttemptId);
        File.Move(staging, _paths.Instruction(expected.Slug), overwrite: true);

        var next = current with
        {
            Status = TaskStatus.Dispatched,
            AttemptId = checked(current.AttemptId + 1),
            Revision = checked(current.Revision + 1),
            LastTransitionOrigin = TransitionOrigin.Human,
            UpdatedAt = _clock.GetUtcNow(),
            Note = "差し戻しから次の試行を送った",
        };
        await WriteStateAtomicallyAsync(_paths.State(expected.Slug), next, overwrite: true, ct);
        return new TaskWriteResult.Written(next);
    }

    /// <summary>
    /// 再起動時に人間へ見せるもの。設計 §14-1。
    /// </summary>
    /// <remarks>
    /// <b>読めなかったタスクも一緒に返す。</b> 握りつぶすと、壊れた <c>state.json</c> が
    /// 「仕事が無い」と区別できなくなる（§7 の「不明を Resting にしない」と同じ理由）。
    /// <para>
    /// <c>Dispatched</c> は「送ったかもしれない」を意味する。<b>自動で再送しない</b> ——
    /// 呼び出し元は人間に確認を求めること。
    /// </para>
    /// </remarks>
    public async Task<TaskRecoveryScan> ScanForRecoveryAsync(CancellationToken ct)
    {
        var dispatched = new List<TaskState>();
        var unreadable = new List<UnreadableTask>();

        foreach (var slug in await ListSlugsAsync(ct))
        {
            switch (await ReadAsync(slug, ct))
            {
                case TaskReadResult.Found { State.Status: TaskStatus.Dispatched } found:
                    dispatched.Add(found.State);
                    break;
                case TaskReadResult.Unreadable broken:
                    unreadable.Add(new UnreadableTask(slug, broken.Reason));
                    break;
            }
        }

        return new TaskRecoveryScan(dispatched, unreadable);
    }

    private void MoveCurrentAttempt(string slug, int attemptId)
    {
        var taskDirectory = _paths.TaskDirectory(slug);
        var destination = _paths.AttemptDirectory(slug, attemptId);
        Directory.CreateDirectory(destination);

        foreach (var fileName in AttemptFiles)
        {
            var source = Path.Combine(taskDirectory, fileName);
            if (File.Exists(source))
            {
                File.Move(source, Path.Combine(destination, fileName));
            }
        }
    }

    private static async Task WriteStateAtomicallyAsync(string statePath, TaskState state, bool overwrite, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(statePath)!;
        var temporaryPath = Path.Combine(directory, $".{StateFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, state, TaskStateJson.Options, ct);
                await stream.FlushAsync(ct);
            }

            File.Move(temporaryPath, statePath, overwrite);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

/// <summary>再起動時の走査結果。設計 §14-1。</summary>
/// <param name="Dispatched">
/// 送ったかもしれない仕事。<b>自動で再送しない。</b> 人間に確認を求める。
/// </param>
/// <param name="Unreadable">
/// <c>state.json</c> を読めなかった仕事。人間が手で直せるファイルなので壊れているのは想定内。
/// 黙って捨てず、人間に見せる。
/// </param>
public sealed record TaskRecoveryScan(
    IReadOnlyList<TaskState> Dispatched,
    IReadOnlyList<UnreadableTask> Unreadable);

/// <param name="Slug">読めなかった仕事。</param>
/// <param name="Reason">人間に見せる理由。秘密値を入れない。</param>
public sealed record UnreadableTask(string Slug, string Reason);

public abstract record TaskReadResult
{
    public sealed record Found(TaskState State) : TaskReadResult;
    public sealed record Missing : TaskReadResult;
    public sealed record Unreadable(string Reason) : TaskReadResult;
}

public abstract record TaskWriteResult
{
    public sealed record Written(TaskState State) : TaskWriteResult;
    public sealed record Rejected(string Reason) : TaskWriteResult;
    public sealed record Conflicted(string Reason) : TaskWriteResult;
}
