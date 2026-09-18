using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Agents.ClaudeCode;
using MultiAIAgentCompany.Core.Agents.CodexCli;
using MultiAIAgentCompany.Core.Coordination;
using MultiAIAgentCompany.Core.Sessions;
using MultiAIAgentCompany.Core.Status;
using MultiAIAgentCompany.Core.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace MultiAIAgentCompany.Tests;

/// <summary>
/// レビュー工程の判定行を、<b>実物の CLI が本当に書くか</b>を測る（設計 §37-5）。
/// </summary>
/// <remarks>
/// <b>ここまで、判定行は一度も実機で測っていなかった。</b> 読む側
/// （<see cref="ReviewVerdicts"/>）のテストは入力を手で置いているので、
/// 「部門がその形で書くか」は**絶対に映らない** —— §37-5b で踏んだのと同じ形である。
/// <para>
/// 測るのは<b>値ではなく形</b>。<c>ok</c> か <c>revise</c> かはレビューの中身次第なので、
/// 見るのは「<see cref="ReviewVerdict.Unknown"/> にならないこと」だけ。
/// **ここを値で縛ると、モデルの気分でテストが落ちる。**
/// </para>
/// <para>
/// <b>Antigravity の設計レビュー部門は、ここでは測れない</b>（§29）——
/// headless の <c>agy</c> はツール権限を人間に聞けず全部自動拒否するので、
/// <c>report.md</c> を書くことすらできない。実機の既定は
/// <see cref="DriveMode.ExternalTerminal"/> で、そこでは人間が窓で押す。
/// </para>
/// </remarks>
[Collection(LiveCollection.Name)]
public sealed class ReviewVerdictLiveTests(ITestOutputHelper output)
{
    /// <summary>直しどころを埋めた成果物。</summary>
    private const string NeedsWork =
        """
        # 設計: 設定ファイルの読み込み

        - 起動時に `config.json` を読む。無ければ既定値で動く
        - 読めたが壊れていたときは、**既定値で動く**
        - 値の検証はしない（書いた人を信じる）
        """;

    /// <summary>
    /// 直しどころの無い成果物。<b>これも測る</b> ——
    /// レビューが <c>ok</c> を出せないなら、**計画は毎回差し戻しで止まる。**
    /// </summary>
    private const string LooksFine =
        """
        # 設計: 起動時のログの場所

        - ログは `<ワークスペース>/.company/log/app.log` に追記する
        - 置き場所が作れなかったら、**起動を失敗させる**（黙って別の場所へ書かない）
        - 1ファイルが 10MB を超えたら `app.log.1` へ退避し、退避は1世代だけ残す
        - ログに鍵・トークン・パスワードの値を書かない
        """;

    [LiveFact]
    public async Task Claude_が判定行を書く_直しどころがあるとき() =>
        await MeasureAsync(new ClaudeCodeAdapter(), "レビュー", NeedsWork);

    [LiveFact]
    public async Task Claude_が判定行を書く_直しどころが無いとき() =>
        await MeasureAsync(new ClaudeCodeAdapter(), "レビュー", LooksFine);

    [LiveCodexFact]
    public async Task Codex_が判定行を書く_直しどころがあるとき() =>
        await MeasureAsync(new CodexCliAdapter(), "設計レビュー（整合）", NeedsWork);

    [LiveCodexFact]
    public async Task Codex_が判定行を書く_直しどころが無いとき() =>
        await MeasureAsync(new CodexCliAdapter(), "設計レビュー（整合）", LooksFine);

