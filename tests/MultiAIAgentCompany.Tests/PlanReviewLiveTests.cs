using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Agents.ClaudeCode;
using MultiAIAgentCompany.Core.Agents.CodexCli;
using MultiAIAgentCompany.Core.Coordination;
using MultiAIAgentCompany.Core.Sessions;
using MultiAIAgentCompany.Core.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace MultiAIAgentCompany.Tests;

/// <summary>
/// <b>差し戻しの往復を、実物の CLI で一巡させる</b>（設計 §37-5 / §40）。
/// </summary>
/// <remarks>
/// §39 で測ったのは<b>判定行の形だけ</b>。計画としては
/// <c>revise</c> → 差し戻し → 2周目 —— を一度も動かしていない。
/// <para>
/// <b>画面は通さない。</b> 通すのは <see cref="PlanRunner"/> と
/// <see cref="CompanyScanner"/> で、本番（<c>ShellComposer</c> / <c>MainWindow</c>）と
/// 同じ順序・同じ配線で回す。<b>違うのは駆動モードだけ</b> ——
/// 本番の既定は外部ターミナル（人間が窓で押す）で、ここは構造化（承認をテストが通す）。
/// </para>
/// <para>
/// <b>終わり方は縛らない。</b> レビューが何回 <c>revise</c> と言うかは相手次第で
/// （§39-2）、上限に達して人間を呼ぶのも<b>設計どおりの終わり方</b>である。
/// 見るのは「**差し戻しが実際に起きて、2周目が走り、止まるべきところで止まった**」こと。
/// </para>
/// </remarks>
[Collection(LiveCollection.Name)]
public sealed class PlanReviewLiveTests(ITestOutputHelper output)
{
    /// <summary>1周ごとの待ち。CLI の turn はだいたい 20〜60 秒かかる。</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    /// <summary>一巡の上限。<b>終わらないことを失敗として出す</b>（黙って待ち続けない）。</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 工程の一言（<c>Handover</c>）に<b>判断の権限を書かない</b>場合（設計 §40-2）。
    /// </summary>
    /// <remarks>
    /// 実測（2026-09-12）では、差し戻された設計工程が<b>やり直さずに質問した</b> ——
    /// レビューの指摘が「決めてくれ」の形だったので、protocol どおりに止まった。
    /// **終わり方は縛らない**（相手次第）が、<b>差し戻しが起きること</b>と
    /// <b>止まるべきところで止まること</b>は縛る。
    /// </remarks>
    [LivePlanFact]
    public async Task 差し戻しが起きて_止まるべきところで止まる() =>
        await RunAsync(
            "`config.json` の読み込みについて、10行程度の設計を `report.md` に書いてください。"
            + "コードは書かないこと。");

    /// <summary>
    /// 一言に<b>判断の権限を書いた</b>場合（設計 §40-2）。
    /// </summary>
    /// <remarks>
    /// <b>protocol を曲げているのではない。</b> 「指示に書かれていないことは決めない」
    /// （§16 / §17-6）の<b>「書かれていない」の方を、計画が埋めている</b> ——
    /// 権限を渡すのは、指示を書く側の仕事である。
    /// </remarks>
    [LivePlanFact]
    public async Task 判断の権限を渡すと_2周目まで走る() =>
        await RunAsync(
            "`config.json` の読み込みについて、10行程度の設計を `report.md` に書いてください。"
            + "コードは書かないこと。"
            + "**この工程の範囲で判断が要る点は、最も妥当な案を選んで、選んだ理由を報告に書くこと。**"
            + "質問はせず、報告まで書き切ってください。");

