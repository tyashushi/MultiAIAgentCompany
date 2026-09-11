namespace MultiAIAgentCompany.Core.Sessions;

/// <summary>
/// <see cref="TurnGate"/> の今の様子。<b>観測値だけ</b>（設計 §36）。
/// </summary>
/// <param name="InFlight">走っている turn があるか。</param>
/// <param name="Since">
/// その turn を stdin に書いた時刻。走っていなければ null。
/// <b>途中の発話では動かない</b> —— 測るのは「終わりを観測していない時間」である。
/// </param>
/// <param name="Queued">まだ書かれていない数。</param>
public readonly record struct TurnActivity(bool InFlight, DateTimeOffset? Since, int Queued);

/// <summary>
/// 期限までに turn の終わりを観測していない（設計 §36）。
/// </summary>
/// <param name="Since">その turn を書いた時刻。</param>
/// <param name="Elapsed">そこからの経過。</param>
/// <param name="Queued">
/// そのあいだに積まれた数。<b>0 でも出す</b> ——
/// 積まれていなくても「送ったのに何も起きない」は成立している（§7）。
/// </param>
public sealed record TurnSilence(DateTimeOffset Since, TimeSpan Elapsed, int Queued);

/// <summary>
/// 期限までに turn の終わりを観測したかを計算する（設計 §36）。
/// </summary>
/// <remarks>
/// <b>§31 の <see cref="Coordination.ReportWatch"/> と同じ形。</b> 純関数で、時刻も期限も引数で受け、
/// <b>どこにも書かない</b> —— 計算値をファイルへ書き出すと、人間がそちらを編集して
/// 「設定したのに黙って無視される」が起きる（§31-1）。
/// <para>
/// <b>沈黙から相手の状態を推定してはいない。</b> 言っているのは
/// <b>「アプリは期限までに turn の終わりを観測しなかった」</b>という、アプリ自身についての事実だけ
/// （§7 / §31-5）。だから状態は動かさず、人間に出す用件も「止まっている」ではない。
/// </para>
/// </remarks>
public static class TurnWatch
{
    /// <summary>
    /// アプリ既定の期限（設計 §36-2）。
    /// </summary>
    /// <remarks>
    /// <b>設定に出さない。</b> 秘書には部門定義が無い（§17-2）ので、部門ごとにすると
    /// 秘書だけ別経路になる。<b>ここは分かっていて雑にしている</b>（§31-4 と同じ割り切り）。
    /// </remarks>
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromMinutes(10);

    /// <param name="awaitingHuman">
    /// <b>その turn が、人間の答えを待っているか</b>（承認が出たまま返事をしていない）。
    /// <para>
    /// <b>これは沈黙ではない。</b> §31-2 が <c>AwaitingAnswer</c> を
    /// 「人間の番であって、部門の沈黙ではない」として除いたのと同じ ——
    /// 待たせているのはこちらなので、<b>相手を見に行けと言ってはいけない。</b>
    /// </para>
    /// </param>
    public static TurnSilence? Of(
        TurnActivity activity, bool awaitingHuman, TimeSpan? deadline, DateTimeOffset now)
    {
        if (awaitingHuman || deadline is null || deadline.Value <= TimeSpan.Zero)
        {
            return null;
        }

        if (!activity.InFlight || activity.Since is not { } since)
        {
            return null;
        }

        var elapsed = now - since;

        // 時計が巻き戻ったときに嘘をつかない。期限ちょうどでは出さない（§31-2）。
        if (elapsed < TimeSpan.Zero || elapsed <= deadline.Value)
        {
            return null;
        }

        return new TurnSilence(since, elapsed, activity.Queued);
    }
}