    /// <summary>
    /// 実物の部門に、計画のレビュー工程とまったく同じものを渡して、報告を読む。
    /// </summary>
    /// <remarks>
    /// <b>文面を作り直さない。</b> protocol も指示書も判定の頼み方も、
    /// アプリが本番で使う関数からそのまま取る —— 手で写すと、
    /// **測ったのは本番と別の文面**になる（§7）。
    /// </remarks>
    private async Task MeasureAsync(IAgentAdapter adapter, string departmentName, string reviewed)
    {
        // Codex はリポジトリの外に書き込めないので、scratch はリポジトリの中に置く。
        var scratch = Path.Combine(FindRepositoryRoot(), ".live-scratch", Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(scratch, "ws");
        Directory.CreateDirectory(workspace);

        var paths = new CompanyPaths(workspace);
        const string slug = "task-review-live";
        Directory.CreateDirectory(paths.TaskDirectory(slug));

        try
        {
            await DepartmentReadme.WriteAsync(paths, CancellationToken.None);
            await File.WriteAllTextAsync(
                paths.Instruction(slug),
                CompanyInstruction.Compose(ReviewStepText(reviewed), paths, slug,
                    DepartmentStore.CreateDefaultDepartments().Single(d => d.DisplayName == departmentName)),
                CancellationToken.None);

            var session = (IStructuredSession)await adapter.StartAsync(
                new WorkspaceRef(workspace), departmentName, DriveMode.Structured, CancellationToken.None);

            var finished = new TaskCompletionSource<OutcomeVerdict>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.TurnFinished += (_, verdict) => finished.TrySetResult(verdict);

            // 本番の窓では人間が押す。ここは**人間の代わりに通す** ——
            // 測りたいのは承認の往復ではなく、報告の書き方である。
            session.ApprovalRequested += (_, request) => _ = ApproveAsync(session, request);

            await using (session)
            {
                await session.SendUserMessageAsync(
                    DepartmentReadme.LaunchPrompt(paths, slug), CancellationToken.None);

                var verdict = await finished.Task.WaitAsync(TimeSpan.FromMinutes(10));
                output.WriteLine($"turn: succeeded={verdict.Succeeded} reason={verdict.Reason}");

                await session.StopAsync(CancellationToken.None);
            }

            Assert.True(File.Exists(paths.Report(slug)), "report.md が書かれなかった");

            var report = await File.ReadAllTextAsync(paths.Report(slug), CancellationToken.None);
            output.WriteLine("---- report.md ----");
            output.WriteLine(report);

            var parsed = ReviewVerdicts.Parse(report);
            output.WriteLine($"---- ReviewVerdicts.Parse => {parsed}");

            // **値では縛らない。** 見るのは「読める形で書いたか」だけ。
            Assert.NotEqual(ReviewVerdict.Unknown, parsed);
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, true);
            }
        }
    }

    /// <summary>
    /// <c>PlanRunner</c> がレビュー工程へ渡す本文と同じ形（§37-7）。
    /// </summary>
    private static string ReviewStepText(string reviewed) =>
        $"""
        この仕事は、計画「設定の読み込みを決める」の 2 番目の工程です。

        1 番目の工程の設計をレビューしてください。

        ## design の報告（そのまま）

        {reviewed}

        {ReviewVerdicts.RequestText}
        """;

    /// <summary>
    /// 提示された中から通る決定を選ぶ。<b>語彙をハードコードしない</b>（設計 §5）——
    /// 提示されていないものは送れないので、<b>提示された順</b>に見て最初の「通す」を選ぶ。
    /// </summary>
    private static async Task ApproveAsync(IStructuredSession session, ApprovalRequest request)
    {
        string[] preferred = ["allow", "accept", "acceptForSession", "acceptWithExecpolicyAmendment", "allow_always"];
        var decision = request.AvailableDecisions.FirstOrDefault(
            d => preferred.Contains(d.Id, StringComparer.Ordinal));

        if (decision is null)
        {
            return;
        }

        try
        {
            await session.RespondAsync(request, decision, null, CancellationToken.None);
        }
        catch (Exception)
        {
            // 応答できなかったことでテストを落とさない —— turn の待ちが先に切れる。
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("リポジトリルートが見つからない");
    }
}

/// <summary><c>MAC_LIVE_CODEX=1</c> のときだけ走る。</summary>
internal sealed class LiveCodexFactAttribute : FactAttribute
{
    public LiveCodexFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MAC_LIVE_CODEX") != "1")
        {
            Skip = "MAC_LIVE_CODEX=1 のときだけ走る（実プロセス）";
        }
    }
}
