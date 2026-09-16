using System.Text.Json;

namespace MultiAIAgentCompany.Core.Activity;

/// <summary>Stream completed の形を確認できた版だけを覚える（設計 §61-3）。</summary>
public sealed class AgyLogVersions(string dataRoot)
{
    private static readonly object Gate = new();
    private readonly string _path = Path.Combine(dataRoot, "agy-log-versions.json");

    public bool Contains(string? version)
    {
        lock (Gate) return !string.IsNullOrWhiteSpace(version) && Read().Contains(version);
    }

    public void Validate(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return;
        lock (Gate)
        {
            var versions = Read();
            if (!versions.Add(version)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(versions.Order(StringComparer.Ordinal)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 保存できなければ次回も未検証として扱う（設計 §61-3）。
            }
        }
    }

    private HashSet<string> Read()
    {
        try { return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(_path)) ?? []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }
}

/// <summary>
/// agy の版（設計 §61-3）。ログの形の検証を版ごとに覚えるために読む。
/// <b>読めなければ null</b> —— 版を推測で埋めない（未検証のまま「作業中か承認待ち」を出す）。
/// </summary>
public static partial class AgyVersion
{
    private static readonly Dictionary<string, string?> Cache = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*(\d+(?:\.\d+)+)\s*$", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex VersionLine();

    /// <summary><c>agy --version</c> の出力（実測: <c>1.2.4</c> の1行）から版を取る。</summary>
    public static string? Parse(string? output) =>
        output is null ? null : VersionLine().Match(output) is { Success: true } match ? match.Groups[1].Value : null;

    /// <summary>同じ実行ファイルは1回だけ聞く。5 秒で諦める。</summary>
    public static async Task<string?> DetectAsync(string executable, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Cache.TryGetValue(executable, out var cached)) return cached;
            string? version = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var info = new System.Diagnostics.ProcessStartInfo(executable)
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                };
                info.ArgumentList.Add("--version");
                using var process = System.Diagnostics.Process.Start(info);
                if (process is not null)
                {
                    var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                    try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                        return null;
                    }
                    if (process.ExitCode == 0) version = Parse(await stdout.ConfigureAwait(false));
                }
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                // 聞けなければ未検証として扱う（§61-3）。
            }
            Cache[executable] = version;
            return version;
        }
        finally { Gate.Release(); }
    }
}
