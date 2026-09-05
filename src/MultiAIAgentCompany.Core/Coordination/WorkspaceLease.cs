namespace MultiAIAgentCompany.Core.Coordination;

/// <summary>
/// ワークスペース全体で1つずつしか無い権利。設計 §14-2。
/// </summary>
/// <remarks>
/// 初版は <c>WriteLease</c> を各タスクの <c>TaskState</c> に持たせていたので、
/// 別タスクに別部門の有効な lease を同時に置けた（再レビューで発覚）。
/// 置き場所を <c>.company/lease.json</c> —— ワークスペースに1つ —— へ移す。
/// </remarks>
public enum LeaseKind
{
    /// <summary>成果物への書き込み。同時に1部門（設計 §8）。</summary>
    Write,

    /// <summary>
    /// Unity を触る仕事を走らせる権利。同時に1部門（設計 §14-2）。
    /// <see cref="Write"/> とは別の権利にする —— Unity を読むだけの仕事は成果物を書かないが、
    /// MCP のキューは食う（実測 §13-5b: 読み取り専用の混在負荷で6件失敗、うち4件が TimeoutError）。
    /// </summary>
    Unity,
}

/// <summary>1つの権利を、いま誰が持っているか。</summary>
/// <param name="Kind">権利の種類。</param>
/// <param name="DepartmentId">持っている部門。</param>
/// <param name="TaskSlug">その権利で走っている仕事。</param>
/// <param name="AcquiredAt">取得時刻。</param>
/// <param name="ExpiresAt">失効時刻。アプリが落ちても時間で解ける。</param>
public sealed record LeaseHolder(
    LeaseKind Kind,
    string DepartmentId,
    string TaskSlug,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ExpiresAt)
{
    /// <summary>その時点で有効か。<b>取得前は有効ではない</b>（初版は未来の lease も有効にしていた）。</summary>
    public bool IsValidAt(DateTimeOffset now) => AcquiredAt <= now && now < ExpiresAt;
}

/// <summary>
/// <c>.company/lease.json</c> の中身。種類ごとに保持者は<b>高々1つ</b>。
/// </summary>
/// <remarks>
/// <b>これは保証ではなく約束である。</b> アプリは MCP 経路に立たないので（§8 / §14-2）、
/// 約束を破って Unity を叩く CLI を止められない。だから死活判定には直列化の外にある
/// <c>resources/read</c> を使い、タイムアウトを「Unity が落ちた」と読まない（§13-5b）。
/// </remarks>
/// <param name="Revision">楽観ロック。アプリの知らない書き換えを検出するためのもの。</param>
/// <param name="Holders">種類ごとの保持者。</param>
public sealed record WorkspaceLeases(long Revision, IReadOnlyDictionary<LeaseKind, LeaseHolder> Holders)
{
    public static WorkspaceLeases Empty { get; } =
        new(0, new Dictionary<LeaseKind, LeaseHolder>());

    /// <summary>その部門が、その時点でその権利を持っているか。</summary>
    public bool IsHeldBy(LeaseKind kind, string departmentId, DateTimeOffset now) =>
        Holders.TryGetValue(kind, out var holder)
        && holder.IsValidAt(now)
        && string.Equals(holder.DepartmentId, departmentId, StringComparison.Ordinal);

    /// <summary>その権利を、いま新しく取れるか（誰も持っていないか、失効しているか）。</summary>
    public bool CanAcquire(LeaseKind kind, DateTimeOffset now) =>
        !Holders.TryGetValue(kind, out var holder) || !holder.IsValidAt(now);
}
