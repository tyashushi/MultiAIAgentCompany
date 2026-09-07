using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MultiAIAgentCompany.Core.Coordination;
using MultiAIAgentCompany.Core.Workspace.Trust;

namespace MultiAIAgentCompany.Core.Workspace;

/// <summary>
/// 同じワークスペースを2つのアプリが開くのを防ぐ（設計 §26）。
/// </summary>
/// <remarks>
/// <b>正本は OS の排他ロック</b>。pid や起動時刻は<b>人間に見せる説明</b>であって、
/// 判定の根拠ではない —— 「pid を確かめてから書く」形にすると、同時起動の競合を潰しきれない。
/// <para>
/// <b><c>lease.json</c> に種類を足さない</b>（§14-2）。あちらは「失効しても勝手に奪わない」
/// という人間向けの契約で、アプリの生死は OS のプロセス寿命に寄せる方が自然。
/// クラッシュしても OS がロックを返すので、<b>ここには「外す」操作が要らない</b>。
/// </para>
/// <para>
/// <b><c>.company/</c> に置かない</b>（§16-1）。あそこは AI と共有する調整文書の場所で、
/// アプリ専用の実行時ファイルを混ぜると、部門が読んだり人間が直す対象と誤解される。
/// </para>
/// </remarks>
public sealed class WorkspaceInstanceLock : IDisposable
{
    private readonly FileStream _lock;

    private WorkspaceInstanceLock(FileStream held, string directory)
    {
        _lock = held;
        Directory = directory;
    }

    /// <summary>ロックと説明を置いている場所。人間に見せる。</summary>
    public string Directory { get; }

    public void Dispose() => _lock.Dispose();

    /// <summary>
    /// 取れたら握って返す。取れなければ、いま開いている相手の説明を返す。
    /// </summary>
    /// <remarks>
    /// <b>ロックファイルを消す手段は出さない</b>（§26-2）。Unix では、ロック中のファイルを
    /// 消して作り直すと**別の実体**になり、2つのプロセスが別々のロックを持ててしまう。
    /// </remarks>
    public static WorkspaceInstanceLockResult Acquire(string workspaceRoot, string runtimeRoot, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);

        // **解決後のパスを鍵にする**（§21-2 と同じ理由）。symlink の綴り違いで
        // 「別のワークスペース」に見えると、同じフォルダを2つのアプリが開ける。
        var resolved = WorkspacePathNormalizer.Normalize(workspaceRoot);
        var directory = Path.Combine(
            runtimeRoot, "workspaces", CompanyDigest.OfBytes(Encoding.UTF8.GetBytes(KeyOf(workspaceRoot))));

        try
        {
            System.IO.Directory.CreateDirectory(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // **ここで投げない**（レビューで発覚）。呼び出し元はフォルダ選択の `async void` の
            // 先なので、置けないフォルダを選んだだけで**アプリが落ちる**。
            // 「置けない」は「開いている」とは違う、という §26-2 の扱いに乗せる。
            return new WorkspaceInstanceLockResult.Unavailable(
                $"ロックの置き場所を作れません: {exception.Message}", directory);
        }

        var lockPath = Path.Combine(directory, "instance.lock");
        FileStream held;
        try
        {
            held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            // 取れない＝誰かが握っている。**これが判定**。説明は読めたら添える。
            return new WorkspaceInstanceLockResult.Held(ReadHolder(directory), directory);
        }
        catch (UnauthorizedAccessException exception)
        {
            return new WorkspaceInstanceLockResult.Unavailable($"ロックを置けません: {exception.Message}", directory);
        }

        // 説明はロックとは別のファイルに書く。ロック本体は FileShare.None なので、
        // 握られている間は他のプロセスから**読むこともできない**。
        WriteHolder(directory, new WorkspaceInstanceHolder(
            Environment.ProcessId,
            now,
            Environment.MachineName,
            Environment.UserName,
            resolved));

        return new WorkspaceInstanceLockResult.Acquired(new WorkspaceInstanceLock(held, directory));
    }

    /// <summary>
    /// 同じフォルダを指しているかの判定に使う鍵（設計 §26-1）。
    /// </summary>
    /// <remarks>
    /// <b>ロックの鍵と、画面の「同じフォルダか」の判定を1つにする。</b> 別々にすると、
    /// 綴り違いで「自分のロックを別のアプリのものと言う」「同じフォルダなのに部門を止める」が起きる
    /// （レビューで2度出た）。<c>WorkspacePathNormalizer.Equals</c> は Windows を
    /// 大文字小文字の区別なしで比べるので、鍵もそれに合わせる。
    /// </remarks>
    public static string KeyOf(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        var resolved = WorkspacePathNormalizer.Normalize(workspaceRoot);
        return OperatingSystem.IsWindows() ? resolved.ToLowerInvariant() : resolved;
    }

    private static WorkspaceInstanceHolder? ReadHolder(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "holder.json");
            return File.Exists(path)
                ? JsonSerializer.Deserialize<WorkspaceInstanceHolder>(File.ReadAllText(path), Options)
                : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // 読めなかったことを、いなかったことにしない。呼び出し元は null を
            // 「保持者情報は読めません」と表示する（§26-2）。
            return null;
        }
    }

    private static void WriteHolder(string directory, WorkspaceInstanceHolder holder)
    {
        try
        {
            File.WriteAllText(Path.Combine(directory, "holder.json"), JsonSerializer.Serialize(holder, Options));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 説明が書けなくてもロックは取れている。**開けることを優先する。**
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <param name="Pid">開いているアプリのプロセス ID。<b>判定には使わない</b>（§26-1）。</param>
/// <param name="OpenedAt">開いた時刻。</param>
/// <param name="Host">機械の名前。別のホストからの同時オープンは防げない（§26-3 の懸念）。</param>
/// <param name="User">利用者。</param>
/// <param name="WorkspacePath">解決後のワークスペースのパス。</param>
public sealed record WorkspaceInstanceHolder(
    int Pid,
    DateTimeOffset OpenedAt,
    string Host,
    string User,
    string WorkspacePath);

/// <summary>ロックを取れたか（設計 §26-1）。</summary>
public abstract record WorkspaceInstanceLockResult
{
    /// <summary>取れた。<b>返すまで握り続ける。</b></summary>
    public sealed record Acquired(WorkspaceInstanceLock Lock) : WorkspaceInstanceLockResult;

    /// <summary>誰かが開いている。<paramref name="Holder"/> は読めなければ null。</summary>
    public sealed record Held(WorkspaceInstanceHolder? Holder, string Directory) : WorkspaceInstanceLockResult;

    /// <summary>ロックを置けない（権限など）。<b>開いている、とは違う。</b></summary>
    public sealed record Unavailable(string Reason, string Directory) : WorkspaceInstanceLockResult;
}
