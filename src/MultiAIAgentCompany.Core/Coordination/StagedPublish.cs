namespace MultiAIAgentCompany.Core.Coordination;

/// <summary>
/// 部門が一時ファイルのまま残した報告・質問を、アプリが最終名へ引き取る（設計 §62-16）。
/// </summary>
/// <remarks>
/// <b>tmp → rename は publish 契約（§16-1）のままにする。</b> ただし Antigravity の「編集を受け入れる」
/// モードでは、書き込みは通るのに <c>mv</c> はシェルの承認が要り、<b>報告のたびに承認で止まった</b>
/// （実機で発覚）。承認されたあとも名前を変えずに写し、一時ファイルが残った。
/// <para>
/// <b>しばらく書き換わっていない一時ファイルは、書き終わったものとして扱う。</b>
/// 部門には「一時ファイルは1回で書き上げる。書き足すなら新しい一時ファイルに全部書き直す」と頼んである。
/// 置くのは同じフォルダの中の rename なので、最終名に書きかけが見えることは無い。
/// </para>
/// </remarks>
public static class StagedPublish
{
    /// <summary>この長さ書き換わらなければ、書き終わったとみなす。</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 最終名が無く、書き終わった一時ファイルがあれば、いちばん新しいものを最終名にする。引き取ったら true。
    /// </summary>
    public static bool TryPromote(string finalPath, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        if (File.Exists(finalPath))
        {
            return false;
        }

        try
        {
            var newest = Staged(finalPath)
                .Select(path => new FileInfo(path))
                .Where(file => file.Length > 0)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();

            // **一番新しいものが落ち着くまで待つ。** 古い方を先に置くと、書き直している最中の古い版を読む。
            if (newest is null || now.UtcDateTime - newest.LastWriteTimeUtc < SettleTime)
            {
                return false;
            }

            File.Move(newest.FullName, finalPath, overwrite: false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 部門が同時に rename した、など。次の走査でまた見る。
            return false;
        }
    }

    /// <summary>
    /// 最終名と<b>同じ中身</b>の一時ファイルを片付ける（写してから消し忘れたもの）。違う中身は残す。
    /// </summary>
    public static void RemoveCopies(string finalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        try
        {
            // **一時ファイルがあるときだけ読む**（走査は数秒おきに全部の仕事を回る）。
            var staged = Staged(finalPath).ToArray();
            if (staged.Length is 0 || !File.Exists(finalPath))
            {
                return;
            }

            var published = File.ReadAllBytes(finalPath);
            foreach (var path in staged)
            {
                if (File.ReadAllBytes(path).AsSpan().SequenceEqual(published))
                {
                    File.Delete(path);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static IEnumerable<string> Staged(string finalPath)
    {
        var directory = Path.GetDirectoryName(finalPath)!;
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, $"{Path.GetFileName(finalPath)}.tmp*")
            : [];
    }
}
