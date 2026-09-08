namespace MultiAIAgentCompany.Core.Terminal;

/// <summary>
/// いまの OS に合う <see cref="ITerminalLauncher"/> を選ぶ（設計 §32-7）。
/// </summary>
/// <remarks>
/// <b>macOS 以外はまだ無い。</b> ブリーフが Avalonia を選んだ理由は Win/Mac/Linux なので、
/// これは<b>その選択に対する借金</b>である —— 借金は黙って抱えず、
/// **押したときに理由が出る形**にしておく（§28-1 の「押してから失敗するまで分からない、を無くす」）。
/// </remarks>
public static class TerminalLaunchers
{
    public static ITerminalLauncher ForCurrentOs() =>
        OperatingSystem.IsMacOS() ? new MacTerminalLauncher() : new UnsupportedTerminalLauncher();
}

/// <summary>
/// この OS では外部ターミナルを開けない（設計 §32-7）。
/// </summary>
/// <remarks>
/// <b>黙って何もしない実装にしない。</b> それをやると
/// 「押しても何も起きない」になり、原因が画面に出ない。
/// </remarks>
public sealed class UnsupportedTerminalLauncher : ITerminalLauncher
{
    private static string Reason =>
        $"この OS（{Environment.OSVersion.Platform}）向けのターミナル起動はまだ実装されていません（設計 §32-7）";

    public Task<TerminalLaunchResult> LaunchAsync(TerminalLaunchRequest request, CancellationToken ct) =>
        Task.FromResult<TerminalLaunchResult>(new TerminalLaunchResult.Failed(Reason));

    public Task<bool> FocusAsync(TerminalHandle handle, CancellationToken ct) => Task.FromResult(false);

    public Task<TerminalTerminateResult> TerminateAsync(TerminalHandle handle, CancellationToken ct) =>
        Task.FromResult<TerminalTerminateResult>(new TerminalTerminateResult.NotRunning(Reason));
}