    private async Task RunAsync(string handover)
    {
        // Codex はリポジトリの外に書き込めないので、scratch はリポジトリの中に置く。
        var scratch = Path.Combine(FindRepositoryRoot(), ".live-scratch", Guid.NewGuid().ToString("N"));
        var root = Path.Combine(scratch, "ws");
        Directory.CreateDirectory(root);

        var workspace = new WorkspaceRef(root);
        var paths = workspace.Company;
        var clock = TimeProvider.System;

        // 本番（`ShellComposer`）と同じ配線。
        var tasks = new TaskStore(paths, clock);
        var leases = new LeaseStore(paths, clock);
        var dispatcher = new TaskDispatcher(paths, tasks, leases, clock);
        var scanner = new CompanyScanner(paths, tasks, leases, clock);
        var plans = new PlanStore(paths, clock);
        var runner = new PlanRunner(paths, plans, tasks, dispatcher, clock);

        // **設計は書き手、レビューは読むだけ**（§29-1）。
        // ここが §37-4b で互いに待って止まったところなので、本番と同じ形で回す。
        var departments = new Dictionary<string, DepartmentDefinition>(StringComparer.Ordinal)
        {
            ["design"] = new("design", "設計", "要件と設計判断を整理する。",
                AgentKind.ClaudeCode, DriveMode.Structured, ReportDeadlineMinutes: 30),
            ["design-review"] = new("design-review", "設計レビュー（整合）",
                "設計文書が、他の節と矛盾していないかを見る。",
                AgentKind.CodexCli, DriveMode.Structured, ReadsOnly: true, ReportDeadlineMinutes: 30),
        };

        var sessions = new Dictionary<string, IStructuredSession>(StringComparer.Ordinal);

        try
        {
            await DepartmentReadme.WriteAsync(paths, CancellationToken.None);

            foreach (var (id, department) in departments)
            {
                IAgentAdapter adapter = department.Agent is AgentKind.ClaudeCode
                    ? new ClaudeCodeAdapter()
                    : new CodexCliAdapter();

                var session = (IStructuredSession)await adapter.StartAsync(
                    workspace, department.DisplayName, DriveMode.Structured, CancellationToken.None);

                // 本番の窓では人間が押す。ここは**人間の代わりに通す**。
                session.ApprovalRequested += (_, request) => _ = ApproveAsync(session, request);
                session.TurnFinished += (_, verdict) =>
                    output.WriteLine($"[{id}] turn: succeeded={verdict.Succeeded} {verdict.Reason}");

                sessions[id] = session;
            }

            var hands = new PlanHands(
                id => departments.TryGetValue(id, out var department) ? department : null,
                id => sessions.TryGetValue(id, out var session) ? session : null,

                // 構造化なので窓は開かない。本番の `LaunchIfTerminalAsync` にあたる場所。
                (result, _, _) => Task.FromResult(result));

            const string planId = "plan-live-review";
            var created = await plans.CreateAsync(
                planId,
                "設定ファイルの読み込みを決める",
                [
                    new PlanStep("design", handover),
                    new PlanStep("design-review", "1番目の工程の設計をレビューしてください。", ReviewsStep: 0),
                ],
                CancellationToken.None);
            Assert.IsType<PlanWriteResult.Written>(created);

            var deadline = DateTimeOffset.UtcNow + Budget;
            PlanTick tick = new PlanTick.Idle("まだ始めていない");
            Plan plan = ((PlanWriteResult.Written)created).Plan;
            var sawSendBack = false;

            while (DateTimeOffset.UtcNow < deadline)
            {
                // 本番と同じ順序 —— **走査してから計画を進める**。
                // 逆にすると、報告が出ていても `Dispatched` のまま見えて1周むだになる。
                await scanner.SyncAsync(CompanyScanKind.Periodic, CancellationToken.None);

                if (await plans.ReadAsync(planId, CancellationToken.None) is not PlanReadResult.Found found)
                {
                    Assert.Fail("計画を読めなくなった");
                    return;
                }

                plan = found.Plan;
                tick = await runner.StepAsync(plan, hands, CancellationToken.None);

                if (tick is PlanTick.Acted acted)
                {
                    output.WriteLine($"[plan] {acted.Note}");
                    if (acted.Note.Contains("送り直した", StringComparison.Ordinal))
                    {
                        sawSendBack = true;
                    }
                }

                if (tick is PlanTick.Done or PlanTick.Stopped)
                {
                    break;
                }

                await Task.Delay(TickInterval);
            }

            output.WriteLine($"---- 終わり方: {tick}");
            output.WriteLine($"---- 差し戻し {plan.Revisions} 回");
            foreach (var (step, index) in plan.Steps.Select((step, index) => (step, index)))
            {
                output.WriteLine($"     工程{index + 1} {step.DepartmentId}: slug={step.TaskSlug} verdict={step.Verdict}");
            }

            foreach (var slug in await tasks.ListSlugsAsync(CancellationToken.None))
            {
                if (await tasks.ReadAsync(slug, CancellationToken.None) is TaskReadResult.Found state)
                {
                    output.WriteLine($"     {slug}: {state.State.Status} ({state.State.DepartmentId})");
                }
            }

            // **証拠を全部出す。** 出さないと、終わり方の理由を人が推し量ることになる（§7）。
            foreach (var step in plan.Steps)
            {
                if (step.TaskSlug is not { } slug)
                {
                    continue;
                }

                foreach (var (label, path) in new[]
                {
                    ("report.md", paths.Report(slug)),
                    ("question.md", paths.Question(slug)),
                    ("rejection.md", paths.Rejection(slug)),
                })
                {
                    if (File.Exists(path))
                    {
                        output.WriteLine($"---- {slug} / {label} ----");
                        output.WriteLine(await File.ReadAllTextAsync(path));
                    }
                }
            }

            // **終わり方は2つだけ**（§37-6）—— 全部終わったか、止めて人間を呼んだか。
            // **黙って止まっているのは失敗**である。ここは機構の話なので、素直に落とす。
            Assert.True(tick is PlanTick.Done or PlanTick.Stopped, $"時間切れ: {tick}");

            // **往復が起きたこと。** これが起きないなら、測りたかったものを測っていない。
            //
            // **ただし「起きなかった」は、コードが壊れていることを意味しない**
            // （`ClaudeLiveRoundTripTests.WaitForApprovalAsync` と同じ姿勢）——
            // レビューが最初から `ok` と書けば、計画は差し戻さずに正しく終わる。
            // **それでも通したことにはしない** —— 通すと、測れていない回が
            // 「往復を確かめた」として残る（§7）。
            if (!sawSendBack)
            {
                Assert.Fail(
                    $"一度も差し戻しが起きなかった（差し戻し {plan.Revisions} 回、終わり方 {tick}）。"
                    + "**コードの失敗とは限らない** —— レビューが最初から ok を出せば、"
                    + "計画は差し戻さずに正しく終わる（§39-2 / §40-1）。"
                    + "上の記録で工程と判定を確かめ、単独で再実行して切り分けること。");
            }

            Assert.True(plan.Revisions >= 1);
        }
        finally
        {
            foreach (var session in sessions.Values)
            {
                try
                {
                    await session.StopAsync(CancellationToken.None);
                    await session.DisposeAsync();
                }
                catch (Exception exception)
                {
                    output.WriteLine($"後始末で例外: {exception.Message}");
                }
            }

            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, true);
            }
        }
    }

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
            // 応答できなかったことでテストを落とさない —— 待ちの側が先に切れる。
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

/// <summary><c>MAC_LIVE_PLAN=1</c> のときだけ走る。<b>Claude と Codex を何ターンも回す。</b></summary>
internal sealed class LivePlanFactAttribute : FactAttribute
{
    public LivePlanFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MAC_LIVE_PLAN") != "1")
        {
            Skip = "MAC_LIVE_PLAN=1 のときだけ走る（実プロセスを何ターンも回す）";
        }
    }
}
