using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Agents.ClaudeCode;
using MultiAIAgentCompany.Core.Sessions;
using MultiAIAgentCompany.Core.Status;
using MultiAIAgentCompany.Core.Workspace;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>
/// 実プロセスの往復。<b>偽チャネルでは確かめられないものだけ</b>をここに置く。
/// </summary>
/// <remarks>
/// 既定では走らない。`MAC_LIVE_CLAUDE=1` を立てたときだけ走る。
/// 実際の `claude` を起動して課金の対象になるうえ、ネットワークと認証に依存するため、
/// 普段のテスト実行に混ぜない。
/// <para>
/// 判定は「動いた」ではなく<b>「往復が閉じた」</b>（設計 §13）——
/// 承認要求を受信 → 決定を送信 → 同じ turn が再開 → <b>決定どおりに世界が変わった</b>。
/// </para>
/// </remarks>
[Collection(LiveCollection.Name)]
public sealed class ClaudeLiveRoundTripTests
{
    [LiveFact]
    public async Task 実プロセスがモデルを申告する()
    {
        // **設計 §27-1 の根拠を実物で押さえる。** fixture だけだと、CLI が
        // 「いま何を使っているか」を本当に返すのかは確かめたことにならない。
        var root = Path.Combine(Path.GetTempPath(), "mac-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var session = (IStructuredSession)await new ClaudeCodeAdapter().StartAsync(
                new WorkspaceRef(root), "実装", DriveMode.Structured, CancellationToken.None);

            await session.SendUserMessageAsync("何もしないでください。「ok」とだけ答えてください。", CancellationToken.None);

            // init は最初のやりとりで来る。少し待って拾う。
            var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
            while (session.ObservedModel is null && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(500);
            }

            var model = Assert.IsType<AgentModel>(session.ObservedModel);
            Assert.False(string.IsNullOrWhiteSpace(model.Id));

            // Claude は思考の強さを返してこない（§27-1）。**返ってきたら設計が古い。**
            Assert.Null(model.ReasoningEffort);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [LiveTheory]
    [InlineData("allow", true)]
    [InlineData("deny", false)]
    public async Task 実プロセスで承認の往復が閉じる(string decisionId, bool expectFile)
    {
        var root = Path.Combine(Path.GetTempPath(), "mac-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "hello.txt");

        try
        {
            var adapter = new ClaudeCodeAdapter();
            var session = (IStructuredSession)await adapter.StartAsync(
                new WorkspaceRef(root), "実装", DriveMode.Structured, CancellationToken.None);

            var approval = new TaskCompletionSource<ApprovalRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            var finished = new TaskCompletionSource<OutcomeVerdict>(TaskCreationOptions.RunContinuationsAsynchronously);
            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.ApprovalRequested += (_, request) => approval.TrySetResult(request);
            session.TurnFinished += (_, verdict) => finished.TrySetResult(verdict);
            session.Exited += (_, code) => exited.TrySetResult(code);

            await using (session)
            {
                await session.SendUserMessageAsync(
                    $"Run exactly this shell command and nothing else: echo HELLO > {target}",
                    CancellationToken.None);

                var request = await WaitForApprovalAsync(approval.Task);

                // 版が変わっても承認要求の形が読めていること。
                Assert.Equal(AgentKind.ClaudeCode, request.Agent);
                Assert.Equal(ApprovalKind.Runtime, request.Kind);
                Assert.True(request.Offers(decisionId));

                await session.RespondAsync(
                    request,
                    request.AvailableDecisions.Single(decision => decision.Id == decisionId),
                    decisionId == "deny" ? "live test: denied on purpose" : null,
                    CancellationToken.None);

                var verdict = await finished.Task.WaitAsync(TimeSpan.FromMinutes(3));

                // 「turn が終わった」ではなく「決定どおりに世界が変わった」を見る。
                Assert.Equal(expectFile, File.Exists(target));
                Assert.Equal(expectFile, verdict.Succeeded);

                // Init から版が取れていること（検出器の版切り替えの入口）。
                Assert.False(string.IsNullOrWhiteSpace(adapter.DetectedVersion));

                await session.StopAsync(CancellationToken.None);
            }

            // 段階的終了で子が実際に終わり、exit code が握りつぶされていないこと。
            var exitCode = await exited.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.InRange(exitCode, 0, 255);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
    /// <summary>
    /// 承認要求を待つ。<b>来なかったことを「コードが壊れている」と読ませない。</b>
    /// </summary>
    /// <remarks>
    /// エージェントが毎回同じ行動を取るとは限らない —— 依頼どおりにコマンドを実行しようと
    /// しなければ、承認要求はそもそも発生しない。実測（2026-09-06）で、単独なら29秒で通る
    /// ケースが、連続実行の中で3分待っても来ないことがあった。
    /// <para>
    /// §13-5b の「タイムアウトを『Unity が落ちた』と解釈しない」と同じ話で、
    /// <b>混雑や気まぐれと、壊れていることは別</b>。
    /// </para>
    /// </remarks>
    private static async Task<ApprovalRequest> WaitForApprovalAsync(Task<ApprovalRequest> approval)
    {
        try
        {
            return await approval.WaitAsync(TimeSpan.FromMinutes(3));
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                "3分待っても承認要求が来なかった。コードの失敗とは限らない —— "
                + "エージェントが依頼どおりにコマンドを実行しようとしなければ、承認要求は発生しない。"
                + "単独で再実行して切り分けること。", exception);
        }
    }
}

/// <summary>
/// <c>MAC_LIVE_CLAUDE=1</c> のときだけ走る Theory。立っていなければ<b>スキップとして出る</b>。
/// </summary>
/// <remarks>
/// 黙って通る（＝走っていないのに緑に見える）形にしない。xunit 2.x には実行時スキップが
/// 無いので、属性の <c>Skip</c> を環境変数から決める。
/// </remarks>
internal sealed class LiveTheoryAttribute : TheoryAttribute
{
    public LiveTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("MAC_LIVE_CLAUDE") != "1")
        {
            Skip = "MAC_LIVE_CLAUDE=1 のときだけ走る（実プロセスの往復）";
        }
    }

}

/// <summary>
/// <c>MAC_LIVE_CLAUDE=1</c> のときだけ走る Fact。<see cref="LiveTheoryAttribute"/> と同じ理由。
/// </summary>
internal sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MAC_LIVE_CLAUDE") != "1")
        {
            Skip = "MAC_LIVE_CLAUDE=1 のときだけ走る（実プロセス）";
        }
    }
}
