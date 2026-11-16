using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Hakata.Controls;

/// <summary>
/// A <see cref="Button"/> whose content is a Material icon, optionally followed by a label.
/// It stays a Button on purpose: wrapping one would swallow Click, Command and the button styles.
/// </summary>
public class IconButton : Button
{
    public static readonly StyledProperty<MaterialIconKind> IconProperty =
        AvaloniaProperty.Register<IconButton, MaterialIconKind>(nameof(Icon));

    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<IconButton, string?>(nameof(Label));

    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<IconButton, double>(nameof(IconSize), 14d);

    static IconButton()
    {
        IconProperty.Changed.AddClassHandler<IconButton>((b, _) => b.Rebuild());
        LabelProperty.Changed.AddClassHandler<IconButton>((b, _) => b.Rebuild());
        IconSizeProperty.Changed.AddClassHandler<IconButton>((b, _) => b.Rebuild());
    }

    public MaterialIconKind Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>Optional text after the icon; leave empty for an icon-only button.</summary>
    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    void Rebuild()
    {
        var icon = new MaterialIcon { Kind = Icon, IconSize = IconSize, VerticalAlignment = VerticalAlignment.Center };
        if (Label is not { Length: > 0 })
        {
            Content = icon;
            return;
        }
        var text = new TextBlock { Text = Label, VerticalAlignment = VerticalAlignment.Center };
        // match the button's font: a TextBlock built here does not pick up the Button FontSize style itself
        text.Bind(TextBlock.FontSizeProperty, this.GetObservable(FontSizeProperty));
        // the colour is inherited from the button, so disabled and accent labels follow it
        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children = { icon, text },
        };
    }
}
