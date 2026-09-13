using Avalonia;

namespace MultiAIAgentCompany.Desktop;

internal static class Program
{
    /// <summary>
    /// 落ちた理由を残す場所（設計 §49）。
    /// </summary>
    /// <remarks>
    /// <b>画面が消えたのに、理由がどこにも無い</b>を無くすためにある。
    /// `dotnet run` で起動していると標準エラーは端末にしか出ず、
    /// **アプリとして起動したときは誰も見ていない。**
    /// </remarks>
    private static readonly string CrashLog = Path.Combine(
        Core.Workspace.WorkspaceMemory.RuntimeRoot, "crash.log");

    /// <summary>
    /// UI スレッドで例外が出た（設計 §49）。<b>画面に出すために使う。</b>
    /// </summary>
    public static event EventHandler<Exception>? UiThreadFailed;

    [STAThread]
    public static void Main(string[] args)
    {
        // **握りつぶさない**（§7）。未処理の例外は、必ずどこかに残す。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Record("AppDomain", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Record("Task", e.Exception);
            e.SetObserved();
        };

        // **画面の失敗で、動いているものを道連れにしない**（設計 §49）。
        // UI スレッドの例外は、既定では**プロセスごと落とす** ——
        // 実機で踏んだ（設定画面でモデルを選んでいる最中に、秘書ごと死んだ）。
        //
        // **黙って飲み込むのではない。** 記録に残し、作業ログにも出す（§7）——
        // 消えるのは「落ちること」であって、「起きたこと」ではない。
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Record("UIThread", e.Exception);
            UiThreadFailed?.Invoke(null, e.Exception);
            e.Handled = true;
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            Record("Main", exception);
            throw;
        }
    }

    /// <summary>
    /// 例外を1つ書き足す。<b>ここで失敗しても、何もしない</b> ——
    /// 記録に失敗したことで、落ち方をさらに分かりにくくしない。
    /// </summary>
    public static void Record(string where, Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLog)!);
            File.AppendAllText(
                CrashLog,
                $"---- {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [{where}]{Environment.NewLine}{exception}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 記録できなくても、落ち方は変えない。
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
