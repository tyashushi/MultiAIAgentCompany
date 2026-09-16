using System.Text;

namespace MultiAIAgentCompany.Core.Coordination;

/// <summary>送る前の添付1つ。<b>まだ複製していない</b>（設計 §58-2）。</summary>
public sealed record AttachmentDraft(string SourcePath, string Name, long Length);

/// <summary>落とされたものを受け付けるかの判定（設計 §58-3）。</summary>
/// <param name="Accepted">受け付けたもの。</param>
/// <param name="Rejections">弾いたものと理由。<b>黙って落とさない。</b></param>
public sealed record AttachmentAdmission(IReadOnlyList<AttachmentDraft> Accepted, IReadOnlyList<string> Rejections);

public abstract record AttachmentCopyResult
{
    /// <param name="Links">ワークスペースからの相対パス。区切りは <c>/</c>。</param>
    public sealed record Copied(IReadOnlyList<string> Links) : AttachmentCopyResult;
    public sealed record Failed(string Reason) : AttachmentCopyResult;
}

/// <summary>
/// 秘書への添付（設計 §58）。<b>作業フォルダの <c>.company/attachments/</c> へ複製してパスを渡す</b> ——
/// CLI ごとの画像入力には乗せない（§58-1）。
/// </summary>
public static class Attachments
{
    /// <summary>
    /// Claude Code アプリに合わせる（§58-3）。公式の数字が無く、根拠は v2.1.31 の「リクエスト 20MB」。
    /// </summary>
    public const long MaxFileBytes = 20L * 1024 * 1024;

    public const long MaxTotalBytes = 20L * 1024 * 1024;

    /// <summary>claude.ai の1会話の上限に寄せた（§58-3）。</summary>
    public const int MaxCount = 20;

    public const string Heading = "添付:";

    /// <summary>
    /// 落とされたパスを、今ある添付に足してよいか決める。<b>越えたものだけ弾き、残りは受け付ける</b>（§58-3）。
    /// </summary>
    public static AttachmentAdmission Admit(IReadOnlyList<AttachmentDraft> current, IEnumerable<string> dropped)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(dropped);

        var accepted = new List<AttachmentDraft>();
        var rejections = new List<string>();
        var count = current.Count;
        var total = current.Sum(draft => draft.Length);

        foreach (var path in dropped)
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            if (Directory.Exists(path)) { rejections.Add($"{name}: フォルダは添付できない"); continue; }

            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists) { rejections.Add($"{name}: ファイルが見つからない"); continue; }
                _ = info.Length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or ArgumentException or NotSupportedException)
            {
                rejections.Add($"{name}: 読めない（{exception.GetType().Name}）");
                continue;
            }

            // **同じファイルを2回落としても1つ**（送る文に同じパスが並ぶだけになる）。
            if (current.Concat(accepted).Any(draft => string.Equals(draft.SourcePath, info.FullName, StringComparison.Ordinal)))
            {
                continue;
            }
            if (count >= MaxCount) { rejections.Add($"{name}: 一度に添付できるのは {MaxCount} 個まで"); continue; }
            if (info.Length > MaxFileBytes) { rejections.Add($"{name}: 1ファイル {Megabytes(MaxFileBytes)} まで（{Megabytes(info.Length)}）"); continue; }
            if (total + info.Length > MaxTotalBytes) { rejections.Add($"{name}: 合計 {Megabytes(MaxTotalBytes)} を越える"); continue; }

            accepted.Add(new AttachmentDraft(info.FullName, info.Name, info.Length));
            count++;
            total += info.Length;
        }

        return new AttachmentAdmission(accepted, rejections);
    }

    /// <summary>
    /// 送るときに <c>.company/attachments/&lt;時刻&gt;-&lt;4桁&gt;/</c> へ複製する（§58-2）。
    /// <b>1つでも失敗したら、途中まで作ったフォルダを消して失敗を返す。</b>
    /// </summary>
    public static async Task<AttachmentCopyResult> CopyAsync(
        CompanyPaths paths, IReadOnlyList<AttachmentDraft> drafts, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(drafts);
        if (drafts.Count == 0) return new AttachmentCopyResult.Copied([]);

        // **送るときにも見直す**（§58-3）—— 落としてから送るまでに変わり得る。
        long total = 0;
        foreach (var draft in drafts)
        {
            long length;
            try { length = new FileInfo(draft.SourcePath).Length; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new AttachmentCopyResult.Failed($"{draft.Name}: 読めない（{exception.GetType().Name}）");
            }
            if (length > MaxFileBytes) return new AttachmentCopyResult.Failed($"{draft.Name}: 1ファイル {Megabytes(MaxFileBytes)} を越えた");
            total += length;
        }
        if (total > MaxTotalBytes) return new AttachmentCopyResult.Failed($"添付の合計が {Megabytes(MaxTotalBytes)} を越えた");

        var folder = $"{now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
        var directory = Path.Combine(paths.AttachmentsRoot, folder);
        try
        {
            Directory.CreateDirectory(directory);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var links = new List<string>();
            foreach (var draft in drafts)
            {
                var name = UniqueName(SafeName(draft.Name), used);
                await using (var source = new FileStream(draft.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
                await using (var target = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await source.CopyToAsync(target, ct).ConfigureAwait(false);
                }
                links.Add($".company/attachments/{folder}/{name}");
            }
            return new AttachmentCopyResult.Copied(links);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return new AttachmentCopyResult.Failed($"添付を複製できない（{exception.GetType().Name}: {exception.Message}）");
        }
    }

    /// <summary>
    /// 会話に出す文・残す文・秘書へ送る文は<b>同じ</b>（§58-4）。本文が空なら <c>添付:</c> の塊だけ。
    /// </summary>
    public static string Compose(string? body, IReadOnlyList<string> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        var text = body?.TrimEnd() ?? "";
        if (links.Count == 0) return text;

        var builder = new StringBuilder();
        if (text.Length > 0) builder.Append(text).Append("\n\n");
        builder.Append(Heading);
        foreach (var link in links) builder.Append("\n- ").Append(link);
        return builder.ToString();
    }

    /// <summary>
    /// <b>空白は <c>_</c></b> —— リンク検出の区切りが空白なので（§56-3 / §58-2）。
    /// どの OS でもパスに使えない文字も <c>_</c> にする。
    /// </summary>
    public static string SafeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(char.IsWhiteSpace(c) || char.IsControl(c) || InvalidNameChars.Contains(c) ? '_' : c);
        }
        // 末尾の `.` は Windows で落ちる。先頭の `.`（.gitignore 等）は残す。
        var safe = builder.ToString().TrimEnd('.');
        return safe.Length == 0 ? "attachment" : safe;
    }

    private static readonly HashSet<char> InvalidNameChars = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    private static string UniqueName(string name, HashSet<string> used)
    {
        if (used.Add(name)) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem}-{i}{extension}";
            if (used.Add(candidate)) return candidate;
        }
    }

    public static string Megabytes(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.#}MB" : $"{Math.Max(1, (bytes + 1023) / 1024)}KB";
}
