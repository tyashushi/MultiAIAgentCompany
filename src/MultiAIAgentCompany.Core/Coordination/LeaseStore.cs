using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiAIAgentCompany.Core.Coordination;

/// <summary><c>.company/lease.json</c> にあるワークスペース権利表の唯一の保存口。</summary>
/// <remarks>
/// 調整文書（<c>.company/</c> 配下）への書き込みは Write lease の対象外である。
/// 対象にすると、報告書を出せる部門まで一つに制限され、設計 §14-2 のレベル4が成立しない。
/// この型は実際の書き込みを監視せず、権利についての約束を記録するだけである。
/// </remarks>
public sealed class LeaseStore
{
    private const string LeaseFileName = "lease.json";
    private readonly CompanyPaths _paths;
    private readonly TimeProvider _clock;

    public LeaseStore(CompanyPaths paths, TimeProvider clock)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<LeaseReadResult> ReadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(_paths.Lease))
        {
            return new LeaseReadResult.Found(WorkspaceLeases.Empty);
        }

        try
        {
            await using var stream = new FileStream(_paths.Lease, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, useAsync: true);
            var document = await JsonSerializer.DeserializeAsync<LeaseDocument>(stream, WorkspaceLeaseJson.Options, ct);
            if (document is null)
            {
                return new LeaseReadResult.Unreadable("lease.json が空です");
            }

            if (document.Holders is null)
            {
                return new LeaseReadResult.Unreadable("lease.json の holders がありません");
            }

            var holders = new Dictionary<LeaseKind, LeaseHolder>();
            foreach (var holder in document.Holders)
            {
                if (!Enum.IsDefined(holder.Kind))
                {
                    return new LeaseReadResult.Unreadable("lease.json に未定義の列挙値があります");
                }

                if (!holders.TryAdd(holder.Kind, holder))
                {
                    return new LeaseReadResult.Unreadable("lease.json に同じ kind の保持者が複数あります");
                }
            }

            return new LeaseReadResult.Found(new WorkspaceLeases(document.Revision, holders));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new LeaseReadResult.Unreadable($"lease.json を読めません: {exception.Message}");
        }
    }

    public async Task<LeaseWriteResult> AcquireAsync(WorkspaceLeases expected, LeaseKind kind, Actor actor,
        string taskSlug, TimeSpan duration, LeaseTakeover takeover, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ValidateDuration(duration);
        ValidateTaskSlug(taskSlug);
        ct.ThrowIfCancellationRequested();

        var currentResult = await ReadAsync(ct);
        if (currentResult is LeaseReadResult.Unreadable unreadable)
        {
            return new LeaseWriteResult.Conflicted($"lease.json を検証できません: {unreadable.Reason}");
        }

        var current = ((LeaseReadResult.Found)currentResult).Leases;
        if (current.Revision != expected.Revision)
        {
            return RevisionConflict(expected, current);
        }

        var now = _clock.GetUtcNow();
        if (current.Holders.TryGetValue(kind, out var holder))
        {
            if (holder.IsValidAt(now))
            {
                return new LeaseWriteResult.Denied("有効な保持者がいます", holder);
            }

            if (takeover is LeaseTakeover.Deny)
            {
                // **失効と「まだ始まっていない」を分ける**（§24-2）。
                // 未来の取得時刻を失効として返すと、UI が「待っても空かない」と言い、
                // 外そうとしても外せない状態を作る。
                return holder.IsExpiredAt(now)
                    ? new LeaseWriteResult.DeniedExpired("失効した保持者がいます（時間では空きません）", holder)
                    : new LeaseWriteResult.Denied("取得時刻が未来の保持者がいます", holder);
            }

            // **`AllowExpired` は「失効したものを置き換えてよい」。**
            // まだ始まっていない保持者は失効ではないので、ここも通さない（§24-2）。
            if (!holder.IsExpiredAt(now))
            {
                return new LeaseWriteResult.Denied("取得時刻が未来の保持者がいます", holder);
            }
        }

        var nextHolders = new Dictionary<LeaseKind, LeaseHolder>(current.Holders)
        {
            [kind] = new LeaseHolder(kind, actor, taskSlug, now, now + duration),
        };
        return await WriteAsync(new WorkspaceLeases(checked(current.Revision + 1), nextHolders), ct);
    }

    public async Task<LeaseWriteResult> RenewAsync(WorkspaceLeases expected, LeaseKind kind, Actor actor,
        TimeSpan duration, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ValidateDuration(duration);
        ct.ThrowIfCancellationRequested();

        var currentResult = await ReadAsync(ct);
        if (currentResult is LeaseReadResult.Unreadable unreadable)
        {
            return new LeaseWriteResult.Conflicted($"lease.json を検証できません: {unreadable.Reason}");
        }

        var current = ((LeaseReadResult.Found)currentResult).Leases;
        if (current.Revision != expected.Revision)
        {
            return RevisionConflict(expected, current);
        }

        var now = _clock.GetUtcNow();
        if (!current.Holders.TryGetValue(kind, out var holder)
            || !holder.IsValidAt(now)
            || holder.Holder != actor)
        {
            return new LeaseWriteResult.NotHeld("有効な権利を保持していません");
        }

        var nextHolders = new Dictionary<LeaseKind, LeaseHolder>(current.Holders)
        {
            [kind] = holder with { ExpiresAt = now + duration },
        };
        return await WriteAsync(new WorkspaceLeases(checked(current.Revision + 1), nextHolders), ct);
    }

    /// <summary>
    /// 失効した保持者を外す（設計 §24-2）。<b>人間が押したときだけ呼ぶ。</b>
    /// </summary>
    /// <remarks>
    /// <b>§14-2 の「時間切れだけを根拠に奪わない」を破っていない。</b> 根拠は時間ではなく、
    /// <b>人間が「その保持者はもう動いていない」と判断したこと</b>。アプリが言えるのは
    /// 「このアプリが管理しているセッションに該当する保持者はいない」までで、
    /// 別インスタンス・手動起動の CLI・クラッシュ後の残存までは分からない（§9）。
    /// <para>
    /// <b>有効な保持者は外さない。</b> それは奪取であって、この操作ではない。
    /// </para>
    /// </remarks>
    public async Task<LeaseWriteResult> ReleaseExpiredAsync(
        WorkspaceLeases expected, LeaseKind kind, LeaseHolder judged, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(judged);
        ct.ThrowIfCancellationRequested();

        var currentResult = await ReadAsync(ct);
        if (currentResult is LeaseReadResult.Unreadable unreadable)
        {
            return new LeaseWriteResult.Conflicted($"lease.json を検証できません: {unreadable.Reason}");
        }

        var current = ((LeaseReadResult.Found)currentResult).Leases;
        if (current.Revision != expected.Revision)
        {
            return RevisionConflict(expected, current);
        }

        var now = _clock.GetUtcNow();
        if (!current.Holders.TryGetValue(kind, out var holder))
        {
            return new LeaseWriteResult.NotHeld("その権利には保持者がいません");
        }

        // **「有効でない」と「失効した」は違う**（レビューで発覚）。
        // `IsValidAt` は取得時刻が未来のもの（時計のずれ・手編集）も false にするが、
        // それは**まだ始まっていない** lease であって、外してよいものではない。
        if (!holder.IsExpiredAt(now))
        {
            return new LeaseWriteResult.Denied(
                holder.IsValidAt(now) ? "まだ有効な保持者です（外しません）" : "まだ失効していません（外しません）",
                holder);
        }

        // **人間が見て判断したのは `judged`。** 画面に出してから押すまでの間に
        // lease.json が変わっていたら、**別の保持者を外すことになる**（レビューで発覚）。
        // 人間の明示的な判断が根拠なので、判断の対象が変わったら実行しない。
        if (holder != judged)
        {
            return new LeaseWriteResult.Conflicted(
                $"表示していた保持者と違います（表示: {judged.Holder.Id} / 現在: {holder.Holder.Id}）");
        }

        var remaining = new Dictionary<LeaseKind, LeaseHolder>(current.Holders);
        remaining.Remove(kind);
        return await WriteAsync(new WorkspaceLeases(checked(current.Revision + 1), remaining), ct);
    }

    public async Task<LeaseWriteResult> ReleaseAsync(WorkspaceLeases expected, LeaseKind kind, Actor actor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ct.ThrowIfCancellationRequested();

        var currentResult = await ReadAsync(ct);
        if (currentResult is LeaseReadResult.Unreadable unreadable)
        {
            return new LeaseWriteResult.Conflicted($"lease.json を検証できません: {unreadable.Reason}");
        }

        var current = ((LeaseReadResult.Found)currentResult).Leases;
        if (current.Revision != expected.Revision)
        {
            return RevisionConflict(expected, current);
        }

        var now = _clock.GetUtcNow();
        if (!current.Holders.TryGetValue(kind, out var holder)
            || !holder.IsValidAt(now)
            || holder.Holder != actor)
        {
            return new LeaseWriteResult.NotHeld("有効な権利を保持していません");
        }

        var nextHolders = new Dictionary<LeaseKind, LeaseHolder>(current.Holders);
        nextHolders.Remove(kind);
        return await WriteAsync(new WorkspaceLeases(checked(current.Revision + 1), nextHolders), ct);
    }

    /// <summary>
    /// 誰も持っていない lease を書く（設計 §23-1）。
    /// </summary>
    /// <remarks>
    /// <b>隔離のあと（<see cref="LeaseRecovery"/>）だけで使う。</b> 通常の経路から呼ばない ——
    /// 読めない lease を黙って作り直すのは §14-2 に反する。
    /// </remarks>
    public Task<LeaseWriteResult> WriteEmptyAsync(CancellationToken ct) =>
        WriteAsync(WorkspaceLeases.Empty, ct);

    private async Task<LeaseWriteResult> WriteAsync(WorkspaceLeases leases, CancellationToken ct)
    {
        Directory.CreateDirectory(_paths.Root);
        await WriteLeasesAtomicallyAsync(_paths.Lease, leases, ct);
        return new LeaseWriteResult.Written(leases);
    }

    private static LeaseWriteResult.Conflicted RevisionConflict(WorkspaceLeases expected, WorkspaceLeases current) =>
        new($"Revision が一致しません（expected: {expected.Revision}, actual: {current.Revision}）");

    private static void ValidateDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "duration は正でなければなりません");
        }
    }

    private static void ValidateTaskSlug(string taskSlug)
    {
        if (!CompanyPaths.IsValidSlug(taskSlug))
        {
            throw new ArgumentException("taskSlug は有効な slug でなければなりません", nameof(taskSlug));
        }
    }

    private static async Task WriteLeasesAtomicallyAsync(string leasePath, WorkspaceLeases leases, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(leasePath)!;
        var temporaryPath = Path.Combine(directory, $".{LeaseFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            var document = new LeaseDocument(leases.Revision, leases.Holders.Values.ToArray());
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, document, WorkspaceLeaseJson.Options, ct);
                await stream.FlushAsync(ct);
            }

            File.Move(temporaryPath, leasePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record LeaseDocument(long Revision, LeaseHolder[] Holders);
}

