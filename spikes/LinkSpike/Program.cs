using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

[assembly: AvaloniaTestApplication(typeof(LinkSpike.HeadlessBuilder))]
namespace LinkSpike;

public static class HeadlessBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

// 確かめたいこと: SelectableTextBlock 1つのまま、
//  (1) リンク部分のクリックが取れるか
//  (2) ドラッグ選択（リンクをまたぐものも）が壊れないか、ドラッグでリンクが誤発火しないか
//  (3) 選択のコピー文字列にリンクの文字が入るか
//  (4) リンクの上でカーソルを指にできるか
public sealed class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

public sealed class LinkTextBlock : SelectableTextBlock
{
    private static readonly Regex LinkPattern = new(@"\.company/[^\s]+?\.(png|jpg|jpeg|webp)", RegexOptions.IgnoreCase);
    private readonly List<(int Start, int End, string Path)> _links = new();
    private Point? _pressedAt;

    public event Action<string>? LinkClicked;

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    public LinkTextBlock()
    {
        AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    public void SetContent(string text)
    {
        _links.Clear();
        var inlines = new InlineCollection();
        var pos = 0;
        foreach (Match m in LinkPattern.Matches(text))
        {
            if (m.Index > pos) inlines.Add(new Run(text[pos..m.Index]));
            inlines.Add(new Run(m.Value)
            {
                TextDecorations = Avalonia.Media.TextDecorations.Underline,
                Foreground = Brushes.DodgerBlue,
            });
            _links.Add((m.Index, m.Index + m.Length, m.Value));
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) inlines.Add(new Run(text[pos..]));
        Inlines = inlines;
    }

    private string? LinkAt(Point p)
    {
        var local = new Point(p.X - Padding.Left, p.Y - Padding.Top);
        // HitTestPoint().IsInside は文字の上でも false を返す（実測）ので使わない。
        // リンクの文字範囲が占める矩形で判定する（折り返しで複数矩形になる）。
        foreach (var l in _links)
            foreach (var r in TextLayout.HitTestTextRange(l.Start, l.End - l.Start))
                if (r.Contains(local)) return l.Path;
        return null;
    }

    private void OnPressed(object? s, PointerPressedEventArgs e) => _pressedAt = e.GetPosition(this);

    private void OnMoved(object? s, PointerEventArgs e)
        => Cursor = LinkAt(e.GetPosition(this)) is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);

    private void OnReleased(object? s, PointerReleasedEventArgs e)
    {
        var at = e.GetPosition(this);
        var pressed = _pressedAt;
        _pressedAt = null;
        if (e.InitialPressMouseButton != MouseButton.Left || pressed is null) return;
        // ドラッグ（選択）ならリンクとして扱わない
        var moved = Math.Abs(at.X - pressed.Value.X) + Math.Abs(at.Y - pressed.Value.Y);
        if (moved > 4 || SelectionStart != SelectionEnd) return;
        if (LinkAt(at) is { } path) LinkClicked?.Invoke(path);
    }
}

public static class Program
{
    private const string Sample =
        "あなた: バナーを作って\n\n" +
        "秘書: デザイナーから届きました。画像ができました：.company/departments/designer/images/banner-01.png ご確認ください。\n\n" +
        "秘書: 別案もあります：.company/departments/designer/images/banner-02.png";

