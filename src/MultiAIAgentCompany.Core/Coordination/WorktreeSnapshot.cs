using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace MultiAIAgentCompany.Core.Coordination;

/// <summary>
/// 作業ツリーの控え（設計 §62-6）。読むだけの部門が書き換えていないかを、<b>事後に</b>確かめる。
/// </summary>
/// <remarks>
/// <b>防ぐのではなく、気付く。</b> <c>ReadsOnly</c> は宣言であって強制ではない（§29-1）ので、
/// 送る前と報告のあとで <c>git status</c> と中身の sha256 を比べる。
/// <para>
/// <b>見えないもの</b>: <c>.gitignore</c> で無視されたファイルの変化と、<c>.company/</c> の下
/// （報告・質問・デザイナーの画像はそこに書くのが約束なので、比べない）。
/// </para>
/// </remarks>
/// <param name="Available">控えを取れたか。git でない・git が失敗したら false。</param>
/// <param name="Reason">取れなかった理由。</param>
/// <param name="Entries">パス（ワークスペースからの相対、区切りは <c>/</c>）→ 状態と中身の sha256。</param>
public sealed record WorktreeSnapshot(bool Available, string? Reason, IReadOnlyDictionary<string, string> Entries)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>送る前の控えの置き場所。<b>試行ごと</b>（差し戻しの送り直しは別の控え）。</summary>
    public static string BeforePath(CompanyPaths paths, string slug, int attemptId) =>
        Path.Combine(paths.TaskDirectory(slug), $"worktree-before.{attemptId}.json");

    /// <summary>報告のあとに比べた結果の置き場所。<b>1回の報告につき1回だけ</b>比べるため。</summary>
    public static string CheckPath(CompanyPaths paths, string slug, int attemptId) =>
        Path.Combine(paths.TaskDirectory(slug), $"worktree-check.{attemptId}.json");

    /// <summary>いまの作業ツリーの控えを取る。<b>例外にしない</b>（取れなければ <see cref="Available"/> = false）。</summary>
    public static async Task<WorktreeSnapshot> TakeAsync(CompanyPaths paths, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);

        // **リポジトリの根ではなく、ワークスペースからの接頭辞を聞く**（macOS の一時フォルダのように
        // symlink を通った場所では、git が返す根の実パスとワークスペースのパスが食い違う）。
        var (prefixCode, prefix) = await RunGitAsync(paths.WorkspaceRoot, ["rev-parse", "--show-prefix"], ct).ConfigureAwait(false);
        if (prefixCode != 0)
        {
            return Unavailable("git の作業ツリーではない（または git を起動できない）");
        }

        // **改行だけを削る**（レビューで発覚）。Trim() だと ` app/` のような先頭の空白まで消え、接頭辞を外せなくなる。
        prefix = prefix.TrimEnd('\r', '\n');

        // **ワークスペースの下だけを見る**（pathspec `.`）。リポジトリの別の場所の変化は、この部門と関係が薄い。
        var (code, output) = await RunGitAsync(
            paths.WorkspaceRoot,
            ["-c", "core.quotepath=false", "status", "--porcelain=v1", "-z", "--untracked-files=all", "--", "."],
            ct).ConfigureAwait(false);
        if (code != 0)
        {
            return Unavailable($"git status が失敗した（終了コード {code}）");
        }

        var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var tokens = output.Split('\0');
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (token.Length < 4)
            {
                continue;
            }

            var status = token[..2];
            var names = new List<string> { token[3..] };

            // 名前の変更・複製は、次のトークンが元の名前（-z の形式）。
            if (status.Contains('R') || status.Contains('C'))
            {
                if (index + 1 < tokens.Length)
                {
                    names.Add(tokens[++index]);
                }
            }

            foreach (var fromRepository in names)
            {
                // porcelain のパスはリポジトリの根から。ワークスペースの外（名前の変更の元など）はそのまま残す。
                var key = fromRepository.StartsWith(prefix, StringComparison.Ordinal)
                    ? fromRepository[prefix.Length..]
                    : fromRepository;
                if (key == ".company" || key.StartsWith(".company/", StringComparison.Ordinal))
                {
                    continue;
                }

                var full = Path.Combine(paths.WorkspaceRoot, key.Replace('/', Path.DirectorySeparatorChar));
                entries[key] = $"{status}|{await HashAsync(full, ct).ConfigureAwait(false)}";
            }
        }

        return new WorktreeSnapshot(true, null, entries);
    }

    /// <summary>2つの控えの違い。増えた・消えた・中身か状態が変わったパス。</summary>
    public static IReadOnlyList<string> Changes(WorktreeSnapshot before, WorktreeSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        return before.Entries.Keys.Union(after.Entries.Keys)
            .Where(path => !before.Entries.TryGetValue(path, out var was)
                || !after.Entries.TryGetValue(path, out var now)
                || !string.Equals(was, now, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// 送る前の控えを置く。<b>同じ試行の控えが既にあれば置かない</b>
    /// （窓を開き直しただけのときに取り直すと、その間の書き換えが見えなくなる）。
    /// </summary>
    public static async Task SaveBeforeAsync(CompanyPaths paths, string slug, int attemptId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var path = BeforePath(paths, slug, attemptId);
        if (File.Exists(path))
        {
            return;
        }

        await WriteAsync(path, await TakeAsync(paths, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 報告のあとに比べる。<b>1回の報告につき1回だけ</b> git を走らせ、結果を置いておく。
    /// </summary>
    public static async Task<WorktreeCheck> CheckAsync(CompanyPaths paths, string slug, int attemptId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var checkPath = CheckPath(paths, slug, attemptId);
        if (await ReadJsonAsync<CheckRecord>(checkPath, ct).ConfigureAwait(false) is { } done)
        {
            return Judge(done.Before, done.After, alreadyReported: true);
        }

        if (await ReadJsonAsync<WorktreeSnapshot>(BeforePath(paths, slug, attemptId), ct).ConfigureAwait(false) is not { } before)
        {
            return new WorktreeCheck.NoBaseline();
        }

        var after = before.Available
            ? await TakeAsync(paths, ct).ConfigureAwait(false)
            : Unavailable("送る前の控えが取れていない");

        var record = new CheckRecord(before, after);
        await WriteAsync(checkPath, record, ct).ConfigureAwait(false);
        return Judge(before, after, alreadyReported: false);
    }

    private static WorktreeCheck Judge(WorktreeSnapshot before, WorktreeSnapshot after, bool alreadyReported)
    {
        if (!before.Available || !after.Available)
        {
            return new WorktreeCheck.Unavailable(before.Reason ?? after.Reason ?? "控えを取れなかった", alreadyReported);
        }

        var changes = Changes(before, after);
        return changes.Count is 0
            ? new WorktreeCheck.Unchanged()
            : new WorktreeCheck.Changed(changes, alreadyReported);
    }

    /// <summary>画面と作業ログに出す、変わったファイルの一覧（最大10件と件数）。</summary>
    public static string Describe(IReadOnlyList<string> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var shown = string.Join("、", changes.Take(10));
        return changes.Count > 10 ? $"{shown} ほか（全 {changes.Count} 件）" : $"{shown}（{changes.Count} 件）";
    }

    private static WorktreeSnapshot Unavailable(string reason) =>
        new(false, reason, new Dictionary<string, string>());

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        try
        {
            if (Directory.Exists(path))
            {
                return "directory";
            }

            if (!File.Exists(path))
            {
                return "missing";
            }

            await using var stream = File.OpenRead(path);
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // **読めないことを「同じ」にしない。** 理由の名前を値にして、次に読めたら違いとして出る。
            return $"unreadable:{exception.GetType().Name}";
        }
    }

    private sealed record CheckRecord(WorktreeSnapshot Before, WorktreeSnapshot After);

    private static async Task WriteAsync<T>(string path, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(value, JsonOptions), ct).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken ct) where T : class
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false))
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static async Task<(int ExitCode, string Output)> RunGitAsync(
        string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        try
        {
            var info = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Sessions.ProcessEncoding.Utf8,
                StandardErrorEncoding = Sessions.ProcessEncoding.Utf8,
                UseShellExecute = false,
            };

            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info);
            if (process is null)
            {
                return (-1, string.Empty);
            }

            var output = process.StandardOutput.ReadToEndAsync(ct);
            _ = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // **git が無い環境でも止めない。** 控えが取れないだけ。
            return (-1, string.Empty);
        }
    }
}

/// <summary><see cref="WorktreeSnapshot.CheckAsync"/> の結果。</summary>
public abstract record WorktreeCheck
{
    /// <summary>送る前の控えが無い（読むだけの部門ではない、またはこの機能より前に送った）。</summary>
    public sealed record NoBaseline : WorktreeCheck;

    /// <summary>変わっていない。</summary>
    public sealed record Unchanged : WorktreeCheck;

    /// <summary>控えが取れず、比べられなかった。</summary>
    /// <param name="AlreadyReported">前に一度比べて、同じ結果を出してある。</param>
    public sealed record Unavailable(string Reason, bool AlreadyReported) : WorktreeCheck;

    /// <summary>変わった。</summary>
    /// <param name="AlreadyReported">前に一度比べて、同じ結果を出してある。</param>
    public sealed record Changed(IReadOnlyList<string> Paths, bool AlreadyReported) : WorktreeCheck;
}
