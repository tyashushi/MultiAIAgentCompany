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
/// <c>state.json</c> の中身。<b>状態の第一根拠</b>（設計 §7、<c>EvidenceSource.Document</c>）。
/// </summary>
/// <param name="Slug">タスクの識別子。ディレクトリ名と一致する。</param>
/// <param name="Status">仕事状態。</param>
/// <param name="AttemptId">
/// 何回目の試行か（設計 §14-1）。差し戻すと1つ進み、それまでの指示書・報告書は
/// <c>attempts/&lt;n&gt;/</c> へ封じられる。「どの指示に対する報告か」はこれで決まる。
/// </param>
/// <param name="Revision">
/// 楽観ロック用。読んだ revision と違っていたら書かない。
/// <b>部門同士の競合を捌くためのものではない</b> —— <c>state.json</c> を書くのはアプリだけ
/// （設計 §14-1）。用途はアプリの知らない書き換え（人間の手直し、二重起動）の検出で、
/// 不一致を見たらマージせず、書き込みを拒否して人間に見せる。
/// </param>
/// <param name="DepartmentId">担当部門。</param>
/// <param name="LastTransitionOrigin">直前の遷移を起こしたのが自動化か人間か。</param>
/// <param name="UpdatedAt">最終更新。</param>
/// <param name="Note">人間向けの1行。<b>秘密値を入れない</b>（設計 §10）。</param>
public sealed record TaskState(
    string Slug,
    TaskStatus Status,
    int AttemptId,
    long Revision,
    string DepartmentId,
    TransitionOrigin LastTransitionOrigin,
    DateTimeOffset UpdatedAt,
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

        // フィールドが欠けていたら既定値で埋めずに落とす。
        // これが無いと `status` を落とした state.json が Drafted として読めてしまい、
        // 信頼順1位の根拠（EvidenceSource.Document）が嘘をつく。
        // state.json は人間が手で直せるファイルなので、欠落は想定内の入力である。
        RespectRequiredConstructorParameters = true,
    };
}
