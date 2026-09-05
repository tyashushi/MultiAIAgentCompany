namespace MultiAIAgentCompany.Core.Status;

/// <summary>
/// 根拠の出どころ。設計 §7 の信頼順そのもので、数値が小さいほど信頼できる。
/// </summary>
/// <remarks>
/// 生の PTY バイト列に正規表現を当てた観測は、この列挙に居場所が無い。
/// TUI はカーソル移動・消去・行上書き・alternate screen を使うので、
/// 見えている文字列とバイト列は一致しない。<see cref="RenderedScreen"/> は
/// 端末エミュレータが構築した画面に対する観測だけを指す。
/// </remarks>
public enum EvidenceSource
{
    /// <summary><c>.company/</c> の状態遷移。再起動を跨いで残る。最も確か。</summary>
    Document = 1,

    /// <summary>stream-json / app-server の構造化イベント。</summary>
    StructuredEvent = 2,

    /// <summary>アプリ自身が仕事を投げた事実。</summary>
    Dispatch = 3,

    /// <summary>端末エミュレータが構築した画面に対する、CLI 別・版別の検出器。</summary>
    RenderedScreen = 4,

    /// <summary>プロセスの終了。</summary>
    ProcessExit = 5,
}

/// <summary>
/// 状態が「なぜそう言えるのか」。設計 §7。状態は必ずこれと一緒に持つ。文字列1つでは足りない。
/// </summary>
/// <param name="Source">出どころ。信頼順は <see cref="EvidenceSource"/> の値の昇順。</param>
/// <param name="ObservedAt">観測時刻。鮮度の判定に使う。古い根拠は現在の証拠ではない。</param>
/// <param name="SessionId">観測元のセッション。</param>
/// <param name="TurnId">観測元の turn。</param>
/// <param name="Agent">どのエージェントについての観測か。</param>
/// <param name="AgentVersion">観測時に判っていた CLI の版。検出器は版に依存する。</param>
/// <param name="DetectorVersion">判定した検出器の版。知らない版なら判定させない。</param>
/// <param name="RedactedSummary">
/// 人間向けの1行。<b>秘密値を入れない</b>（設計 §10）。ここは画面ではなく永続しうる経路。
/// </param>
public sealed record Evidence(
    EvidenceSource Source,
    DateTimeOffset ObservedAt,
    string? SessionId,
    string? TurnId,
    AgentRef Agent,
    string? AgentVersion,
    string? DetectorVersion,
    string RedactedSummary)
{
    /// <summary>この根拠がまだ現在の証拠と言えるか。</summary>
    public bool IsFreshAt(DateTimeOffset now, TimeSpan maxAge) => now - ObservedAt <= maxAge;

    /// <summary>
    /// 信頼順で強い方を返す。同順なら新しい方。設計 §7 の信頼順を1箇所に閉じ込めるための唯一の比較。
    /// </summary>
    public static Evidence Stronger(Evidence a, Evidence b)
    {
        if (a.Source != b.Source)
        {
            return a.Source < b.Source ? a : b;
        }

        return a.ObservedAt >= b.ObservedAt ? a : b;
    }
}

/// <summary>どの部門のどのエージェントか、を指す軽い参照。</summary>
/// <param name="DepartmentId">部門の識別子。</param>
/// <param name="Kind">エージェントの種類。</param>
public readonly record struct AgentRef(string DepartmentId, Agents.AgentKind Kind);

/// <summary>
/// 3軸の状態を根拠つきで束ねたもの。設計 §7。
/// <b>1つに潰さない。</b> 潰すと「CLI がクラッシュした」と「画面が解析できない」が
/// 両方「不明」になり、人間が取るべき行動が分からなくなる。
/// </summary>
/// <param name="Runtime">稼働状態と、その根拠。</param>
/// <param name="Activity">活動状態と、その根拠。</param>
/// <param name="Work">仕事状態。<c>.company/</c> に無ければ null（仕事を持っていない）。</param>
public sealed record DepartmentStatus(
    Observed<RuntimeState> Runtime,
    Observed<ActivityState> Activity,
    Observed<Coordination.TaskStatus>? Work);

/// <summary>値と、その値をそう判定した根拠の対。</summary>
public sealed record Observed<T>(T Value, Evidence Evidence);
