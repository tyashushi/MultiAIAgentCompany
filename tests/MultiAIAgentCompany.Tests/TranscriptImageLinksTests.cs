using MultiAIAgentCompany.Core.Coordination;
using Xunit;

namespace MultiAIAgentCompany.Tests;

public sealed class TranscriptImageLinksTests
{
    [Theory]
    [InlineData(".company/tasks/banner/images/a.png", ".company/tasks/banner/images/a.png")]
    [InlineData("画像です：.company/tasks/仕事/images/絵.PNG。", ".company/tasks/仕事/images/絵.PNG")]
    [InlineData("「.company/x.jpg」", ".company/x.jpg")]
    [InlineData("（.company/x.jpeg）", ".company/x.jpeg")]
    [InlineData("[画像](.company/x.webp)", ".company/x.webp")]
    [InlineData("`.company/x.gif`", ".company/x.gif")]
    [InlineData("前の行\n.company/x.JpEg\n次の行", ".company/x.JpEg")]
    [InlineData(@"画像：.company\tasks\banner\images\a.PNG。", @".company\tasks\banner\images\a.PNG")]
    [InlineData("画像 .company/x.pngです", ".company/x.png")]
    [InlineData("保存先は.company/x.pngです", ".company/x.png")]
    public void 約物と区切りと拡張子を扱い文字位置を保つ(string text, string expected)
    {
        var link = Assert.Single(TranscriptImageLinks.Find(text));
        Assert.Equal(expected, link.Link);
        Assert.Equal(text.IndexOf(expected, StringComparison.Ordinal), link.Start);
        Assert.Equal(expected, text.Substring(link.Start, link.Length));
    }

    [Theory]
    [InlineData("")]
    [InlineData("foo.company/x.png")]
    [InlineData("123.company/x.png")]
    [InlineData("/tmp/.company/x.png")]
    [InlineData(@"C:\workspace\.company\x.png")]
    [InlineData("../.company/x.png")]
    [InlineData(".company-other/x.png")]
    [InlineData(".company/a b.png")]
    [InlineData(".company/x.svg")]
    [InlineData(".company/x.png.exe")]
    [InlineData(".company/x.pngsuffix")]
    public void 対象でない文字列はリンクにしない(string text) => Assert.Empty(TranscriptImageLinks.Find(text));

    [Fact]
    public void 複数リンクを会話の順で返す()
    {
        const string text = "あなた：画像を作って\n\n報告：.company/a.png。別案「.company/b.WEBP」と `.company/c.gif`";
        var links = TranscriptImageLinks.Find(text);
        Assert.Equal([".company/a.png", ".company/b.WEBP", ".company/c.gif"], links.Select(link => link.Link));
        Assert.All(links, link => Assert.Equal(link.Link, text.Substring(link.Start, link.Length)));
    }

    [Theory]
    [InlineData(".company/tasks/banner/images/a.png")]
    [InlineData(@".company\tasks\banner\images\a.png")]
    [InlineData(@".company/tasks\banner/images/../images/a.png")]
    public void 区切りを揃えて存在しないファイルも解決する(string link)
    {
        var paths = new CompanyPaths(Path.Combine(Path.GetTempPath(), "image links workspace"));
        var result = Assert.IsType<ImageLinkResolution.Resolved>(TranscriptImageLinks.Resolve(paths, link));
        Assert.Equal(Path.Combine(paths.Root, "tasks", "banner", "images", "a.png"), result.FullPath);
    }

    [Theory]
    [InlineData(".company/../outside.png")]
    [InlineData(@".company\..\outside.png")]
    [InlineData(@".company/tasks/..\../../outside.png")]
    [InlineData(".company/../.company-other/a.png")]
    [InlineData(".company/..")]
    [InlineData(".company/tasks/..")]
    [InlineData("/outside.png")]
    [InlineData("other/a.png")]
    public void 正規化後にcompanyの下でなければ拒否する(string link)
    {
        var paths = new CompanyPaths(Path.GetTempPath());
        var result = Assert.IsType<ImageLinkResolution.Rejected>(TranscriptImageLinks.Resolve(paths, link));
        Assert.Equal(".company の外なので開かない", result.Reason);
    }

    [Fact]
    public void 大文字小文字の比較はOSに合わせる()
    {
        var result = TranscriptImageLinks.Resolve(new CompanyPaths(Path.GetTempPath()), ".company/../.COMPANY/a.png");
        Assert.Equal(OperatingSystem.IsWindows(), result is ImageLinkResolution.Resolved);
    }

    [Fact]
    public void 不正なパスでも例外で落とさない() =>
        Assert.IsType<ImageLinkResolution.Rejected>(TranscriptImageLinks.Resolve(new CompanyPaths(Path.GetTempPath()), ".company/\0.png"));
}
