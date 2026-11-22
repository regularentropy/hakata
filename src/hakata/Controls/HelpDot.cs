using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Hakata.Controls;

/// <summary>
/// The small "?" beside a settings field. Hover shows the explanation as a tooltip, a click opens it
/// as a flyout, and keyboard or screen-reader users get it through the automation name: the old
/// TextBlock could only be discovered with a mouse.
/// </summary>
public class HelpDot : Button
{
    public static readonly StyledProperty<string?> ExplanationProperty =
        AvaloniaProperty.Register<HelpDot, string?>(nameof(Explanation));

    public HelpDot()
    {
        Content = "?";
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Help);
        Click += OnClick;
        Sync();
    }

    public string? Explanation
    {
        get => GetValue(ExplanationProperty);
        set => SetValue(ExplanationProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ExplanationProperty) Sync();
    }

    void Sync()
    {
        AutomationProperties.SetName(this, Explanation ?? "Help");
        AutomationProperties.SetHelpText(this, Explanation);
        ToolTip.SetTip(this, Explanation);
    }

    void OnClick(object? sender, RoutedEventArgs e)
    {
        if (Explanation is not { Length: > 0 } text) return;
        var flyout = new Flyout
        {
            Content = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 320,
                FontSize = 12,
            },
        };
        flyout.ShowAt(this);
    }
}
