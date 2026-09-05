using System.Text.Json;

namespace MultiAIAgentCompany.Core.Coordination;

/// <summary>
/// <c>.company/tasks</c> にある仕事状態の唯一の保存口。
/// 設計 §14-1 により、<c>state.json</c> を書くのはこの型だけである。
/// </summary>
public sealed class TaskStore
{
    private const string StateFileName = "state.json";
    private static readonly string[] AttemptFiles = ["instruction.md", "report.md", "question.md", "answer.md"];

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

        if (current.Status is TaskStatus.Rejected && to is TaskStatus.Dispatched)
        {
            MoveCurrentAttempt(expected.Slug, current.AttemptId);
        }

        var next = current with
        {
            Status = to,
            AttemptId = current.Status is TaskStatus.Rejected && to is TaskStatus.Dispatched
                ? checked(current.AttemptId + 1)
                : current.AttemptId,
            Revision = checked(current.Revision + 1),
            LastTransitionOrigin = origin,
            UpdatedAt = _clock.GetUtcNow(),
            Note = note,
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