    public static AppBuilder BuildApp() => AppBuilder.Configure<App>().UsePlatformDetect();

    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--headless") return Headless();
        BuildApp().StartWithClassicDesktopLifetime(args, lifetime =>
        {
            lifetime.Startup += (_, _) =>
            {
                var status = new TextBlock { Margin = new Thickness(14), Text = "（クリックしたリンクがここに出る）" };
                var tb = new LinkTextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14), FontSize = 15 };
                tb.SetContent(Sample);
                tb.LinkClicked += p => status.Text = $"クリック: {p}  @ {DateTime.Now:T}";
                var panel = new DockPanel();
                DockPanel.SetDock(status, Dock.Bottom);
                panel.Children.Add(status);
                panel.Children.Add(tb);
                ((Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)lifetime).MainWindow =
                    new Window { Title = "LinkSpike", Width = 640, Height = 320, Content = panel };
            };
        });
        return 0;
    }

    private static int Headless()
    {
        var failures = 0;
        Func<string>? Dbg = null;
        void Check(string name, bool ok) { Console.WriteLine($"{(ok ? "OK " : "NG ")} {name}"); if (!ok) failures++; }

        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessBuilder));
        session.Dispatch(() =>
        {
            var clicks = new List<string>();
            var tb = new LinkTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 15 };
            tb.SetContent(Sample);
            tb.LinkClicked += clicks.Add;
            var w = new Window { Width = 640, Height = 320, Content = tb };
            w.Show();
            Dispatcher.UIThread.RunJobs();

            var text = tb.Inlines!.Text;
            var linkIdx = text.IndexOf(".company/departments/designer/images/banner-01.png", StringComparison.Ordinal);
            var ev = 0; tb.AddHandler(InputElement.PointerPressedEvent, (_, _) => ev++, RoutingStrategies.Tunnel, true);
            Dbg = () => $"{clicks.Count} ev={ev} sel=[{tb.SelectionStart},{tb.SelectionEnd}] bounds={tb.Bounds} linkIdx={linkIdx} rect={tb.TextLayout.HitTestTextPosition(linkIdx+10)}";
            Rect RectOf(int i) => tb.TextLayout.HitTestTextPosition(i);
            Point Center(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);
            Point ToWin(Point p) => tb.TranslatePoint(p, w)!.Value;

            // (1) リンク中央をクリック
            var onLink = ToWin(Center(RectOf(linkIdx + 10)));
            w.MouseDown(onLink, MouseButton.Left); w.MouseUp(onLink, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Check("リンクのクリックが取れる", clicks.Count == 1 && clicks[0].EndsWith("banner-01.png"));

            // リンク外のクリックは発火しない
            var offLink = ToWin(Center(RectOf(2)));
            w.MouseDown(offLink, MouseButton.Left); w.MouseUp(offLink, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Check("リンク外のクリックでは発火しない", clicks.Count == 1);

            // (2) リンクをまたぐドラッグ選択（1つ目の発言から2つ目のリンクの途中まで）
            var from = ToWin(Center(RectOf(1)));
            var link2 = text.IndexOf("banner-02", StringComparison.Ordinal);
            var to = ToWin(Center(RectOf(link2 + 3)));
            w.MouseDown(from, MouseButton.Left); w.MouseMove(to, RawInputModifiers.LeftMouseButton); w.MouseUp(to, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Check("ドラッグではリンクが発火しない", clicks.Count == 1);
            var sel = tb.SelectedText;
            Console.WriteLine($"   drag sel=[{tb.SelectionStart},{tb.SelectionEnd}] 期待≈[1,{link2+3}]");
            Check("発言とリンクをまたいで選択できる", sel.Contains("バナー") && sel.Contains("banner-01.png") && sel.Contains("別案"));

            // (2b) リンクの上だけでドラッグしても発火しない
            var a = ToWin(Center(RectOf(linkIdx + 2)));
            var b = ToWin(Center(RectOf(linkIdx + 20)));
            w.MouseDown(a, MouseButton.Left); w.MouseMove(b, RawInputModifiers.LeftMouseButton); w.MouseUp(b, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Check("リンク上のドラッグでは発火しない", clicks.Count == 1 && tb.SelectedText.Length > 0);

            // (1b) 選択が残った状態でリンクをクリック → 選択が解除されて発火するか
            w.MouseDown(onLink, MouseButton.Left); w.MouseUp(onLink, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Check("選択が残っていてもリンクのクリックが取れる", clicks.Count == 2);

            // (3) 全選択のテキストにパスがそのまま入る
            tb.SelectAll();
            Check("全選択の文字にパスが入る", tb.SelectedText.Contains(".company/departments/designer/images/banner-02.png"));

            // (4) カーソル
            w.MouseMove(onLink);
            Dispatcher.UIThread.RunJobs();
            Check("リンク上でカーソルが指になる", tb.Cursor is not null && tb.Cursor != Cursor.Default);
        }, default).GetAwaiter().GetResult();
        Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} NG");
        return failures;
    }
}
