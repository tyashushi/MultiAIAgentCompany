namespace MultiAIAgentCompany.Core.Agents;

/// <summary>
/// 1つの CLI を1部門として動かすためのアダプタ。設計 §4 の <c>Agents/</c>。
/// </summary>
/// <remarks>
/// 実装は CLI ごとに分かれる。共通の枠に無理に押し込めない —— 3つは対称ではない（設計 §2）。
/// </remarks>
public interface IAgentAdapter
{
    AgentKind Kind { get; }

    AgentCapabilities Capabilities { get; }

    /// <summary>実際に起動した CLI の版。検出器はこれで切り替わる。判らなければ null。</summary>
    string? DetectedVersion { get; }

    /// <summary>
    /// 部門を起動する。<b>シェルを噛ませない</b>（設計 §13-6 の規則1）。
    /// <c>bash -lc claude</c> のようにすると macOS の <c>/bin/bash</c> 3.2 の readline が
    /// 経路に入る余地が生まれる。CLI を直接 exec すること。
    /// </summary>
    Task<Sessions.IAgentSession> StartAsync(
        Workspace.WorkspaceRef workspace,
        DriveMode mode,
        CancellationToken ct);

    /// <summary>
    /// 承認要求の通知。(a) と (b) の両方がここを通るが、<see cref="ApprovalRequest.Kind"/> で区別できる。
    /// </summary>
    event EventHandler<ApprovalRequest>? ApprovalRequested;

    /// <summary>
    /// 決定を返す。<b>生の string を受けない</b>（設計 §14-4）。
    /// <paramref name="decision"/> は <paramref name="request"/> が提示したものでなければならず、
    /// そうでなければ <see cref="ArgumentException"/> になる。
    /// </summary>
    Task RespondAsync(ApprovalRequest request, ApprovalDecision decision, CancellationToken ct);

    /// <summary>状態の根拠になる観測。信頼順は <see cref="Status.EvidenceSource"/> を見る。</summary>
    event EventHandler<Status.Evidence>? Observed;
}
