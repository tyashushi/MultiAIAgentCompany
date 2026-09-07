namespace MultiAIAgentCompany.Core.Coordination;

/// <summary>
/// 部門へ渡す指示書を組み立てる。
/// </summary>
/// <remarks>
/// <b>人間の文章だけでは足りない。</b> 部門は、報告をどこにどう書くかを知らない。
/// 実機で通して初めて分かった（2026-09-06）—— turn は成功して終わるのに
/// <c>report.md</c> が現れず、仕事状態が <c>Dispatched</c> のまま止まった。
/// <para>
/// §16-1 は「publish 契約は部門に守らせるものなので、<b>指示書に書く</b>」と決めている。
/// ここがその実装。
/// </para>
/// </remarks>
public static class CompanyInstruction
{
    /// <summary>
    /// 設計レビューの依頼文（設計 §29-2）。
    /// </summary>
    /// <remarks>
    /// <b>2人に同じ問いを投げない。</b> 同じ文書を読ませても、問いが違えば別のものが出る ——
    /// 同じ問いなら、費用は2倍で発見はほとんど増えない（2026-09-07 の実測）。
    /// </remarks>
    /// <param name="documentPath">読む設計文書。</param>
    /// <param name="lens">どちらの目で見るか。</param>
    /// <param name="paths">ワークスペースの調整基盤。</param>
    /// <param name="slug">この仕事。</param>
    /// <returns>
    /// <b>そのまま <c>instruction.md</c> に書ける形。</b> publish 契約（§16-1）まで含む ——
    /// 「本文だけ返して、呼び出し側が <see cref="Compose"/> に通す」形にしていたら、
    /// **通し忘れると報告の書き方を知らないまま終わる罠**になる（レビューで指摘）。
    /// </returns>
    public static string ComposeDesignReview(
        string documentPath, DesignReviewLens lens, CompanyPaths paths, string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
        ArgumentNullException.ThrowIfNull(paths);

        var focus = lens switch
        {
            DesignReviewLens.Consistency =>
                "## 見てほしいこと（整合）\n\n"
                + "**この文書の中で矛盾しているところ**を探してください。\n\n"
                + "- ある節で決めたことが、別の節の前提を壊していないか\n"
                + "- 「〜しない」と決めた規則を、別の場所で破っていないか\n"
                + "- 決めたはずなのに、それを根拠にしている箇所が古いままになっていないか\n"
                + "- 用語が場所によって違う意味で使われていないか\n\n"
                + "**文書の外の話（実装がどうなっているか）は見なくてよい。**",

            DesignReviewLens.Outside =>
                "## 見てほしいこと（外から）\n\n"
                + "**この設計で作られたものを使う人が、何に困るか**を挙げてください。\n\n"
                + "- 設計文書に書かれていない観点を探す。**書いてあることの確認ではない**\n"
                + "- 初めて触る人が、何をすればよいか分かるか\n"
                + "- うまくいかなかったとき、次に何をすればよいか分かるか\n"
                + "- この種のアプリなら当然あるはずなのに、触れられていないもの\n\n"
                + "**「文書の中で筋が通っているか」は見なくてよい。** そちらは別の部門が見る。",

            _ => throw new ArgumentOutOfRangeException(nameof(lens), lens, null),
        };

        return Compose(
            $"{documentPath} を読んでレビューしてください。\n\n"
            + $"{focus}\n\n"
            + "## 書き方\n\n"
            + "- 重要な順に、**最大10件**\n"
            + "- 各項目に「どこ（節や見出し）」「なぜ問題か」「どう直すか」\n"
            + "- **推測で危険を膨らませない。** 反例を書けないものは「懸念」と明記する\n"
            + "- 日本語で書く\n\n"
            + "## やらないこと\n\n"

            // **報告まで禁じない**（レビューで発覚）。「ファイルを書き換えない」とだけ書くと、
            // 忠実な CLI は report.md も書かず、**出力だけして正常終了する** ——
            // 仕事は Dispatched のまま永久に止まる。禁じるのは作業ツリーの書き換え。
            + "- **作業ツリーのファイルを書き換えない。** この部門は読むだけ\n"
            + "- ただし**報告と質問は書く。** 書き方は下の約束に従う",
            paths, slug);
    }

    /// <summary>
    /// 人間の指示に、調整基盤の約束を添える。
    /// </summary>
    /// <param name="humanText">人間（または秘書）が書いた指示。</param>
    /// <param name="paths">ワークスペースの調整基盤。</param>
    /// <param name="slug">この仕事。</param>
    public static string Compose(string humanText, CompanyPaths paths, string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(humanText);
        ArgumentNullException.ThrowIfNull(paths);

        var directory = paths.TaskDirectory(slug);
        var report = paths.Report(slug);
        var question = paths.Question(slug);
        var answer = paths.Answer(slug);

        return $"""
            {humanText.TrimEnd()}

            ---

            ## この仕事の約束

            上の依頼をこなしたら、**必ず報告を書くこと**。書かないと、こちらからは
            「終わったのか、まだ動いているのか」が分からない。

            ### 報告の書き方（この手順を守ること）

            1. `{report}.tmp.<任意のID>` に報告を書く
            2. ファイルを閉じる
            3. **同じディレクトリの中で** `{report}` へ rename する

            **途中まで書いたファイルを最終名で置かないこと。** こちらは最終名の出現を
            「書き終わった」の合図として読むので、書きかけを置かれると途中を読んでしまう。

            ### 判断に迷ったら、自分で埋めずに止まる

            依頼に書かれていない判断が必要になったら、**勝手に決めない**。
            同じ手順（tmp に書いて rename）で `{question}` に質問を書き、そこで止まること。
            人間の回答は `{answer}` に置かれる。置かれたら続きをやってよい。

            ### やらないこと

            - **git の操作**（commit / merge / push）。これは人間の仕事
            - このワークスペースの外への書き込み
            - `{directory}` 以外の調整文書を書き換えること

            ### 秘密情報

            報告にも質問にも、**鍵・トークン・パスワードの値を書かないこと**。
            これらのファイルは残り、人間がエディタで開く。
            """;
    }
}

/// <summary>設計レビューの目（設計 §29-2）。<b>2人に同じ問いを投げないためにある。</b></summary>
public enum DesignReviewLens
{
    /// <summary>文書の中で矛盾していないか。</summary>
    Consistency,

    /// <summary>その設計で作られたものを使う人が、何に困るか。</summary>
    Outside,
}
