using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MultiAIAgentCompany.Core.Terminal;

/// <summary>
/// Windows のコンソール窓で部門を開く（設計 §32-7）。
/// </summary>
/// <remarks>
/// **実測に基づく**（2026-09-14、Windows 11 Pro 26200、既定のターミナルは Windows Terminal）。
/// <list type="bullet">
/// <item><b>シェルを挟まずに CLI をそのまま新しい窓で起こす。</b>
/// <c>ShellExecute</c> で起こすとコンソールが新しく割り当てられ、
/// 既定のターミナルが Windows Terminal ならそちらに入る。</item>
/// <item><b>引数は <c>ArgumentList</c> のまま渡る。</b> npm の <c>codex.cmd</c> を経由しても、
/// 日本語・空白・<c>"</c> を含む引数が node にそのまま届いた。
/// スクリプトを書いて引用符を組み立てる形（macOS の <c>.sh</c>）にすると、
/// PowerShell 5.1 は <c>"</c> を落とすので、**あえてシェルを挟まない。**</item>
/// <item><b>窓のハンドルは PID。</b> Windows Terminal に入った窓は
/// <c>MainWindowHandle</c> が 0 になるので、窓は前面化のたびに PID から引く。</item>
/// </list>
/// <para>
/// <b>タブの共有（§33-5）はしない。</b> コンソールには「その窓のタブに打ち込む」手段が無い ——
/// 開き直しは、前の CLI を終わらせて（窓ごと閉じて）新しい窓を開く。
/// </para>
/// <para>
/// <b>窓の見出しは付けない。</b> 付けるには <c>cmd /c title</c> を挟むことになり、
/// 引数の引用符を自分で組み立てることになる。CLI が自分で見出しを付ける。
/// </para>
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsTerminalLauncher : ITerminalLauncher
{
    /// <summary><see cref="AttachConsole"/> はプロセスに1つのコンソールしか持てないので、並べて呼ばない。</summary>
    private static readonly object ConsoleGate = new();

    private readonly string _pidDirectory;

    /// <param name="pidDirectory">
    /// PID ファイルを置く場所。<b><c>.company/</c> には置かない</b>（macOS の側と同じ理由）。
    /// </param>
    public WindowsTerminalLauncher(string? pidDirectory = null)
    {
        _pidDirectory = pidDirectory
            ?? Path.Combine(Path.GetTempPath(), "MultiAIAgentCompany", "terminal");
    }

    public async Task<TerminalLaunchResult> LaunchAsync(TerminalLaunchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Directory.Exists(request.WorkingDirectory))
        {
            return new TerminalLaunchResult.Failed($"作業フォルダがありません: {request.WorkingDirectory}");
        }

        if (WindowsCommandLine.UnsafeForBatch(request.Command, request.Arguments) is { } unsafeReason)
        {
            return new TerminalLaunchResult.Failed(unsafeReason);
        }

        string pidPath;
        try
        {
            Directory.CreateDirectory(_pidDirectory);
            pidPath = Path.Combine(_pidDirectory, $"{Guid.NewGuid():N}.pid");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new TerminalLaunchResult.Failed($"PID を置く場所を作れません: {exception.Message}");
        }

        var info = new ProcessStartInfo(request.Command)
        {
            // **新しいコンソールを持たせるために ShellExecute で起こす。**
            // UseShellExecute = false だと、親がコンソールを持っているときにそれを共有してしまう。
            UseShellExecute = true,
            WorkingDirectory = request.WorkingDirectory,
            WindowStyle = ProcessWindowStyle.Normal,
        };

        foreach (var argument in request.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        int pid;
        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return new TerminalLaunchResult.Failed($"{request.Command} を起動できません");
            }

            pid = process.Id;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new TerminalLaunchResult.Failed($"ターミナルを開けません: {exception.Message}");
        }

        // **PID はこちらで書く。** macOS ではスクリプトが自分で書くが、
        // ここはスクリプトを挟まないので、起こした側しか知らない。
        // 形（PID ファイル）を揃えておけば、待つ側・終わらせる側は OS を知らずに済む。
        try
        {
            await File.WriteAllTextAsync(pidPath, pid.ToString(), ct);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // **窓はもう開いている。** 失敗にすると、アプリが知らない窓が残る（§32-8）。
            // PID が無いまま返し、呼び出し側が「起動を確かめられない」として扱う（§41）。
        }

        return new TerminalLaunchResult.Launched(new TerminalHandle(pid.ToString(), 1, pidPath));
    }

    public Task<bool> FocusAsync(TerminalHandle handle, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (!int.TryParse(handle.WindowId, out var pid) || !IsAlive(pid))
        {
            return Task.FromResult(false);
        }

        var window = FindConsoleWindow(pid);
        return Task.FromResult(window != 0 && BringToFront(window));
    }

    public async Task<TerminalTerminateResult> TerminateAsync(TerminalHandle handle, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (!int.TryParse(handle.WindowId, out var pid))
        {
            return new TerminalTerminateResult.Failed($"PID を読めません: {handle.WindowId}");
        }

        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return new TerminalTerminateResult.NotRunning($"プロセス {pid} はもう居ません");
        }

        using (process)
        {
            // **起こしたものと同じプロセスか確かめる。** PID は使い回されるので、
            // 窓を閉じたあとに別のプロセスが同じ番号を得ていることがある。
            if (!IsSameLaunch(process, handle.PidFilePath))
            {
                return new TerminalTerminateResult.NotRunning($"プロセス {pid} はもう居ません（番号が別のプロセスに使われている）");
            }

            // **ツリーごと終わらせる**（§32-2d と同じ理由）。npm の shim は
            // cmd.exe → node の2段なので、cmd だけ落とすと node が残る。
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                return new TerminalTerminateResult.NotRunning($"プロセス {pid} はもう居ません");
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return new TerminalTerminateResult.Failed($"終了させられません: {exception.Message}");
            }

            // **送っただけで返らない**（macOS の側と同じ、§41）。
            try
            {
                await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(3), ct);
            }
            catch (TimeoutException)
            {
                // 待っても死ななければ、それはそれで観測。ここでは握りつぶさない。
            }
        }

        return new TerminalTerminateResult.Signalled(pid);
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>PID ファイルより後に起きたプロセスは、起こしたものではない。</summary>
    private static bool IsSameLaunch(Process process, string pidFilePath)
    {
        try
        {
            return !File.Exists(pidFilePath)
                || process.StartTime.ToUniversalTime() <= File.GetLastWriteTimeUtc(pidFilePath).AddSeconds(5);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            // 確かめられないときは、起こしたものとして扱う（終わらせられない方が困る）。
            return true;
        }
    }

    /// <summary>
    /// そのプロセスのコンソール窓を引く。
    /// </summary>
    /// <remarks>
    /// <b>Windows Terminal に入った窓は <c>MainWindowHandle</c> では取れない</b>（実測で 0）。
    /// そのコンソールにつないで <c>GetConsoleWindow</c> を引くと疑似窓が返り、
    /// その持ち主が Windows Terminal の窓になる。conhost の窓なら、それ自体が返る。
    /// </remarks>
    private static nint FindConsoleWindow(int pid)
    {
        lock (ConsoleGate)
        {
            // **自分がコンソールを持っているとき（dotnet run など）は、つなぎ替えない。**
            // 手放すと、自分の標準出力が行き先を失う。
            if (GetConsoleWindow() == 0 && AttachConsole((uint)pid))
            {
                try
                {
                    var console = GetConsoleWindow();
                    if (console != 0)
                    {
                        var owner = GetAncestor(console, GaRootOwner);
                        return owner != 0 ? owner : console;
                    }
                }
                finally
                {
                    FreeConsole();
                }
            }
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return process.MainWindowHandle;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static bool BringToFront(nint window)
    {
        if (IsIconic(window))
        {
            ShowWindow(window, SwRestore);
        }

        return SetForegroundWindow(window);
    }

    private const uint GaRootOwner = 3;
    private const int SwRestore = 9;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    private static extern nint GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint window, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);
}

