using Avalonia;
using Avalonia.Media;

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

            // **Inter に無い字は、日本語フォントで描く**（設計 §52-6、実機で踏んだ）。
            // `Window` にフォントを指定するだけでは、**ツールチップのように窓の外に出る小窓へ届かない** ——
            // 部門の絵のツールチップが □ になった。窓ごと・部品ごとに書くと、書き忘れた所がまた化ける。
            // **既定は Inter のまま**にする（`WithInterFont` が入れる名前と同じ）—— 英数字の見た目を変えない。
            // **Windows には Hiragino が無い**ので Yu Gothic UI を並べる（無い名前は飛ばされる）。
            // 指定しなくても OS 任せの字で出るが、どの字になるかが決まらない（2026-09-14）。
            // **`Window` の FontFamily には足さない**（実機で踏んだ）—— あちらの `Inter` は同梱の Inter に解決されず、
            // 足すと英数字まで Yu Gothic UI で描かれ、**パスの `\` が `¥` になる。** ここは足りない字だけに効く。
            .With(new FontManagerOptions
            {
                DefaultFamilyName = "fonts:Inter#Inter",
                FontFallbacks =
                [
                    new FontFallback { FontFamily = new FontFamily("Hiragino Sans") },
                    new FontFallback { FontFamily = new FontFamily("Yu Gothic UI") },
                ],
            })
            .LogToTrace();
}
