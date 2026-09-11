using MultiAIAgentCompany.Core.Coordination;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>設計 §37-5。<b>「たぶん OK」を作らない</b>ことを固定する。</summary>
public sealed class ReviewVerdictsTests
{
    [Theory]
    [InlineData("verdict: ok")]
    [InlineData("Verdict: OK")]
    [InlineData("  verdict:ok  ")]
    [InlineData("**verdict: ok**")]
    [InlineData("- verdict: `ok`")]
    [InlineData("# verdict：ok")]
    [InlineData("verdict: ok.")]
    public void 通す判定を読む(string line)
    {
        Assert.Equal(ReviewVerdict.Ok, ReviewVerdicts.Parse($"# レビュー結果\n\n{line}\n\n本文"));
    }

    [Fact]
    public void 直しを求める判定を読む()
    {
        Assert.Equal(ReviewVerdict.NeedsRevision, ReviewVerdicts.Parse("verdict: revise\n理由: 3件"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# レビュー結果\n\nおおむね良いと思います。")]
    public void 判定行が無ければ分からない(string? report)
    {
        Assert.Equal(ReviewVerdict.Unknown, ReviewVerdicts.Parse(report));
    }

    [Theory]
    [InlineData("verdict: ok|revise")]
    [InlineData("verdict: たぶん大丈夫")]
    [InlineData("verdict:")]
    public void 知らない値は分からない(string line)
    {
        // **protocol の説明文を引用しただけの行**もここに落ちる（`ok|revise`）。
        Assert.Equal(ReviewVerdict.Unknown, ReviewVerdicts.Parse(line));
    }

    [Fact]
    public void 食い違う判定が2つあったら_どちらも採らない()
    {
        Assert.Equal(ReviewVerdict.Unknown, ReviewVerdicts.Parse("verdict: ok\n...\nverdict: revise"));
    }

    [Fact]
    public void 同じ判定が2度書いてあるのは通す()
    {
        Assert.Equal(ReviewVerdict.Ok, ReviewVerdicts.Parse("verdict: ok\n...\nverdict: ok"));
    }

    [Fact]
    public void 判定という語で始まるだけの行は判定行ではない()
    {
        Assert.Equal(ReviewVerdict.Unknown, ReviewVerdicts.Parse("verdict は report.md に書きます"));
    }
}
