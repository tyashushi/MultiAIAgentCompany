using System.Text.Json.Serialization;

namespace MultiAIAgentCompany.Core.Coordination;

/// <summary>
/// 仕事状態。設計 §7 の第3軸で、<c>state.json</c> に永続する唯一の状態。
/// </summary>
/// <remarks>
/// <c>System.Threading.Tasks.TaskStatus</c> とは別物。曖昧なところでは
/// <c>Coordination.TaskStatus</c> と修飾する。
/// </remarks>
public enum TaskStatus
{
    /// <summary>指示書はあるが、まだ部門へ投げていない。</summary>
    Drafted,

    /// <summary>部門へ dispatch した。</summary>
    Dispatched,

    /// <summary>部門が作業中。</summary>
    InProgress,

    /// <summary>(b) 判断の相談で止まっている。<c>question.md</c> がある。</summary>
    AwaitingAnswer,

    /// <summary>報告書が出た。人間のレビュー待ち。</summary>
    Reported,

    /// <summary>人間が受け入れた。終端。</summary>
    Accepted,

    /// <summary>人間が差し戻した。終端ではない。</summary>
    Rejected,

    /// <summary>失敗して終わった。終端。</summary>
    Failed,

    /// <summary>取り消された。終端。</summary>
    Cancelled,
}

/// <summary>
/// 誰がその遷移を起こしたか。設計 §6。
/// <b>自動化が勝手に終端状態から復帰できてはいけない。</b>
/// </summary>
public enum TransitionOrigin
{
    Automation,
    Human,
}

/// <summary>
/// 書き込み権の lease。設計 §8 —— <b>書き込み権は同時に1部門のみ</b>。
/// 他の部門は読み取りのみで、書き込みが要る仕事は待つ。
/// </summary>
/// <remarks>
/// CLI のサンドボックス境界に頼らないこと。実測（§13-2）で codex の
/// <c>workspace-write</c> はワークスペース外の <c>/tmp</c> と <c>$TMPDIR</c> にも
/// 承認なしで書けた。「承認を求めてこなかった＝ワークスペース内で完結した」ではない。
/// </remarks>
/// <param name="DepartmentId">書き込み権を持っている部門。</param>
/// <param name="AcquiredAt">取得時刻。</param>
/// <param name="ExpiresAt">失効時刻。アプリが落ちても、時間で解けるようにしておく。</param>
public sealed record WriteLease(string DepartmentId, DateTimeOffset AcquiredAt, DateTimeOffset ExpiresAt)
{
    public bool IsValidAt(DateTimeOffset now) => now < ExpiresAt;
}

/// <summary>
/// <c>state.json</c> の中身。<b>状態の第一根拠</b>（設計 §7、<c>EvidenceSource.Document</c>）。
/// </summary>
/// <param name="Slug">タスクの識別子。ディレクトリ名と一致する。</param>
/// <param name="Status">仕事状態。</param>
/// <param name="Revision">
/// 楽観ロック用。読んだ revision と違っていたら書かない。
/// 複数部門とアプリが同じファイルを触るので、last-write-wins にしない。
/// </param>
/// <param name="DepartmentId">担当部門。</param>
/// <param name="LastTransitionOrigin">直前の遷移を起こしたのが自動化か人間か。</param>
/// <param name="UpdatedAt">最終更新。</param>
/// <param name="Lease">書き込み権。持っていなければ null。</param>
/// <param name="Note">人間向けの1行。<b>秘密値を入れない</b>（設計 §10）。</param>
public sealed record TaskState(
    string Slug,
    TaskStatus Status,
    long Revision,
    string DepartmentId,
    TransitionOrigin LastTransitionOrigin,
    DateTimeOffset UpdatedAt,
    WriteLease? Lease = null,
    string? Note = null);

/// <summary>
/// <c>state.json</c> の JSON 設定。列挙は名前で書く（人間が読んで直せるように）。
/// </summary>
public static class TaskStateJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
}
