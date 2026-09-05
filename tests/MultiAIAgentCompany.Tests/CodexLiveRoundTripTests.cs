using MultiAIAgentCompany.Core.Agents;
using MultiAIAgentCompany.Core.Agents.CodexCli;
using MultiAIAgentCompany.Core.Sessions;
using MultiAIAgentCompany.Core.Status;
using MultiAIAgentCompany.Core.Workspace;
using Xunit;

namespace MultiAIAgentCompany.Tests;

/// <summary>
/// Codex app-server の実プロセス往復。<b>偽チャネルでは確かめられないものだけ</b>。
/// </summary>
/// <remarks>
/// 承認を出させるには <c>workspace-write</c> の書き込み可能ルート
/// <c>[workdir, /tmp, $TMPDIR]</c> の<b>外</b>へ書かせる必要がある（設計 §13-2）。
/// だから作業ディレクトリをリポジトリ配下に作り、その兄弟を書き込み先にする。
/// <c>/tmp</c> を使うと承認なしで通ってしまい、往復を測れない。
/// </remarks>
[Collection(LiveCollection.Name)]
public sealed class CodexLiveRoundTripTests
{
    [LiveCodexTheory]
    [InlineData("accept", true, "completed")]
    [InlineData("cancel", false, "interrupted")]
    [InlineData("acceptWithExecpolicyAmendment", true, "completed")]
    public async Task 実プロセスで承認の往復が閉じる(string decisionId, bool expectFile, string _)
    {
        var scratch = Path.Combine(FindRepositoryRoot(), ".live-scratch", Guid.NewGuid().ToString("N"));
        var ws = Path.Combine(scratch, "ws");
        var outside = Path.Combine(scratch, "outside");
        Directory.CreateDirectory(ws);
        Directory.CreateDirectory(outside);
        var target = Path.Combine(outside, "hello.txt");

        try
        {
            var adapter = new CodexCliAdapter();
            var session = (IStructuredSession)await adapter.StartAsync(
                new WorkspaceRef(ws), "実装", DriveMode.Structured, CancellationToken.None);

            var approval = new TaskCompletionSource<ApprovalRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            var finished = new TaskCompletionSource<OutcomeVerdict>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.ApprovalRequested += (_, request) => approval.TrySetResult(request);
            session.TurnFinished += (_, verdict) => finished.TrySetResult(verdict);

            await using (session)
            {
                await session.SendUserMessageAsync(
                    $"Run exactly this shell command and nothing else: echo HELLO > {target}",
                    CancellationToken.None);

                var request = await approval.Task.WaitAsync(TimeSpan.FromMinutes(3));

                Assert.Equal(AgentKind.CodexCli, request.Agent);
                Assert.Equal(ApprovalKind.Runtime, request.Kind);

                // §7: Codex は threadId / turnId を出すので、根拠を推定なしで埋められる。
                Assert.False(string.IsNullOrWhiteSpace(request.SessionId));
                Assert.False(string.IsNullOrWhiteSpace(request.TurnId));

                // §5: 決定の語彙は版で変わる。提示されたものだけを返す。
                Assert.True(request.Offers(decisionId));
                Assert.False(request.Offers("decline"));

                await session.RespondAsync(
                    request,
                    request.AvailableDecisions.Single(decision => decision.Id == decisionId),
                    decisionId == "cancel" ? "live test: cancelled on purpose" : null,
                    CancellationToken.None);

                var verdict = await finished.Task.WaitAsync(TimeSpan.FromMinutes(3));

                // 「turn が終わった」ではなく「決定どおりに世界が変わった」を見る。
                Assert.Equal(expectFile, File.Exists(target));
                Assert.Equal(expectFile, verdict.Succeeded);

                await session.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, true);
            }
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

/// <summary><c>MAC_LIVE_CODEX=1</c> のときだけ走る。立っていなければスキップとして出る。</summary>
internal sealed class LiveCodexTheoryAttribute : TheoryAttribute
{
    public LiveCodexTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("MAC_LIVE_CODEX") != "1")
        {
            Skip = "MAC_LIVE_CODEX=1 のときだけ走る（実プロセスの往復）";
        }
    }
}
