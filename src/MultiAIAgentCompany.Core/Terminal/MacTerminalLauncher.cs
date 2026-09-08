using System.Diagnostics;
using System.Text;

namespace MultiAIAgentCompany.Core.Terminal;

/// <summary>
/// macOS の Terminal.app で部門を開く（設計 §32）。
/// </summary>
/// <remarks>
/// **実測に基づく**（§32-2、2026-09-09、macOS 26.5 arm64）。
/// <c>osascript</c> の <c>do script</c> は <c>tab 1 of window id 43990</c> を返すので、
/// **窓の id をそのままハンドルにできる。**
/// </remarks>
// **macOS 専用であることを型に書く。** Windows / Linux は別の実装になる（§32-7）——
// 「クロスプラットフォーム」と言えるのは、その2つを書いてからである。
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public sealed class MacTerminalLauncher : ITerminalLauncher
{
    private readonly string _scriptDirectory;

    /// <param name="scriptDirectory">
    /// 起動用のシェルスクリプトを置く場所。
    /// <b><c>.company/</c> には置かない</b> —— あれは調整の記録（§6）で、
    /// これは実行時の道具にすぎない。
    /// </param>
    public MacTerminalLauncher(string? scriptDirectory = null)
    {
        _scriptDirectory = scriptDirectory
            ?? Path.Combine(Path.GetTempPath(), "MultiAIAgentCompany", "terminal");
    }

    public async Task<TerminalLaunchResult> LaunchAsync(TerminalLaunchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        string scriptPath;
        string pidPath;
        try
        {
            Directory.CreateDirectory(_scriptDirectory);
            var stem = Path.Combine(_scriptDirectory, $"{Guid.NewGuid():N}");
            scriptPath = stem + ".sh";
            pidPath = stem + ".pid";
            await File.WriteAllTextAsync(scriptPath, MacTerminalScript.Build(request, pidPath), ct);
            var mode = File.GetUnixFileMode(scriptPath);
            File.SetUnixFileMode(scriptPath, mode | UnixFileMode.UserExecute);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new TerminalLaunchResult.Failed($"起動用スクリプトを書けません: {exception.Message}");
        }

        // **窓を共有するなら `in window id <id>`**（設計 §33-5）。
        var target = request.ReuseWindowId is { Length: > 0 } window
            ? $" in window id {window}"
            : string.Empty;
        var applescript =
            $"""
             tell application "Terminal"
                 activate
                 do script "{MacTerminalScript.EscapeForAppleScriptString(scriptPath)}"{target}
             end tell
             """;

        var run = await RunAsync("osascript", ["-e", applescript], ct);
        if (run.ExitCode != 0)
        {
            return new TerminalLaunchResult.Failed(
                $"Terminal.app を開けません: {(run.Stderr.Length > 0 ? run.Stderr : run.Stdout)}");
        }

        // 例: "tab 1 of window id 43990"
        return MacTerminalScript.ParseHandle(run.Stdout, pidPath) is { } handle
            ? new TerminalLaunchResult.Launched(handle)
            : new TerminalLaunchResult.Failed($"窓の id を読み取れません: {run.Stdout.Trim()}");
    }

    public async Task<bool> FocusAsync(TerminalHandle handle, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handle);

        // **窓を前に出し、その中のタブも選ぶ**（§33-5 でタブを共有するため）。
        var applescript =
            $"""
             tell application "Terminal"
                 activate
                 set index of window id {handle.WindowId} to 1
                 set selected of tab {handle.TabIndex} of window id {handle.WindowId} to true
             end tell
             """;

        var run = await RunAsync("osascript", ["-e", applescript], ct);
        return run.ExitCode == 0;
    }

    public async Task<TerminalTerminateResult> TerminateAsync(TerminalHandle handle, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handle);

        int pid;
        try
        {
            if (!File.Exists(handle.PidFilePath))
            {
                // **まだ書かれていないのか、もう終わったのか、ここでは分からない。**
                // 分からないことを「終わった」と書かない（§7）。
                return new TerminalTerminateResult.NotRunning("PID が記録されていません");
            }

            var text = (await File.ReadAllTextAsync(handle.PidFilePath, ct)).Trim();
            if (!int.TryParse(text, out pid))
            {
                return new TerminalTerminateResult.Failed($"PID を読めません: {text}");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new TerminalTerminateResult.Failed($"PID を読めません: {exception.Message}");
        }

        // **PGID を実際に引く。** 「スクリプトの PID と一致していた」のは観測であって保証ではない。
        var group = await RunAsync("ps", ["-o", "pgid=", "-p", pid.ToString()], ct);
        if (group.ExitCode != 0 || !int.TryParse(group.Stdout.Trim(), out var pgid))
        {
            return new TerminalTerminateResult.NotRunning($"プロセス {pid} はもう居ません");
        }

        // **プロセスグループへ送る**（§32-2d）。個別に送ると子が生き残る。
        var kill = await RunAsync("kill", ["-TERM", $"-{pgid}"], ct);
        return kill.ExitCode == 0
            ? new TerminalTerminateResult.Signalled(pgid)
            : new TerminalTerminateResult.Failed(
                $"終了させられません: {(kill.Stderr.Length > 0 ? kill.Stderr : kill.Stdout)}");
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string fileName, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return (-1, string.Empty, $"{fileName} を起動できません");
            }

            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return (process.ExitCode, await stdout, await stderr);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (-1, string.Empty, exception.Message);
        }
    }
}
