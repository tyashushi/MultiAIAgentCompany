using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Status;

namespace MultiAIAgentCompany.Core.Sessions;

/// <summary>
/// 1部門ぶんの子プロセス。設計 §9 —— <b>アプリが全部門の親になる。</b>
/// </summary>
/// <remarks>
/// <b>駆動モードで別のインターフェイスに分ける。</b>
/// v1 にあるのは構造化（<see cref="IStructuredSession"/>）だけ ——
/// パイプで、承認の往復が閉じる。
/// <para>
/// この型が持つのは<b>生死と観測</b>だけ。駆動モードが増えたとき、
/// 1つの型に押し込むと、パイプのセッションに <c>ResizeAsync</c> が生えて
/// <b>黙って何もしない実装</b>になる（§2 の「3つの CLI は対称ではない」と同じ話）。
/// </para>
/// </remarks>
public interface IAgentSession : IAsyncDisposable
{
    string DepartmentId { get; }

    ProcessIdentity Identity { get; }

    DriveMode Mode { get; }

    /// <summary>
    /// 段階的な終了。入力停止 → 正常終了要求 → SIGTERM → 最後に SIGKILL（設計 §9）。
    /// <b>対象は子1つではなくプロセスツリー</b> —— CLI は自分で子を作る。
    /// </summary>
    Task StopAsync(CancellationToken ct);

    event EventHandler<Evidence>? Observed;

    event EventHandler<int>? Exited;

    /// <summary>
    /// 診断のための生の出力（設計 §22）。<b>ライブ表示専用</b>。
    /// </summary>
    /// <remarks>
    /// <b><see cref="Spoke"/> と同じ扱い —— 永続させない。</b>
    /// <see cref="Status.Evidence"/> へ渡さない（§10 / §14-5）。
    /// stderr には作業パス・コマンド・スタックトレース・秘密値が混ざり得るので、
    /// <c>Observed</c> が運ぶのは<b>分類だけ</b>、こちらが<b>中身</b>を運ぶ。
    /// <para>
    /// これが要る理由は実測にある（§13-3 追記2）—— Antigravity は未知の input event を
    /// stdout に何も返さず stderr にだけ警告して捨てる。分類だけでは
    /// 「送ったのに何も起きない」の原因に辿り着けない。
    /// </para>
    /// </remarks>
    event EventHandler<LiveDiagnostic>? Diagnosed;

    /// <summary>
    /// 購読より前に出ていた診断（設計 §22-2）。
    /// </summary>
    /// <remarks>
    /// <b>起動の失敗こそ、この機能が見せたいもの</b>（trust・login・ハンドシェイク）。
    /// ところがそれは<b>購読するより前に</b>出る —— アダプタはハンドシェイクを終えてから
    /// セッションを返すので、イベントだけでは取りこぼす。だからセッション自身が取り置く。
    /// </remarks>
    IReadOnlyList<LiveDiagnostic> RecentDiagnostics(int count);
}

/// <summary>
/// 構造化モードのセッション。stdin / stdout のパイプで、**承認の往復が閉じる**（設計 §13-1 / §13-2）。
/// </summary>
/// <remarks>
/// <b>stdout を読み続けること。</b> 読むのを止めるとパイプのバッファが詰まって
/// 子プロセスが止まる（設計 §5 の「非表示のターミナルも読み続ける」はパイプにも当てはまる）。
/// </remarks>
public interface IStructuredSession : IAgentSession
{
    /// <summary>人間・秘書からの1メッセージを送る。</summary>
    Task SendUserMessageAsync(string text, CancellationToken ct);

    /// <summary>
    /// 承認に答える。<paramref name="decision"/> は
    /// <paramref name="request"/> が提示したものでなければならない（設計 §5）。
    /// </summary>
    /// <param name="reason">
    /// 人間が書いた理由。<b>エージェントに届く</b>（Claude は拒否の <c>message</c> として読む）。
    /// 無いと、拒否された agent は理由の分からないまま盲目的に再試行する。
    /// <para>
    /// 「提示された選択肢」ではなく「その回の応答」に属するので、
    /// <see cref="ApprovalDecision"/> ではなくここに置く。
    /// </para>
    /// <para>
    /// <b>秘密値を書かない。</b> 人間が打った文字列がそのままモデルへ渡る（設計 §10）。
    /// </para>
    /// </param>
    Task RespondAsync(ApprovalRequest request, ApprovalDecision decision, string? reason, CancellationToken ct);

