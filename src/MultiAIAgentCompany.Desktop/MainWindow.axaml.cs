using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace MultiAIAgentCompany.Desktop;

public partial class MainWindow : Window
{
    private readonly DemoDriver? _demo;
    private readonly ShellComposer? _composer;

    public MainWindow() => AvaloniaXamlLoader.Load(this);

    public MainWindow(ShellComposer composer, DemoDriver demo) : this()
    {
        _demo = demo;
        _composer = composer;
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

    /// <summary>
    /// ワークスペースを選ぶ。選んだら各 CLI の trust を読み直す（設計 §13-9）。
    /// <b>アプリは trust を書かない</b> —— 読むだけ。
    /// </summary>
    private async void OnPickWorkspace(object? sender, RoutedEventArgs e)
    {
        if (_composer is null)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "AI たちが働くフォルダを選ぶ",
            AllowMultiple = false,
        });

        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path)
        {
            return;
        }

        await _composer.SelectWorkspaceAsync(path, CancellationToken.None);
    }
}
