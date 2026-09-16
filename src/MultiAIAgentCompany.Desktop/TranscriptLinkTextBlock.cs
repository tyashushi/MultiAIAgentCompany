using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using MultiAIAgentCompany.Core.Coordination;

namespace MultiAIAgentCompany.Desktop;

/// <summary>会話は1つのまま、画像の場所だけを押せるようにする（設計 §56-4）。</summary>
public sealed class TranscriptLinkTextBlock : SelectableTextBlock
{
    public static readonly StyledProperty<string?> TranscriptProperty =
        AvaloniaProperty.Register<TranscriptLinkTextBlock, string?>(nameof(Transcript));

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private IReadOnlyList<TranscriptImageLink> _links = [];
    private Point? _pressedAt;

    public string? Transcript
    {
        get => GetValue(TranscriptProperty);
        set => SetValue(TranscriptProperty, value);
    }

    public event Action<string>? LinkClicked;

    // **継承しただけではテーマが当たらない**（§56 の試作で確認）。
    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    public TranscriptLinkTextBlock()
    {
        AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        PointerExited += (_, _) => Cursor = Cursor.Default;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != TranscriptProperty) return;

        // Text と Inlines が互いを更新しないよう、入力用のプロパティを分ける（§56）。
        var text = Transcript ?? "";
        _links = TranscriptImageLinks.Find(text);
        _pressedAt = null;
        var inlines = new InlineCollection();
        var position = 0;
        foreach (var link in _links)
        {
            if (position < link.Start) inlines.Add(new Run(text[position..link.Start]));
            var run = new Run(link.Link) { TextDecorations = Avalonia.Media.TextDecorations.Underline };
            run.Bind(TextElement.ForegroundProperty, new DynamicResourceExtension("AccentBrush"));
            inlines.Add(run);
            position = link.Start + link.Length;
        }
        if (position < text.Length) inlines.Add(new Run(text[position..]));
        Inlines = inlines;
    }

    private string? LinkAt(Point point)
    {
        // Margin は GetPosition(this) に含まれない。引くのは内側の Padding だけ（§56）。
        var local = new Point(point.X - Padding.Left, point.Y - Padding.Top);
        // **HitTestPoint().IsInside は使わない** —— 文字の真上でも false（試作の実測）。
        foreach (var link in _links)
            foreach (var rectangle in TextLayout.HitTestTextRange(link.Start, link.Length))
                if (rectangle.Contains(local)) return link.Link;
        return null;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e) =>
        _pressedAt = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ? e.GetPosition(this) : null;

    private void OnMoved(object? sender, PointerEventArgs e) =>
        Cursor = LinkAt(e.GetPosition(this)) is null ? Cursor.Default : HandCursor;

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        var at = e.GetPosition(this);
        var pressed = _pressedAt;
        _pressedAt = null;
        if (e.InitialPressMouseButton != MouseButton.Left || pressed is null) return;
        var moved = Math.Abs(at.X - pressed.Value.X) + Math.Abs(at.Y - pressed.Value.Y);
        if (moved > 4 || SelectionStart != SelectionEnd) return;
        if (LinkAt(at) is { } link) LinkClicked?.Invoke(link);
    }
}