public abstract record LeaseReadResult
{
    public sealed record Found(WorkspaceLeases Leases) : LeaseReadResult;
    public sealed record Unreadable(string Reason) : LeaseReadResult;
}

public abstract record LeaseWriteResult
{
    public sealed record Written(WorkspaceLeases Leases) : LeaseWriteResult;
    /// <summary>有効な保持者がいる。<b>待てば空く可能性がある。</b></summary>
    public sealed record Denied(string Reason, LeaseHolder Holder) : LeaseWriteResult;

    /// <summary>
    /// 失効した保持者がいる。<b>待っても空かない</b>（設計 §24-1）。
    /// </summary>
    /// <remarks>
    /// <b><see cref="Denied"/> と型で分ける。</b> §14-2 が時間切れだけでの奪取を禁じているので、
    /// 人間が外すと決めない限り永久に空かない —— 同じ型に潰すと、UI が両方に
    /// 「待つ」と言う（実機で出た。2026-09-06）。
    /// </remarks>
    public sealed record DeniedExpired(string Reason, LeaseHolder Holder) : LeaseWriteResult;
    public sealed record NotHeld(string Reason) : LeaseWriteResult;
    public sealed record Conflicted(string Reason) : LeaseWriteResult;
}

/// <summary><c>lease.json</c> の JSON 設定。列挙は名前で書く。</summary>
public static class WorkspaceLeaseJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        RespectRequiredConstructorParameters = true,

        // 日本語をエスケープしない。既定では "実装" が "\u5B9F\u88C5" になる。
        // 部門名も note も日本語なので、それでは人間が読んで直せるファイルではなくなる ——
        // §14-1 / §14-2 はどちらも「人間が手で直せる」ことに寄りかかっている。
        // このファイルは HTML に埋め込まない（ローカルの調整文書）ので、
        // HTML 向けのエスケープは要らない。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
