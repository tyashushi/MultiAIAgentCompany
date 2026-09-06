using System.Text.Json;

namespace MultiAIAgentCompany.Core.Coordination;

/// <summary>
/// 読めない <c>lease.json</c> からの復帰（設計 §23）。
/// </summary>
/// <remarks>
/// <b>自動で作り直さない</b>（§14-2）。読めない lease は「誰も持っていない」ではなく
/// <b>「持ち主を検証できない」</b> —— 失効した lease（持ち主は分かるが期限切れ）よりも
/// 情報が少ないので、自動判断の根拠としてはさらに弱い。
/// <para>
/// 人間が押したときだけ、<b>隔離してから</b>空の lease を作る。
/// <c>state.json</c> が読めないときの隔離（§16-4）と同じ姿勢。
/// </para>
/// </remarks>
public static class LeaseRecovery
{
    /// <summary>
    /// なぜ読めないかを、人間に見せる言葉にする（設計 §23-2）。
    /// </summary>
    /// <remarks>
    /// <b>診断を詳しくするだけで、扱いは変えない。</b> 古い形式と分かっても
    /// <c>departmentId → holder</c> と読み替えない（§17-2）——
    /// 推測で読み替えると、本来の持ち主が更新も解放もできない lease を作る。
    /// </remarks>
    public static string Describe(string reason, string? content)
    {
        ArgumentNullException.ThrowIfNull(reason);

        if (content is null)
        {
            return reason;
        }

        try
        {
            using var document = JsonDocument.Parse(content);

            // **形を確かめてから触る。** `TryGetProperty` はオブジェクト以外で例外を投げるので、
            // ここで落ちると**復旧項目を出すための診断が、復旧画面ごと落とす**（レビューで発覚）。
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return $"現在の形式として不完全です（オブジェクトではありません）: {reason}";
            }

            if (document.RootElement.TryGetProperty("holders", out var holders)
                && holders.ValueKind is JsonValueKind.Array
                && holders.EnumerateArray().Any(holder =>
                    holder.ValueKind is JsonValueKind.Object
                    && holder.TryGetProperty("departmentId", out _)
                    && !holder.TryGetProperty("holder", out _)))
            {
                return "Actor 導入前の古い lease 形式です（holders に departmentId があり holder がない）。"
                    + "自動変換しません";
            }

            return $"現在の形式として不完全です: {reason}";
        }
        catch (JsonException)
        {
            return $"JSON として壊れています: {reason}";
        }
    }

    /// <summary>
    /// 隔離して空の lease を作る。<b>人間が押したときだけ呼ぶ</b>（設計 §23-1）。
    /// </summary>
    /// <remarks>
    /// <b>押す直前にもう一度読む。</b> 読めるようになっていたら何もしない ——
    /// 人間が手で直した直後かもしれないし、押した時点の事実で動くのがこの設計の姿勢（§14-2）。
    /// <para>
    /// <b>上書きも削除もしない。</b> 元のファイルは <c>.company/unreadable/</c> に残す。
    /// </para>
    /// </remarks>
    /// <returns>退避先。隔離しなかったら null。</returns>
    public static async Task<string?> IsolateAsync(
        CompanyPaths paths, LeaseStore leases, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(leases);

        if (await leases.ReadAsync(ct) is not LeaseReadResult.Unreadable)
        {
            return null;
        }

        if (!File.Exists(paths.Lease))
        {
            return null;
        }

        Directory.CreateDirectory(paths.UnreadableRoot);
        var destination = Path.Combine(
            paths.UnreadableRoot, $"lease-{now.ToUniversalTime():yyyyMMdd-HHmmss}.json");

        try
        {
            File.Move(paths.Lease, destination);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // 空の lease は「誰も持っていない」。ファイルが無いのと同じ意味なので、
        // ここで書かずに済ませてもよいが、**隔離したことを目に見える形で残す**ために書く。
        await leases.WriteEmptyAsync(ct);
        return destination;
    }
}
