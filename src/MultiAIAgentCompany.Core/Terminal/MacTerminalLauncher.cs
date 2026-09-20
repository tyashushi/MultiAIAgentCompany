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
        string Compose(bool reuseWindow)
        {
            var target = reuseWindow && request.ReuseWindowId is { Length: > 0 } window
                ? $" in window id {window}"
                : string.Empty;
            // **TTY も一緒に返させる**（設計 §62-24）。**タブは文字列にできない**
            // （`t as text` は -1700 になる。実機で踏んだ）ので、TTY で引き直して
            // これまでと同じ "tab N of window id M" の形に自分で組む。
            return $"""
                tell application "Terminal"
                    activate
                    set t to do script "{MacTerminalScript.EscapeForAppleScriptString(scriptPath)}"{target}
                    set theTty to tty of t
                    repeat with win in windows
                        repeat with i from 1 to (count of tabs of win)
                            if tty of (item i of tabs of win) is theTty then
                                return "tab " & i & " of window id " & (id of win) & " tty " & theTty
                            end if
                        end repeat
                    end repeat
                    return "tty " & theTty
                end tell
                """;
        }

        // **打ち込む前に、そのタブが空いていることを観測する**（設計 §41、実機で踏んだ）。
        // `do script ... in window id N` は**そのタブのシェルに打ち込む**ので、
        // 前の CLI がまだ前面に居ると、**文字はその CLI に吸われて消える。**
        // `osascript` は成功を返すので、**こちらからは「渡した」に見える。**
        if (request.ReuseWindowId is { Length: > 0 } reusing
            && await WaitUntilIdleAsync(reusing, ct) is { } stillBusy)
        {
            // **窓が生きていることは観測できている。** 呼び出し側が handle を
            // 捨てないように、ふつうの失敗と型で分ける（§41-2b）。
            return new TerminalLaunchResult.WindowBusy(stillBusy);
        }

        var run = await RunAsync("osascript", ["-e", Compose(reuseWindow: true)], ct);

        // **Terminal.app が動いていないと -600 で落ちる**（2026-09-12 に実機で踏んだ）。
        // <c>tell application</c> は**起動していないアプリを自動では起こさない** ——
        // 人間が窓を全部閉じると Terminal.app 自体が終了するので、これはふつうに起きる。
        //
        // **毎回 `open` しない。** 起こすのは落ちたときだけで、
        // 平常時に余計なプロセスを1つ増やさない。
        if (run.ExitCode != 0 && IsNotRunning(run))
        {
            await RunAsync("open", ["-a", "Terminal"], ct);

            // **起動を待つ。** `open` は Terminal が Apple Event を受け取れるようになる前に返る。
            // 1回だけ待って1回だけ試す —— **開かないものを何度も叩かない**（§7）。
            await Task.Delay(TimeSpan.FromMilliseconds(800), ct);

            // **覚えている窓 id は捨てる**（レビューで発覚）。Terminal.app が
            // 動いていなかったのだから、**前の窓はもう無い** ——
            // `in window id <古い id>` のまま試すと、
            // **起こし直しても同じ理由で失敗する。**
            run = await RunAsync("osascript", ["-e", Compose(reuseWindow: false)], ct);
        }

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

    /// <summary>
    /// 「アプリケーションは実行されていません」か（AppleScript の <c>-600</c>）。
    /// </summary>
    /// <remarks>
    /// <b>番号で見る。</b> 文言は OS の言語で変わるので、そちらで判定すると
    /// **日本語環境でだけ直り、英語環境で黙って戻る**（§27-2 と同じ姿勢）。
    /// </remarks>
    private static bool IsNotRunning((int ExitCode, string Stdout, string Stderr) run) =>
        run.Stderr.Contains("-600", StringComparison.Ordinal)
        || run.Stdout.Contains("-600", StringComparison.Ordinal);

    public async Task<bool> FocusAsync(TerminalHandle handle, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handle);

        // **TTY があれば位置ではなく中身で探す**（設計 §62-24）。
        // 位置（窓 id + タブ番号）は、タブを閉じたり並べ替えたりすると別のタブを指す。
        var run = await RunAsync("osascript", ["-e", MacTerminalScript.FocusScript(handle)], ct);
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
        if (kill.ExitCode != 0)
        {
            return new TerminalTerminateResult.Failed(
                $"終了させられません: {(kill.Stderr.Length > 0 ? kill.Stderr : kill.Stdout)}");
        }

        // **送っただけで返らない**（設計 §41）。開き直しは「終わらせてから打ち込む」ので、
        // ここで待たないと**シェルがプロンプトへ戻る前に打ち込む**ことになる。
        // **待っても死ななければ、それはそれで観測**であって、ここでは握りつぶさない ——
        // 呼び出し側は次に「タブが空くか」を見る（そこで止まる）。
        await WaitForExitAsync(pid, ct);
        return new TerminalTerminateResult.Signalled(pgid);
    }

    /// <summary>そのプロセスが居なくなるまで待つ（上限つき）。</summary>
    private async Task WaitForExitAsync(int pid, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var alive = await RunAsync("ps", ["-o", "pid=", "-p", pid.ToString()], ct);
            if (alive.ExitCode != 0 || alive.Stdout.Trim().Length is 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }
    }

    /// <summary>
    /// その窓の手前のタブが空くまで待つ（設計 §41）。
    /// </summary>
    /// <remarks>
    /// <b>Terminal 自身に聞く。</b> <c>busy of selected tab</c> は
    /// 「前面のプロセスが走っているか」で、**打ち込んでよいかの直接の観測**である ——
    /// 「TERM を送ったからもう空いているはず」は推測にすぎない（§7）。
    /// </remarks>
    /// <returns>空いたら null。空かなければ、人間に見せる理由。</returns>
    private async Task<string?> WaitUntilIdleAsync(string windowId, CancellationToken ct)
    {
        var applescript =
            $"""
             tell application "Terminal" to get busy of selected tab of window id {windowId}
             """;

        for (var attempt = 0; attempt < 50; attempt++)
        {
            var run = await RunAsync("osascript", ["-e", applescript], ct);

            // **窓が無い・Terminal が居ないなら、待つ相手が居ない。**
            // ここで止めずに通し、いつもの経路（新しい窓／-600 の起こし直し）に任せる。
            if (run.ExitCode != 0)
            {
                return null;
            }

            if (!run.Stdout.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }

        // **空かないまま打ち込まない。** 打つと前の CLI に吸われて、
        // **こちらは「渡した」と思い込む**（実機で踏んだ形）。
        return "その窓では前の CLI がまだ動いている（5秒待っても空かなかった）ので、打ち込まなかった";
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
