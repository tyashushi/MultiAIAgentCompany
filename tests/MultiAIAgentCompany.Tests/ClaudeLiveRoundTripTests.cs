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

                var request = await approval.Task.WaitAsync(TimeSpan.FromMinutes(3));

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
