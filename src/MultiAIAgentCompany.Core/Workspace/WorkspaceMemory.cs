using System.Text.Json;
using System.Text.Json.Serialization;
using MultiAIAgentCompany.Core.Workspace.Trust;

namespace MultiAIAgentCompany.Core.Workspace;

/// <summary>
/// 前回選んだワークスペースを覚える（設計 §21）。
/// </summary>
/// <remarks>
/// <b>覚えるのはパスだけ。</b> trust の結果も、部門が動いていたかも覚えない（§7）——
/// どちらも「観測していない状態を推定しない」に反する。trust は毎回読み直し、
/// 仕事状態は起動時の走査で読む。
/// <para>
/// <b>ワークスペースの中（<c>.company/</c>）には置かない。</b> これはアプリの設定であって、
/// AI たちと共有する調整文書ではない。
/// </para>
/// </remarks>
public sealed class WorkspaceMemory
{
    private const int SchemaVersion = 1;

    private readonly string _path;

    public WorkspaceMemory(string settingsPath) =>
        _path = settingsPath ?? throw new ArgumentNullException(nameof(settingsPath));

    /// <summary>macOS の置き場所（<c>~/Library/Application Support</c>）。</summary>
    public static WorkspaceMemory CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "Application Support", "MultiAIAgentCompany", "workspace.json"));

    /// <summary>人間が選んだので覚える。<b>生のパスと解決後のパスを両方持つ</b>（§21-2）。</summary>
    public async Task RememberAsync(string rawPath, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawPath);

        var remembered = new RememberedWorkspace(
            SchemaVersion, rawPath, WorkspacePathNormalizer.Normalize(rawPath), now);

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, remembered, Options, ct);
            }

            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>
    /// 起動時に、前回のワークスペースをそのまま開いてよいか決める（設計 §21-1）。
    /// </summary>
    /// <remarks>
    /// <b>疑わしいときは開かない。</b> 開くと Startup 走査が <c>state.json</c> を進めるので
    /// （§14-1 / §16-1）、人間が選んでいない場所でそれを起こさない。
    /// </remarks>
    public async Task<WorkspaceResume> DecideAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
        {
            return new WorkspaceResume.Unset();
        }

        RememberedWorkspace? remembered;
        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);
            remembered = await JsonSerializer.DeserializeAsync<RememberedWorkspace>(stream, Options, ct);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // 読めなかったことを「無かった」にしない。人間に理由を見せる（§13-9 と同じ姿勢）。
            return new WorkspaceResume.Ask(null, $"前回のワークスペースの記録を読めなかった: {exception.Message}");
        }

        if (remembered is null || remembered.SchemaVersion != SchemaVersion)
        {
            return new WorkspaceResume.Ask(null, "前回のワークスペースの記録が古い形式だった");
        }

        if (File.Exists(remembered.RawPath))
        {
            return new WorkspaceResume.Ask(remembered, $"{remembered.RawPath} がファイルになっている");
        }

        if (!Directory.Exists(remembered.RawPath))
        {
            return new WorkspaceResume.Ask(remembered, $"{remembered.RawPath} が無い");
        }

        // **symlink の張り替えを黙って追わない**（§21-2）。同じ綴りで別の場所を指すのは、
        // 人間から見て「同じフォルダを開いた」に見えるのに中身が別物になる典型。
        var resolved = WorkspacePathNormalizer.Normalize(remembered.RawPath);
        if (!string.Equals(resolved, remembered.ResolvedPath, StringComparison.Ordinal))
        {
            return new WorkspaceResume.Ask(remembered,
                $"{remembered.RawPath} の実体が変わっている（前回: {remembered.ResolvedPath} / 今回: {resolved}）");
        }

        // **`.company/` を黙って作らない**（§21-1）。無いなら、ここは前に使っていた
        // ワークスペースではないかもしれない。作るのは人間が選んだときだけ。
        if (!Directory.Exists(new WorkspaceRef(remembered.RawPath).Company.Root))
        {
            return new WorkspaceResume.Ask(remembered, $"{remembered.RawPath} に .company/ が無い");
        }

        return new WorkspaceResume.Open(remembered);
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <param name="RawPath">人間が選んだ綴りそのまま。<b>これが人間の指定</b>（§21-2）。</param>
/// <param name="ResolvedPath">そのとき解決した実体。次の起動で張り替えを見つけるために持つ。</param>
public sealed record RememberedWorkspace(
    int SchemaVersion,
    string RawPath,
    string ResolvedPath,
    DateTimeOffset SelectedAt);

/// <summary>起動時に前回のワークスペースをどうするか（設計 §21-1）。</summary>
public abstract record WorkspaceResume
{
    /// <summary>そのまま開いてよい。</summary>
    public sealed record Open(RememberedWorkspace Remembered) : WorkspaceResume;

    /// <summary>
    /// 人間に確かめる。<b>理由を必ず出す</b> ——
    /// 黙って「選んでください」に戻ると、前回の場所が消えたことに気付けない。
    /// </summary>
    /// <remarks>
    /// <b><see cref="Unset"/> と分ける。</b> 記録が読めないのと、一度も選んでいないのは違う。
    /// 同じにすると、壊れた記録が「まだ選んでいない」として黙って捨てられる（§13-9 と同じ姿勢）。
    /// </remarks>
    public sealed record Ask(RememberedWorkspace? Remembered, string Reason) : WorkspaceResume;

    /// <summary>まだ一度も選んでいない。<b>ここだけは何も言わない</b>（初回起動なので）。</summary>
    public sealed record Unset : WorkspaceResume;
}
