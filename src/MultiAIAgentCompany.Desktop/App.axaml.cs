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
            desktop.MainWindow = new MainWindow(composer, runner);

            // ウィンドウを閉じたら全部門を終了する（設計 §9）。
            // v1 はバックグラウンド継続を持たない —— 無人運転に近づくため。
            desktop.ShutdownRequested += async (_, _) => await runner.DisposeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
