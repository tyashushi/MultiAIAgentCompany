using System.Text.RegularExpressions;
using MultiAIAgentCompany.Core.Agents;

namespace MultiAIAgentCompany.Core.Coordination;

/// <summary>会話に書かれた画像の場所。文字位置を保つので、選択・コピーは元の文のまま（設計 §56）。</summary>
public sealed record TranscriptImageLink(int Start, int Length, string Link);

public abstract record ImageLinkResolution
{
    public sealed record Resolved(string FullPath) : ImageLinkResolution;
    public sealed record Rejected(string Reason) : ImageLinkResolution;
}

/// <summary>画像リンクの検出と、.company の内側への解決（設計 §56-4）。ファイルは読まない。</summary>
public static partial class TranscriptImageLinks
{
    // **パスの途中を拾わない。** 日本語の約物や Markdown の括弧は境界にできる。
    // 直前で弾くのは ASCII の英数字だけ —— 日本語の文字まで弾くと「保存先は.company/…」が拾えない。
    // 拡張子の後ろにパスの文字が続くもの（x.png.exe 等）は画像リンクにしない。
    [GeneratedRegex(@"(?<![A-Za-z0-9_./\\:\-])\.company[/\\][^\s]+?\.(?:png|jpg|jpeg|webp|gif)(?![a-z0-9_./\\\-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkPattern();

    // **添付は拡張子を問わない**（設計 §58-5）。名前に空白は入らない（§58-2 で `_` にしている）。
    // 閉じ括弧・引用符は名前に含めず、末尾の句読点も含めない —— 文中に書かれても拾えるように。
    // `<フォルダ>/<名前>` の1段だけ。深いパスの途中で切ってリンクにしない（先読みで弾く）。
    [GeneratedRegex(@"(?<![A-Za-z0-9_./\\:\-])\.company[/\\]attachments[/\\][^\s/\\]+[/\\](?![^\s/\\。、」』）)\]`'""]*[/\\])[^\s/\\。、」』）)\]`'""]*[^\s/\\。、」』）)\]`'"",.:;!?]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AttachmentPattern();

    [GeneratedRegex(@"\.(?:png|jpg|jpeg|webp|gif)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImageExtension();

    /// <summary>画像として出すか。それ以外は文字か「プレビューできない」（§58-5）。</summary>
    public static bool IsImage(string link) => ImageExtension().IsMatch(link);

    public static IReadOnlyList<TranscriptImageLink> Find(string text)
    {
        // 添付の画像は両方に当たる。**先に見つかった範囲と重なるものは捨てる**（同じ文字を2度リンクにしない）。
        var links = new List<TranscriptImageLink>();
        foreach (var match in AttachmentPattern().Matches(text).Concat(LinkPattern().Matches(text)))
        {
            if (links.Any(link => match.Index < link.Start + link.Length && link.Start < match.Index + match.Length)) continue;
            links.Add(new TranscriptImageLink(match.Index, match.Length, match.Value));
        }
        return links.OrderBy(link => link.Start).ToArray();
    }

    public static ImageLinkResolution Resolve(CompanyPaths paths, string link)
    {
        try
        {
            var relative = link.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            if (!relative.StartsWith(".company" + Path.DirectorySeparatorChar, SecretaryApprovalPolicy.PathComparison))
                return new ImageLinkResolution.Rejected(".company の外なので開かない");

            var fullPath = Path.GetFullPath(Path.Combine(paths.WorkspaceRoot, relative));
            // **正規化してから、区切りまで比べる。** .company-other も .. による脱出も通さない（§56）。
            if (!fullPath.StartsWith(paths.Root + Path.DirectorySeparatorChar, SecretaryApprovalPolicy.PathComparison))
                return new ImageLinkResolution.Rejected(".company の外なので開かない");

            return new ImageLinkResolution.Resolved(fullPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new ImageLinkResolution.Rejected("パスとして読めない");
        }
    }
}
