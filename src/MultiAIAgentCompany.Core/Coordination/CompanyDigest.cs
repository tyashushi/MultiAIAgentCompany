using System.Security.Cryptography;

namespace MultiAIAgentCompany.Core.Coordination;

/// <summary>
/// 調整文書の指紋（設計 §20-5）。
/// </summary>
/// <remarks>
/// <b>生バイトに対して取る。正規化しない。</b> 正本はファイルの bytes（§16-1）で、
/// 正規化は「意味が同じ」の推定になる —— Markdown では空白と改行が意味を持つ。
/// 人間が手で直したときの誤検出は**見える誤検出**であり、
/// 意味のある変更を見逃す方がこの設計では危ない。
/// <para>
/// <b>mtime を使わない。</b> 人間の編集・粒度・コピー復元で簡単に嘘になる（§20-5）。
/// </para>
/// </remarks>
public static class CompanyDigest
{
    /// <returns>小文字16進の SHA-256。ファイルが無ければ null。</returns>
    public static async Task<string?> OfFileAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        // **読めなかったことを「無い」にしない。** ここで握りつぶすと、
        // 呼び出し元が「質問は無かった」に倒れる。走査は IOException を
        // 仕事ごとに拾って「読めない仕事」として人間に見せる（§16-3）。
        // **FileShare.Delete を入れる。** publish は最終名への atomic rename なので
        // （§16-1）、読んでいる間に置き換えられる。これが無いと、走査が古い質問を
        // hash している最中に**部門の2度目の publish が失敗し得る** ——
        // 読み手が書き手を止めるのは、この設計では本末転倒。
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    /// <summary>
    /// <b>送った bytes そのもの</b>から取る（設計 §20-2）。
    /// </summary>
    /// <remarks>
    /// 送るために読んだのと別の read で hash を取ると、その間の書き換えで
    /// 「送っていない bytes を送ったことにする」記録ができる。
    /// </remarks>
    public static string OfBytes(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));
}
