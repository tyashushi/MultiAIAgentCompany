using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System.Runtime.InteropServices;

namespace MultiAIAgentCompany.Desktop;

public partial class App : Application
{
    /// <summary>シグナルの購読。<b>アプリと同じ寿命で持つ</b> —— 捨てると解除される。</summary>
    private PosixSignalRegistration[]? _signals;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var composer = ShellComposer.CreateDefault(TimeProvider.System);
            var runner = new DepartmentRunner(composer);

            // 観測を部門タイルにも控える。「原因を見る」「観測を見る」で人間へ出す。
            runner.Observed += (_, item) =>
            {
                foreach (var tile in composer.Shell.Departments.Where(t => t.Id == item.DepartmentId))
                {
                    tile.Record(item.Evidence);
                }
            };
            // 診断はライブ専用（設計 §22）。**観測と混ぜない** ——
            // こちらは redact していない中身なので、保存も転記もしない。
            runner.Diagnosed += (_, item) =>
            {
                foreach (var tile in composer.Shell.Departments.Where(t => t.Id == item.DepartmentId))
                {
                    tile.Diagnostics.Add(item.Diagnostic);
                }
            };

            var secretary = new SecretaryRunner(composer.Approvals);
            desktop.MainWindow = new MainWindow(composer, runner, secretary);

            // ウィンドウを閉じたら全部門を終了する（設計 §9）。
            // v1 はバックグラウンド継続を持たない —— 無人運転に近づくため。
            // アプリが起動した**全セッション**を終了する（設計 §9、2026-09-06 に訂正）——
            // 秘書は部門ではないので、「全部門」と書くと責務から漏れる。
            //
            // **`async void` で await しない**（設計 §25-1、2026-09-07 に訂正）。
            // `ShutdownRequested` の戻り値は void なので、最初の await でフレームワークに
            // 制御が戻り、**CLI の終了を待たずにアプリが落ちる**。孤児が残るかどうかが
            // 運任せになっていた（実機で残らなかったのは間に合っただけ）。
            // → 一度キャンセルして、終了しきってから改めて閉じる。
            var closing = false;
            var finished = false;

            // **ウィンドウを閉じる以外の終わり方も塞ぐ**（設計 §25-1、実測 2026-09-07）。
            // `kill`（SIGTERM）で落とすと `ShutdownRequested` は発火せず、**秘書の
            // `claude` がそのまま残った**。§9 は「アプリが親で、終わったら子も終わる」と
            // 決めているので、終わり方によって保証が変わってはいけない。
            // **SIGHUP は Windows に無い**（`PlatformNotSupportedException`）。
            // 登録で落ちると、ウィンドウが出る前にアプリが死ぬ（レビューで発覚）。
            List<PosixSignalRegistration> signals =
            [
                PosixSignalRegistration.Create(PosixSignal.SIGTERM, Terminate),
                PosixSignalRegistration.Create(PosixSignal.SIGINT, Terminate),
            ];

            if (!OperatingSystem.IsWindows())
            {
                signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGHUP, Terminate));
            }

            _signals = [.. signals];

            void Terminate(PosixSignalContext context)
            {
                // **2度目以降も既定の終了を止める**（レビューで発覚）。
                // 後始末の最中に来た2発目で既定の終了が走ると、**そこで子が切り離される** ——
                // まさにこの変更が塞いだはずの孤児が戻ってくる。
                // 止めるのは常に。走らせる後始末だけを1回にする。
                context.Cancel = true;
                if (closing)
                {
                    return;
                }

                closing = true;
                (desktop.MainWindow as MainWindow)?.NotifyClosing();

                // **無限には待たない** —— 閉じられないアプリの方が悪い。
                _ = ShutDownAsync(desktop, runner, secretary, () => finished = true);
            }

            desktop.ShutdownRequested += (_, e) =>
            {
                // 後始末を終えて**自分から**閉じに来たものだけ通す。
                if (finished)
                {
                    return;
                }

                // **2度目以降も止める**（レビューで発覚）。後始末の最中にもう一度閉じられると、
                // 通常終了が走って**子が切り離される** —— 塞いだはずの孤児が戻ってくる。
                e.Cancel = true;
                if (closing)
                {
                    return;
                }

                closing = true;

                // **片付けている間に新しい仕事を始めさせない**（レビューで発覚）。
                // 閉じる要求を取り消しているので、ウィンドウはまだ操作できる。
                (desktop.MainWindow as MainWindow)?.NotifyClosing();
                _ = ShutDownAsync(desktop, runner, secretary, () => finished = true);
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 子プロセスを終わらせてからアプリを閉じる（設計 §9 / §25-1）。
    /// </summary>
    /// <remarks>
    /// <b>ここで投げない。</b> 後始末の失敗でアプリが閉じられなくなると、
    /// 人間はウィンドウを閉じられないまま子プロセスも残る —— 一番悪い状態になる。
    /// </remarks>
    private static async Task ShutDownAsync(
        IClassicDesktopStyleApplicationLifetime desktop, DepartmentRunner runner, SecretaryRunner secretary,
        Action finished)
    {
        try
        {
            // **無限に待たない。** 後始末が固まっても、人間はアプリを閉じられるべき。
            await Task.WhenAny(
                Task.Run(async () =>
                {
                    // **`DisposeAsync` ではなく `StopAllAsync`**（レビューで発覚）。
                    // あちらは起動済みのセッションしか閉じないので、**起動処理中の部門を
                    // 取りこぼす** —— 片付けたあとに立ち上がって孤児になる。
                    await runner.StopAllAsync();
                    await secretary.DisposeAsync();
                }),
                Task.Delay(TimeSpan.FromSeconds(20)));
        }
        catch (Exception)
        {
            // 閉じることを止めない。残ったプロセスは §9 の親子関係で OS が引き取る。
        }

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            // ここから先の `ShutdownRequested` は自分が出したもの。止めない。
            finished();
            desktop.Shutdown();
        });
    }
}