/// <summary>
/// Windows のコマンドラインで気を付けること（設計 §32-7）。
/// </summary>
/// <remarks>
/// <b>OS の API を呼ばないものだけをここに置く</b>（<see cref="MacTerminalScript"/> と同じ理由）。
/// </remarks>
public static class WindowsCommandLine
{
    /// <summary>
    /// バッチファイル（<c>.cmd</c> / <c>.bat</c>）に渡すと壊れる引数か。
    /// </summary>
    /// <remarks>
    /// <b>npm の shim は cmd.exe を通る。</b> cmd.exe は <c>\"</c> を引用符の閉じと読むので、
    /// <c>"</c> と <c>&amp;</c> や <c>|</c> が同じ引数に入ると、**後ろが別の命令として走る**。
    /// 空白の無い引数は引用符で囲まれないので、<c>&amp;</c> だけでも同じことになる。
    /// <c>%</c> は引用符の中でも環境変数として展開される。
    /// <para>
    /// <b>通さない側に倒す</b>（§7）。ふつうの引数（指示書の在り処、モデル名、
    /// <c>model_reasoning_effort="high"</c>）にはどちらも現れない。
    /// </para>
    /// </remarks>
    /// <returns>壊れるなら、人間に見せる理由。問題なければ null。</returns>
    public static string? UnsafeForBatch(string command, IEnumerable<string> arguments)
    {
        var extension = Path.GetExtension(command);
        if (!extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var argument in arguments)
        {
            // .NET は空白か `"` を含む引数だけを引用符で囲む。**囲まれない記号は cmd.exe が命令の区切りに読み**、
            // `"` を含むと `\"` で引用符の対応がずれて、やはり記号が外に出る。
            var quotedCleanly = argument.IndexOfAny([' ', '\t']) >= 0 && !argument.Contains('"');
            if (argument.IndexOfAny(['%', '\r', '\n']) >= 0
                || (!quotedCleanly && argument.IndexOfAny(['&', '|', '<', '>', '^', '(', ')']) >= 0))
            {
                return $"{Path.GetFileName(command)} は cmd.exe を通るので、この引数を安全に渡せません"
                    + "（`%` か改行、または `\"` と記号を同時に含む。フォルダ名や設定を確かめてください）";
            }
        }

        return null;
    }
}
