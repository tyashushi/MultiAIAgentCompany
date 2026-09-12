using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MultiAIAgentCompany.Core.Status;

namespace MultiAIAgentCompany.Desktop;

/// <summary>
/// ポーズの絵を読む（設計 §15-2）。
/// </summary>
/// <remarks>
/// <b>一度だけ読んで使い回す。</b> タイルは走査のたびに作り直されるので、
/// そのたびにデコードすると、部門の数 × 走査の回数だけ無駄が出る。
/// <para>
/// <b>読めなかったら null を返す。</b> 絵が無いことでアプリを落とさない ——
/// 絵は「人間が読む助け」であって、状態の正本ではない（正本は §7 の観測）。
/// </para>
/// </remarks>
public static class PoseImages
{
    private static readonly Dictionary<DepartmentPose, Bitmap?> Cache = [];
    private static readonly Lock Gate = new();

    public static Bitmap? Of(DepartmentPose pose)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(pose, out var cached))
            {
                return cached;
            }

            var bitmap = Load(FileNameOf(pose));
            Cache[pose] = bitmap;
            return bitmap;
        }
    }

    /// <summary><c>DepartmentPose</c> とファイル名の対応。<b>ここが唯一の対応表</b>。</summary>
    private static string FileNameOf(DepartmentPose pose) => pose switch
    {
        DepartmentPose.Working => "working",
        DepartmentPose.Resting => "resting",
        DepartmentPose.AwaitingApproval => "awaiting-approval",
        DepartmentPose.Consulting => "consulting",
        DepartmentPose.Degraded => "degraded",
        _ => "unknown",
    };

    private static Bitmap? Load(string name)
    {
        try
        {
            using var stream = AssetLoader.Open(
                new Uri($"avares://MultiAIAgentCompany.Desktop/Assets/poses/{name}.png"));
            return new Bitmap(stream);
        }
        catch (Exception exception)
        {
            // 絵が入っていないビルドでも動く。**落とさない**（上の remarks）。
            // **ただし黙らない**（レビューで指摘）—— 綴り違いや取り込み漏れのとき、
            // 「絵が出ない」だけが症状になると、理由に辿り着けない。
            System.Diagnostics.Debug.WriteLine($"[PoseImages] {name}.png を読めなかった: {exception.Message}");
            return null;
        }
    }
}
