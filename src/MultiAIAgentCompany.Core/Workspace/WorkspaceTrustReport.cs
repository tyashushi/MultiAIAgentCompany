using MultiAIAgentCompany.Core.Agents;

namespace MultiAIAgentCompany.Core.Workspace;

/// <summary>
/// ワークスペースが CLI から信頼されているか。設計 §13-9。
/// </summary>
/// <remarks>
/// <b>3値であることが本質。</b> 「信頼されていない」と「分からない」を同じにすると、
/// 読めていないだけなのに人間へ無用な操作を促すことになる（§13-9 規則1）。
/// </remarks>
public enum WorkspaceTrustState
{
    /// <summary>設定ファイルを読めて、このワークスペースが信頼されていた。</summary>
    Trusted,

    /// <summary>設定ファイルを読めて、このワークスペースは載っていなかった。</summary>
    NotTrusted,

    /// <summary>
    /// <b>分からない。</b> ファイルが無い / 形が違う / 読めない。
    /// 「信頼されていない」と言わない。
    /// </summary>
    Unknown,

    /// <summary>
    /// 分からないのは <see cref="Unknown"/> と同じ。ただし理由は<b>記録がまだ無い</b>こと ——
    /// その CLI をこのフォルダで一度起動すれば分かる（設計 §55-5）。
    /// </summary>
    NoRecord,
}

/// <param name="Agent">どの CLI についての判定か。</param>
/// <param name="State">3値の判定。</param>
public sealed record WorkspaceTrustRow(AgentKind Agent, WorkspaceTrustState State);

/// <summary>ワークスペースに対する、各 CLI の trust 判定をまとめる。</summary>
public static class WorkspaceTrustReport
{
    /// <summary>
    /// 各 probe に聞いて回る。<b>1つが例外を投げても他を止めない</b> ——
    /// 1つ読めないことは、他が読めないことを意味しない。
    /// </summary>
    public static async Task<IReadOnlyList<WorkspaceTrustRow>> BuildAsync(
        WorkspaceRef workspace,
        IEnumerable<IWorkspaceTrustProbe> probes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(probes);

        var rows = new List<WorkspaceTrustRow>();
        foreach (var probe in probes)
        {
            WorkspaceTrustState state;
            try
            {
                state = await probe.IsTrustedAsync(workspace, ct).ConfigureAwait(false) switch
                {
                    true => WorkspaceTrustState.Trusted,
                    false => WorkspaceTrustState.NotTrusted,
                    null => await probe.HasNoRecordAsync(ct).ConfigureAwait(false)
                        ? WorkspaceTrustState.NoRecord
                        : WorkspaceTrustState.Unknown,
                };
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // 聞けなかったのだから、信頼されていないとは言えない。
                state = WorkspaceTrustState.Unknown;
            }

            rows.Add(new WorkspaceTrustRow(probe.Kind, state));
        }

        return rows;
    }
}
