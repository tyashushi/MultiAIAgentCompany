using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace MultiAIAgentCompany.Desktop;

public partial class MainWindow : Window
{
    private readonly DemoDriver? _demo;

    public MainWindow() => AvaloniaXamlLoader.Load(this);

    public MainWindow(ShellComposer composer, DemoDriver demo) : this()
    {
        _demo = demo;
        DataContext = composer.Shell;
        UpdateDemoLabel();
    }

    /// <summary>デモ用。実セッションが繋がったらボタンごと消す。</summary>
    private void OnDemoStep(object? sender, RoutedEventArgs e)
    {
        _demo?.Step();
        UpdateDemoLabel();
    }

    private void UpdateDemoLabel()
    {
        if (_demo is not null && this.FindControl<Button>("DemoButton") is { } button)
        {
            button.Content = _demo.NextLabel;
        }
    }
}
