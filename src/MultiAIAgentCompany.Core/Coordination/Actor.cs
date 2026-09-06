namespace MultiAIAgentCompany.Core.Coordination;

/// <summary>lease を取れる主体。部門と秘書は別物（設計 §17-2）。</summary>
public enum ActorKind
{
    Department,

    /// <summary>
    /// 人間が中央ペインで会話する唯一の相手（§0）。<b>部門ではない</b> ——
    /// 右ペインのタイルにも TaskStatus にも入らない。
    /// </summary>
    Secretary,
}

/// <summary>lease を取れる主体。</summary>
/// <summary>
/// lease を取れる主体。<b>不正な値を作れないようにする</b> ——
/// <c>lease.json</c> に書かれてしまうと、本来の持ち主が更新も解放もできなくなる。
/// </summary>
/// <remarks>
/// <b>値型にしない。</b> <c>default(Actor)</c> が「Id の無い部門」になってしまい、
/// ファクトリの検査を素通りする道が残る。
/// </remarks>
public sealed record Actor
{
    /// <summary>秘書の予約 ID。<b>部門がこの ID を名乗っても秘書にはならない</b>（§17-2）。</summary>
    public const string SecretaryId = "secretary";

    public Actor(ActorKind kind, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "未定義の行為者");
        }

        if (kind is ActorKind.Secretary && !string.Equals(id, SecretaryId, StringComparison.Ordinal))
        {
            // 「秘書」を名乗るのに ID が違うと、照合できない保持者になる。
            throw new ArgumentException($"秘書の ID は '{SecretaryId}' でなければならない", nameof(id));
        }

        Kind = kind;
        Id = id;
    }

    public ActorKind Kind { get; }

    public string Id { get; }

    public static Actor OfDepartment(string departmentId) => new(ActorKind.Department, departmentId);

    public static Actor TheSecretary { get; } = new(ActorKind.Secretary, SecretaryId);

    public override string ToString() => $"{Kind}:{Id}";
}
