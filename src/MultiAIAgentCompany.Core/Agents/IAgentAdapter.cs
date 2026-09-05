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
    /// <param name="departmentId">
    /// どの部門として動かすか。<b>同じ CLI を複数の部門に割り当てられる</b>ので、
    /// エージェントの種類だけでは足りない。
    /// </param>
    /// <returns>
    /// <paramref name="mode"/> に応じて <see cref="Sessions.IStructuredSession"/> か
    /// <see cref="Sessions.ITuiSession"/> を返す。どちらも <see cref="Sessions.IAgentSession"/>。
    /// </returns>
    Task<Sessions.IAgentSession> StartAsync(
        Workspace.WorkspaceRef workspace,
        string departmentId,
        DriveMode mode,
        CancellationToken ct);
}
