using System.Text.Json;

namespace MultiAIAgentCompany.Core.Coordination;

/// <param name="Id">スレッドの識別子。ディレクトリ名と一致する。</param>
/// <param name="Title">画面に出す名前。</param>
/// <param name="CreatedAt">作られた時刻。</param>
/// <param name="UpdatedAt">最後に発言が足された時刻。</param>
public sealed record ThreadMeta(string Id, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <param name="Role">"human" か "secretary"。</param>
/// <param name="Text">発言。</param>
/// <param name="At">時刻。</param>
public sealed record ThreadEntry(string Role, string Text, DateTimeOffset At);

/// <param name="Threads">UpdatedAt の新しい順に並んだスレッド。</param>
/// <param name="Unreadable">読めずに除外した数。保存場所自体の走査失敗も1件として数える。</param>
public sealed record ThreadListResult(IReadOnlyList<ThreadMeta> Threads, int Unreadable);

public abstract record ThreadCreateResult
{
    public sealed record Created(ThreadMeta Meta) : ThreadCreateResult;
    public sealed record Failed(string Reason) : ThreadCreateResult;
}

public abstract record ThreadReadResult
{
    public sealed record Found(ThreadMeta Meta, IReadOnlyList<ThreadEntry> Entries, int SkippedLines) : ThreadReadResult;
    public sealed record Missing : ThreadReadResult;
    public sealed record Unreadable(string Reason) : ThreadReadResult;
}

public abstract record ThreadWriteResult
{
    public sealed record Written(ThreadMeta Meta) : ThreadWriteResult;

    /// <summary>そのスレッドが無い。呼び出し元が明示的に作る。</summary>
    public sealed record Missing(string Reason) : ThreadWriteResult;

    public sealed record Failed(string Reason) : ThreadWriteResult;
}

/// <summary>秘書との会話を .company/secretary/threads に保存する口。</summary>
/// <remarks>I/O や JSON の失敗は結果で返す。キャンセルは TaskStore と同様に呼び出し元へ伝える。</remarks>
public sealed class ThreadStore
{
    private readonly CompanyPaths _paths;
    private readonly TimeProvider _clock;
    // 同じ保存口からの追記と改名が、互いの meta.json を古い値で上書きしないようにする。
    private readonly SemaphoreSlim _writes = new(1, 1);

    public ThreadStore(CompanyPaths paths, TimeProvider clock)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<ThreadListResult> ListAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var threads = new List<ThreadMeta>();
        var unreadable = 0;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(_paths.SecretaryThreads))
            {
                ct.ThrowIfCancellationRequested();
                var read = await ReadAsync(Path.GetFileName(directory), ct);
                if (read is ThreadReadResult.Found found)
                {
                    threads.Add(found.Meta);
                }
                else
                {
                    unreadable++;
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // まだ一度も作っていない場合は空。一方、同名ファイルで塞がれていれば読めない。
            if (File.Exists(_paths.SecretaryThreads))
            {
                unreadable++;
            }
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            // 走査できない場所を、空の正常な一覧として見せない。
            unreadable++;
        }

        return new ThreadListResult(threads.OrderByDescending(meta => meta.UpdatedAt)
            .ThenBy(meta => meta.Id, StringComparer.Ordinal).ToArray(), unreadable);
    }

    public async Task<ThreadCreateResult> CreateAsync(string title, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (title is null)
        {
            return new ThreadCreateResult.Failed("タイトルが null です");
        }

        var now = _clock.GetUtcNow();
        // タイトルはパスに使わない。同じ時刻に作っても識別子が衝突しない。
        var id = FormattableString.Invariant($"{now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        var meta = new ThreadMeta(id, title, now, now);
        try
        {
            Directory.CreateDirectory(_paths.ThreadDirectory(id));
            await WriteMetaAtomicallyAsync(meta, overwrite: false, ct);
            return new ThreadCreateResult.Created(meta);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return new ThreadCreateResult.Failed($"スレッドを作れません: {exception.Message}");
        }
    }

    public async Task<ThreadReadResult> ReadAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var read = await ReadMetaAsync(id, ct);
        if (read is not ThreadReadResult.Found found)
        {
            return read;
        }

        var entries = new List<ThreadEntry>();
        var skipped = 0;
        try
        {
            using var reader = new StreamReader(new FileStream(_paths.ThreadTranscript(id), FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite, bufferSize: 4096, useAsync: true));
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<ThreadEntry>(line, TaskStateJson.Options);
                    if (IsValidEntry(entry))
                    {
                        entries.Add(entry!);
                    }
                    else
                    {
                        skipped++;
                    }
                }
                catch (JsonException)
                {
                    skipped++;
                }
            }
        }
        catch (FileNotFoundException)
        {
            // 作成直後は transcript.jsonl をまだ持たない。
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return new ThreadReadResult.Unreadable($"transcript.jsonl を読めません: {exception.Message}");
        }

        return new ThreadReadResult.Found(found.Meta, entries, skipped);
    }

    public async Task<ThreadWriteResult> AppendAsync(string id, ThreadEntry entry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await _writes.WaitAsync(ct);
        try
        {
            var read = await ReadMetaAsync(id, ct);
            if (read is not ThreadReadResult.Found found)
            {
                return WriteFailure(read);
            }

            if (!IsValidEntry(entry))
            {
                return new ThreadWriteResult.Failed("発言の Role は human または secretary、Text は null 以外が必要です");
            }

            // 共有 Options をそのまま使い、JSONL の改行だけ writer 側で抑える。
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
            {
                Indented = false,
                Encoder = TaskStateJson.Options.Encoder,
            }))
            {
                JsonSerializer.Serialize(writer, entry, TaskStateJson.Options);
            }

            var transcript = _paths.ThreadTranscript(id);
            var needsNewLine = NeedsNewLine(transcript);
            await using (var stream = new FileStream(transcript, FileMode.Append, FileAccess.Write,
                FileShare.Read, bufferSize: 4096, useAsync: true))
            {
                // 手編集や中断で末尾の改行がなくても、次の正常な発言を壊れた行と結合しない。
                if (needsNewLine)
                {
                    await stream.WriteAsync("\n"u8.ToArray(), ct);
                }

                await stream.WriteAsync(buffer.ToArray(), ct);
                await stream.WriteAsync("\n"u8.ToArray(), ct);
                await stream.FlushAsync(ct);
            }

            // 必ず追記の完了後に更新する。meta の保存失敗時も追記済みの発言は消さない。
            var next = found.Meta with { UpdatedAt = _clock.GetUtcNow() };
            await WriteMetaAtomicallyAsync(next, overwrite: true, ct);
            return new ThreadWriteResult.Written(next);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return new ThreadWriteResult.Failed($"スレッドへの追記に失敗しました（発言が一部または全部保存済みの場合があります）: {exception.Message}");
        }
        finally
        {
            _writes.Release();
        }
    }

    public async Task<ThreadWriteResult> RenameAsync(string id, string title, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await _writes.WaitAsync(ct);
        try
        {
            var read = await ReadMetaAsync(id, ct);
            if (read is not ThreadReadResult.Found found)
            {
                return WriteFailure(read);
            }

            if (title is null)
            {
                return new ThreadWriteResult.Failed("タイトルが null です");
            }

            // UpdatedAt は最後の発言時刻。改名だけでは一覧の順序を変えない。
            var next = found.Meta with { Title = title };
            await WriteMetaAtomicallyAsync(next, overwrite: true, ct);
            return new ThreadWriteResult.Written(next);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return new ThreadWriteResult.Failed($"スレッド名を変更できません: {exception.Message}");
        }
        finally
        {
            _writes.Release();
        }
    }

    private async Task<ThreadReadResult> ReadMetaAsync(string id, CancellationToken ct)
    {
        if (!CompanyPaths.IsValidSlug(id))
        {
            return new ThreadReadResult.Unreadable("スレッドの id に使えない文字が含まれています");
        }

        try
        {
            await using var stream = new FileStream(_paths.ThreadMeta(id), FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 4096, useAsync: true);
            var meta = await JsonSerializer.DeserializeAsync<ThreadMeta>(stream, TaskStateJson.Options, ct);
            if (meta is null || meta.Title is null || !string.Equals(meta.Id, id, StringComparison.Ordinal))
            {
                return new ThreadReadResult.Unreadable("meta.json が null、タイトルが null、または Id がディレクトリ名と一致しません");
            }

            return new ThreadReadResult.Found(meta, [], 0);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new ThreadReadResult.Missing();
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return new ThreadReadResult.Unreadable($"meta.json を読めません: {exception.Message}");
        }
    }

    private static ThreadWriteResult WriteFailure(ThreadReadResult read) => read switch
    {
        ThreadReadResult.Missing => new ThreadWriteResult.Missing("meta.json が存在しません"),
        ThreadReadResult.Unreadable unreadable => new ThreadWriteResult.Failed(unreadable.Reason),
        _ => throw new InvalidOperationException("読み取り失敗の結果が必要です"),
    };

    private static bool IsValidEntry(ThreadEntry? entry) =>
        entry is { Role: "human" or "secretary", Text: not null };

    private static bool IsStorageFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException;

    private static bool NeedsNewLine(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length == 0)
            {
                return false;
            }

            stream.Seek(-1, SeekOrigin.End);
            return stream.ReadByte() != '\n';
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    private async Task WriteMetaAtomicallyAsync(ThreadMeta meta, bool overwrite, CancellationToken ct)
    {
        var temporaryPath = Path.Combine(_paths.ThreadDirectory(meta.Id), $".meta.json.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, bufferSize: 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, meta, TaskStateJson.Options, ct);
                await stream.FlushAsync(ct);
            }

            File.Move(temporaryPath, _paths.ThreadMeta(meta.Id), overwrite);
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
