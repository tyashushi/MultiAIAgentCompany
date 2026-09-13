using MultiAIAgentCompany.Core.Agents;

namespace MultiAIAgentCompany.Core.Workspace;

/// <summary>
/// 部門定義を読んだときに、人間に伝えるべきこと（設計 §32-10）。
/// </summary>
/// <remarks>
/// <b>版番号では見つけられない。</b> §23-4 に <c>schemaVersion</c> を持つかどうかを
/// 未決で置いていたが、実機で踏んだ2件はどちらも<b>中身を見れば分かる</b>もので、
/// 版が同じでも起きる（人間は手で書き換える）。
/// <para>
/// <b>弾かない。</b> ここで返すのは警告であって、<c>Validate</c> の失敗ではない ——
/// §30-6 の「読めなかったら開かない」は<b>壊れたファイル</b>の話で、
/// **古い設定や余分なキーは壊れていない。** アプリは書き換えもしない（§30-4）。
/// **人間に見せて、人間が直す。**
/// </para>
/// </remarks>
public static class DepartmentWarnings
{
    /// <summary>
    /// その部門が<b>ツール権限を人間に聞けない</b>組み合わせか（設計 §30-1 の実測）。
    /// </summary>
    /// <remarks>
    /// 承認の往復を持たない CLI（Antigravity）を <see cref="DriveMode.Structured"/> で
    /// 動かすと、headless では<b>全部自動拒否</b>される —— 文書を読むことすらできず、
    /// 仕事は永久に <c>Dispatched</c> のまま止まる。
    /// <para>
    /// <b>これは 2026-09-09 に実機で踏んだ。</b> 既定を <c>ExternalTerminal</c> に変えたが、
    /// <b>既にある <c>departments.json</c> は上書きしない</b>（§30-6）ので、
    /// 古いフォルダを開くと**そのまま同じ失敗を再現する。**
    /// </para>
    /// </remarks>
    public static bool CannotAskHuman(DepartmentDefinition department)
    {
        ArgumentNullException.ThrowIfNull(department);
        return department.Mode is DriveMode.Structured
            && !AgentCapabilities.For(department.Agent).SupportsRuntimeApprovalRoundTrip;
    }

    /// <summary>人間に見せる1行を作る。<b>何が起きるかと、どう直すかを書く。</b></summary>
    public static IReadOnlyList<DepartmentWarning> For(IReadOnlyList<DepartmentDefinition> departments)
    {
        ArgumentNullException.ThrowIfNull(departments);

        var warnings = new List<DepartmentWarning>();
        foreach (var department in departments)
        {
            if (CannotAskHuman(department))
            {
                warnings.Add(new DepartmentWarning(
                    department.Id,
                    $"{department.DisplayName} は {department.Agent} を Structured で動かす設定です。"
                    + "**この組み合わせはツール権限を人間に聞けません**（headless では全部自動拒否され、"
                    + "報告を出せないまま止まります）。"
                    + $"`departments.json` の `{department.Id}` の `mode` を `ExternalTerminal` にすると、"
                    + "そのターミナルで人間が承認できます"));
            }

            // **設定を直さず、何が効かないかを伝える**（設計 §51-3）。
            if (department.PermissionMode is { } permissionMode)
            {
                if (!AgentPermissionModes.For(department.Agent).Contains(permissionMode))
                {
                    warnings.Add(new DepartmentWarning(
                        department.Id,
                        $"{department.DisplayName} の権限モード `{permissionMode}` は {department.Agent} にはありません。"
                        + "**権限モードを渡さずに起動します**。"
                        + $"`departments.json` の `{department.Id}` の `permissionMode` を消すか、"
                        + "その CLI が持っているモードに直してください"));
                }

                if (department.Mode is DriveMode.Structured)
                {
                    warnings.Add(new DepartmentWarning(
                        department.Id,
                        $"{department.DisplayName} は Structured で動かす設定です。"
                        + "**権限モードは外部ターミナルの部門にしか効きません**。"
                        + $"`departments.json` の `{department.Id}` の `permissionMode` を消すか、"
                        + "`mode` を `ExternalTerminal` に直してください"));
                }
            }
        }

        return warnings;
    }
}

/// <param name="DepartmentId">どの部門の話か。</param>
/// <param name="Message">人間に見せる文。<b>秘密値を入れない</b>（設計 §10）。</param>
public sealed record DepartmentWarning(string DepartmentId, string Message);
