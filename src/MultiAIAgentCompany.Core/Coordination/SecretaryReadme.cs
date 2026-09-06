namespace MultiAIAgentCompany.Core.Coordination;

/// <summary>
/// 秘書の protocol の正本を書く（設計 §17-6）。
/// </summary>
/// <remarks>
/// <b>protocol を会話に埋めない。</b> 埋めると正本が会話かコード中の文字列に寄り、
/// §17-3 の「会話は正本ではない」と相性が悪くなる。
/// 起動直後に送るのは「これを読んで」だけ。
/// </remarks>
public static class SecretaryReadme
{
    /// <summary>起動直後に送る1通目。<b>protocol 本体は送らない。</b></summary>
    public static string StartupMessage(CompanyPaths paths) =>
        $"""
        {paths.SecretaryReadme} を読んで、その protocol に従ってください。
        会話履歴は正本ではありません。仕事の提案は outbox に publish してください。
        """;

    /// <summary>protocol を書き出す。既にあれば上書きする（形式が変わることがあるため）。</summary>
    public static async Task WriteAsync(
        CompanyPaths paths, IReadOnlyList<string> departmentLines, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(departmentLines);

        Directory.CreateDirectory(paths.SecretaryRoot);
        Directory.CreateDirectory(paths.SecretaryOutbox);

        var content = $"""
            # 秘書の protocol

            あなたは**秘書**です。人間が画面の中央で会話する唯一の相手です。
            部門に仕事を割り振るのがあなたの役目ですが、**部門に直接話しかけません。**
            やり取りはすべてファイルを通します。

            ## 部門

            {string.Join("\n", departmentLines)}

            ## 仕事を提案する

            人間との会話から「これは部門にやってもらう仕事だ」と判断したら、
            **`{paths.SecretaryOutbox}` に提案を publish してください。**

            ### 書き方（この手順を守ること）

            1. `{paths.SecretaryOutbox}/<好きなID>.md.tmp.<任意>` に書く
            2. ファイルを閉じる
            3. **同じディレクトリの中で** `<好きなID>.md` へ rename する

            **途中まで書いたファイルを最終名で置かないこと。** こちらは最終名の出現を
            「書き終わった」の合図として読みます。

            ### 中身の形

            ```
            department: <上の一覧の部門 ID>

            <指示の本文。部門が読んで作業できるように書く>
            ```

            ## あなたがやらないこと

            - **`{paths.TasksRoot}` の下に何も作らない。** 仕事にするのはアプリの役目です
            - **`state.json` を書かない**
            - **部門に直接話しかけない**
            - 提案を publish したあと、それが仕事になるかは**人間が決めます。**
              押されるまで待ってください

            ## 覚えておくこと

            - **会話は残りません。** アプリを閉じると消えます。
              残したいことは提案として publish してください
            - 提案は人間が「仕事にする」を押すまで `outbox/` に残ります
            """;

        await File.WriteAllTextAsync(paths.SecretaryReadme, content, ct);
    }
}