    /// <summary>(a) ランタイム承認の要求（設計 §3）。</summary>
    event EventHandler<ApprovalRequest>? ApprovalRequested;

    /// <summary>
    /// エージェントの<b>発言そのもの</b>。<b>ライブ表示専用</b>（設計 §17-5 / §10）。
    /// </summary>
    /// <remarks>
    /// <b>これを永続させない。<see cref="Status.Evidence"/> へ渡さない。</b>
    /// §10 は「画面と永続ログを分離し、永続する側だけを redact する」と決めている ——
    /// <c>Observed</c> は要約（秘密値なし）を運び、こちらは中身を運ぶ。
    /// <para>
    /// 橋渡しを完全には防げない（購読側が任意のコードを書ける）。
    /// <b>ヘルパも暗黙変換も置かない</b>ことと、テストで固定することまでが防御。
    /// </para>
    /// </remarks>
    event EventHandler<LiveAgentMessage>? Spoke;

    /// <summary>
    /// turn が終わった。<b>「終わった」は「成功した」ではない。</b>
    /// 判定は3層すべてを見た <see cref="OutcomeVerdict"/> で渡す（設計 §13 末尾 / §14-4）。
    /// </summary>
    event EventHandler<OutcomeVerdict>? TurnFinished;
}

// **`ITuiSession` は消した**（設計 §22-4、2026-09-06）。
// v1 はターミナルを持たない。実装の無いインターフェイスを残すと
// **使えるかのように見える型**になり、読み手に要らない分岐を背負わせる。
// PTY 側の契約（session 隔離、TIOCSWINSZ、foreground へのシグナル、隠しても読み続ける）と
// 実測は §9 / §13-4 / §13-6 / §13-7 / §13-8 に残してある。

/// <summary>
/// プロセスの同一性。<b>PID だけでは足りない</b>（再利用される）。設計 §9。
/// </summary>
/// <param name="Pid">子プロセスの PID。</param>
/// <param name="SessionId">setsid で作った session id。PTY でなければ null 相当の 0。</param>
/// <param name="ProcessGroupId">process group id。シグナルの宛先はこちら。</param>
/// <param name="StartedAt">開始時刻。PID 再利用を見破るための第2の鍵。</param>
public readonly record struct ProcessIdentity(int Pid, int SessionId, int ProcessGroupId, DateTimeOffset StartedAt);

/// <summary>
/// エージェントが言ったこと。<b>ライブ表示だけに使う</b>（設計 §17-5）。
/// </summary>
/// <remarks>
/// <b>型名が性格を表している。</b> これを保存したり、
/// <see cref="Status.Evidence.RedactedSummary"/> に入れたりしない ——
/// 中身に何が入っているか分からないため（§10）。
/// </remarks>
/// <param name="Text">発言の本文。<c>thinking</c> も <c>tool_use</c> も含まない（§17-5）。</param>
public sealed record LiveAgentMessage(string Text);

/// <summary>
/// 診断の1行。<b>ライブ表示だけに使う</b>（設計 §22）。
/// </summary>
/// <remarks>
/// <b>型名が性格を表している。</b> <see cref="LiveAgentMessage"/> と同じく、
/// 保存も <see cref="Status.Evidence.RedactedSummary"/> への転記もしない（§10）。
/// </remarks>
/// <param name="Stream">どちらの出力か。</param>
/// <param name="Text">
/// 行の中身。<b>redact していない。</b> だから永続させない ——
/// 秘密値が混ざり得る前提で、画面にだけ出す（§14-5）。
/// </param>
public sealed record LiveDiagnostic(DiagnosticStream Stream, string Text);

/// <summary>診断の出どころ。</summary>
public enum DiagnosticStream
{
    /// <summary>子プロセスの stderr。</summary>
    StandardError,

    /// <summary>
    /// アプリ側で分かった経路の異常（読めなかった行、解釈できなかったイベント）。
    /// <b>「出力が無い」を「正常」にしないため</b>に出す（§7）。
    /// </summary>
    Protocol,
}
