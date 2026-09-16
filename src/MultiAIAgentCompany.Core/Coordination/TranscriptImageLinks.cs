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

    public static IReadOnlyList<TranscriptImageLink> Find(string text) =>
        LinkPattern().Matches(text)
            .Select(match => new TranscriptImageLink(match.Index, match.Length, match.Value))
            .ToArray();

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
