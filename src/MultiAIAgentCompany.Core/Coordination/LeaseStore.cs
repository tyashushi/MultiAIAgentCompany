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
                return new LeaseWriteResult.Denied("失効した保持者がいます（明示的な奪取許可が必要です）", holder);
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
    public sealed record Denied(string Reason, LeaseHolder Holder) : LeaseWriteResult;
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
