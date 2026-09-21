namespace MultiAIAgentCompany.Core.Terminal;

/// <summary>
/// 部門を外部ターミナルで起動する要求（設計 §32）。
/// </summary>
/// <param name="Title">
/// 窓の見出し。<b>人間が窓を見分けるためのもので、照合には使わない</b>（§32-2c）——
/// 前面化のハンドルは <see cref="TerminalHandle.WindowId"/> である。
/// </param>
/// <param name="WorkingDirectory">その部門の作業フォルダ。</param>
/// <param name="Command">実行ファイル。<b>解決済みのフルパスを渡す</b>（§28-1）。</param>
/// <param name="Arguments">
/// 引数。<b>指示書の本文を入れないこと</b>（設計 §32-2f）——
/// argv は <c>ps</c> に出るので、指示に秘密が入り得る以上そこへ流さない。
/// 渡すのは<b>指示書の在り処だけ</b>で、中身は CLI に読ませる。
/// </param>
/// <param name="ReuseWindowId">
/// 既にある窓のタブとして開くなら、その窓（設計 §33-5）。null なら新しい窓。
/// </param>
/// <param name="EnvironmentVariables">macOS の起動スクリプトで export する値（設計 §61-1）。</param>
/// <param name="Activity">起動前に準備し、セッションが読む記録先。永続化しない（設計 §61-2）。</param>
public sealed record TerminalLaunchRequest(
    string Title,
    string WorkingDirectory,
    string Command,
    IReadOnlyList<string> Arguments,
    string? ReuseWindowId = null,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    Activity.ActivityLaunch? Activity = null);

/// <summary>
/// 起動した窓（タブ）を指すハンドル。
/// </summary>
/// <remarks>
/// <b>タイトルで照合しない。</b> `osascript` の `do script` は
/// <c>tab 1 of window id 43990</c> を返すので、**安定したハンドルが最初から手に入る**
/// （§32-2c の実測）。タイトル照合は、人間が窓の名前を変えた瞬間に壊れる。
/// </remarks>
/// <param name="WindowId">ターミナルの窓。前面化はこれで行う。</param>
/// <param name="TabIndex">その窓の中のタブ。窓を共有するときに要る（§33-5）。</param>
/// <param name="PidFilePath">
/// 起動したシェルが自分の PID を書くファイル。
/// <b>起動時には、まだ書かれていないことがある</b>ので、
/// 読むのは終了させるとき（<see cref="ITerminalLauncher.TerminateAsync"/>）。
/// </param>
/// <param name="Tty">
/// そのタブの TTY（<c>/dev/ttys013</c>）。<b>タブを位置ではなく中身で同定する</b>（設計 §62-24）。
/// <para>
/// 窓 id とタブ番号は**位置**なので、タブを閉じたり並べ替えたりすると別のタブを指す。
/// TTY は Terminal.app が読み取り専用で持っていて、そのタブが死ぬまで変わらない。
/// <b>取れない CLI / OS では null</b>（Windows はコンソール窓なので持たない）。
/// </para>
/// </param>
public sealed record TerminalHandle(string WindowId, int TabIndex, string PidFilePath, string? Tty = null);

public abstract record TerminalLaunchResult
{
    public sealed record Launched(TerminalHandle Handle) : TerminalLaunchResult;

    /// <summary>
    /// 開き直そうとした窓が、まだ塞がっていた（設計 §41-2b）。
    /// </summary>
    /// <remarks>
    /// <b><see cref="Failed"/> と分ける。</b> こちらは
    /// <b>その窓で何かが走っていることを観測している</b> ——
    /// つまり<b>窓は生きている</b>ので、呼び出し側は handle を捨ててはいけない。
    /// <para>
    /// 文言で見分けない（§27-2 と同じ姿勢）。
    /// </para>
    /// </remarks>
    public sealed record WindowBusy(string Reason) : TerminalLaunchResult;

    /// <summary>起動できなかった。<b>理由を人間に見せる</b>（§28-1）。</summary>
    public sealed record Failed(string Reason) : TerminalLaunchResult;
}

public abstract record TerminalTerminateResult
{
    /// <summary>シグナルを送れた。<b>「死んだ」とは言わない</b>（§7）——送れたことだけを観測している。</summary>
    public sealed record Signalled(int ProcessGroupId) : TerminalTerminateResult;

    /// <summary>もう居ない。PID ファイルが無い場合もここ。</summary>
    public sealed record NotRunning(string Reason) : TerminalTerminateResult;

    public sealed record Failed(string Reason) : TerminalTerminateResult;
}

