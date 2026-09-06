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
            desktop.MainWindow = new MainWindow(composer, new DemoDriver(composer, TimeProvider.System));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
