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