/// <summary>
/// 外部ターミナルを開き、前面に出し、終了させる（設計 §32）。
/// </summary>
/// <remarks>
/// <b>プロセスの終了を待たない。</b> 実測（§32-2e）で、3つの CLI はどれも
/// <b>turn が終わってもセッションを終了しない</b> ——
/// `agy -i` の help が "continue the session" と書いているとおりで、
/// Claude Code と Codex も同じだった。
/// <para>
/// したがって<b>完了の信号は <c>report.md</c> しかない</b>（§16-1 の publish 契約）。
/// 提案書にあった <c>exit_code</c> のファイル契約は<b>持たない</b> ——
/// あれが書かれるのは人間が窓を閉じたときだけで、仕事の終わりとは無関係だった。
/// </para>
/// <para>
/// <b>OS 依存であって UI 依存ではない</b>ので Core に置く。
/// Core の禁じ手は Avalonia を参照することであって、OS を知ることではない。
/// </para>
/// </remarks>
public interface ITerminalLauncher
{
    Task<TerminalLaunchResult> LaunchAsync(TerminalLaunchRequest request, CancellationToken ct);

    /// <summary>その窓を前面に出す。<b>戻り値は「出せたか」</b> —— 窓が閉じられていれば false。</summary>
    Task<bool> FocusAsync(TerminalHandle handle, CancellationToken ct);

    /// <summary>
    /// その窓で動いているものを終了させる（設計 §9）。
    /// </summary>
    /// <remarks>
    /// <b>プロセスグループへ送る。</b> 実測（§32-2d）で、
    /// <c>kill -TERM &lt;script-pid&gt;</c> では**子が生き残った** ——
    /// Terminal.app 起動時は PGID がスクリプトの PID と一致していたので、
    /// <c>kill -TERM -&lt;pgid&gt;</c> が正しい形である。
    /// これは §14-5 に未決で残していた foreground PGID の答えでもある。
    /// </remarks>
    Task<TerminalTerminateResult> TerminateAsync(TerminalHandle handle, CancellationToken ct);

    /// <summary>
    /// その窓を閉じる（設計 §62-25）。
    /// </summary>
    /// <remarks>
    /// <b>終了とは別の操作。</b> 実測（2026-09-21）で、窓を閉じても
    /// <b>中のプロセスは孤児として生き残った</b> —— 閉じる前に
    /// <see cref="TerminateAsync"/> で終わらせ、<b>居なくなったことを確かめる</b>。
    /// <para>
    /// <b>巻き込まない。</b> Terminal.app はタブを閉じられない（実測。タブは
    /// <c>close</c> を認識しない）ので、閉じるのは窓 —— <b>その窓にタブが1つのときだけ</b>。
    /// </para>
    /// </remarks>
    Task<TerminalCloseResult> CloseAsync(TerminalHandle handle, CancellationToken ct);

    /// <summary>
    /// 前回の残り（このアプリが開いた窓で、もう何も動いていないもの）を探す（設計 §62-25）。
    /// </summary>
    /// <remarks>
    /// <b>アプリを再起動するとハンドルを失う</b>ので、窓に付けた見出し（<c>custom title</c>）で探す。
    /// **動いている窓は返さない** —— 片付けの対象は、中身が終わっている窓だけ。
    /// <para>
    /// <b>できない OS では空</b>（既定の実装）。「無かった」と「探せない」を呼び出し側で分けたいときは、
    /// この OS でできるかを先に見る。
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<TerminalLeftover>> FindLeftoversAsync(string titlePrefix, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TerminalLeftover>>([]);
}

/// <summary>前回の残りの窓（設計 §62-25）。</summary>
/// <param name="Tty">同定に使う TTY。<b>位置では覚えない</b>（§62-24）。</param>
/// <param name="Title">窓に付けた見出し（<c>MultiAI-&lt;部門&gt;</c>）。人間に見せる。</param>
public sealed record TerminalLeftover(string Tty, string Title);

/// <summary>窓を閉じた結果（設計 §62-25）。<b>閉じられなかった理由を潰さない</b>（§7）。</summary>
public abstract record TerminalCloseResult
{
    public sealed record Closed : TerminalCloseResult;

    /// <summary>その窓はもう無い。<b>失敗ではない</b> —— 人間が先に閉じたのかもしれない。</summary>
    public sealed record NotFound : TerminalCloseResult;

    /// <summary>閉じなかった（タブが他にもある・中でまだ動いている）。</summary>
    public sealed record Kept(string Reason) : TerminalCloseResult;

    public sealed record Failed(string Reason) : TerminalCloseResult;
}
