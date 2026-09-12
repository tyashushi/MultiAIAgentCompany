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

    /// <summary>
    /// <b>実機が本当に書いた報告</b>（2026-09-12 に測った、設計 §39）。
    /// </summary>
    /// <remarks>
    /// 上の入力は<b>こちらが手で作ったもの</b>で、「部門がその形で書くか」は映らない（§37-5b）。
    /// ここに置くのは <c>ReviewVerdictLiveTests</c> が実際に受け取った報告の抜粋 ——
    /// <b>飾りの剥がし方を、実際に測った形の方から縛る。</b>
    /// </remarks>
    [Fact]
    public void 実機の報告を読む_Claude_は判定を見出しの下に太字で書いた()
    {
        Assert.Equal(ReviewVerdict.NeedsRevision, ReviewVerdicts.Parse(
            """
            # レビュー報告: 設定ファイルの読み込み

            ## 指摘事項

            1. **「検証しない」と「壊れていたら既定値」が矛盾する抜け穴がある。**

            ## 判定

            いずれも実装のバグではなく、次工程（実装）に入る前に設計側で決めておくべき前提の
            未確定事項だと考えたため、一度立ち止まる判定にしました。

            **verdict: revise**
            """));
    }

    [Fact]
    public void 実機の報告を読む_Codex_は判定を冒頭に素で書いた()
    {
        Assert.Equal(ReviewVerdict.NeedsRevision, ReviewVerdicts.Parse(
            """
            # 設計レビュー: 設定ファイルの読み込み

            verdict: revise

            `config.json` が壊れている場合に既定値でそのまま動かすと、設定ミスに気付けません。
            """));
    }

    [Fact]
    public void 実機の報告を読む_Claude_は通すときも同じ形で書いた()
    {
        Assert.Equal(ReviewVerdict.Ok, ReviewVerdicts.Parse(
            """
            # レビュー結果: 起動時のログの場所

            ## 判定

            verdict: ok

            ## 理由

            - 4項目とも互いに矛盾せず、次の工程が仕様判断で迷う余地は見当たらなかった。
            """));
    }

    /// <summary>
    /// <b>前の判定を引用した報告は「分からない」になる</b>（設計 §39、測っていて気付いた）。
    /// </summary>
    /// <remarks>
    /// 実機の Claude は<b>対象を再掲してから判定を書いた</b>。差し戻しの2周目に、
    /// 前回の <c>verdict: revise</c> を引用したまま <c>verdict: ok</c> と書くと、
    /// **食い違う2行**になって人間が呼ばれる。
    /// <para>
    /// <b>これは直さない。</b> 片方を選ぶのは推測であって観測ではない（§37-5）——
    /// 止まる側に倒すのが、ここの設計である。<b>起こり得ることとして固定しておく。</b>
    /// </para>
    /// </remarks>
    [Fact]
    public void 前回の判定を引用すると_分からないになる()
    {
        Assert.Equal(ReviewVerdict.Unknown, ReviewVerdicts.Parse(
            """
            # レビュー結果（2周目）

            前回の判定を再掲します。

            > verdict: revise

            指摘はすべて直っています。

            verdict: ok
            """));
    }
}
