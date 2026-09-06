using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace MultiAIAgentCompany.Desktop;

public partial class App : Application
{
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
            var secretary = new SecretaryRunner(composer.Approvals);
            desktop.MainWindow = new MainWindow(composer, runner, secretary);

            // ウィンドウを閉じたら全部門を終了する（設計 §9）。
            // v1 はバックグラウンド継続を持たない —— 無人運転に近づくため。
            // アプリが起動した**全セッション**を終了する（設計 §9、2026-09-06 に訂正）——
            // 秘書は部門ではないので、「全部門」と書くと責務から漏れる。
            desktop.ShutdownRequested += async (_, _) =>
            {
                await runner.DisposeAsync();
                await secretary.DisposeAsync();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
