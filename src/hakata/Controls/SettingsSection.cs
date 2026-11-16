using Avalonia;
using Avalonia.Controls;

namespace Hakata.Controls;

/// <summary>
/// A settings card: the <c>Border.card.page</c> block with a section title over its fields — the
/// shape every Settings page repeats. The template lives in App.axaml. Cards without a plain-text
/// title (or with a button beside it) stay a plain <c>Border Classes="card page"</c>.
/// </summary>
public class SettingsSection : ContentControl
{
    public static readonly StyledProperty<string?> HeaderProperty =
        AvaloniaProperty.Register<SettingsSection, string?>(nameof(Header));

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<SettingsSection, double>(nameof(Spacing), 8d);

    /// <summary>The section title shown above the content.</summary>
    public string? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    /// <summary>Gap between the rows inside the card; cards that packed their rows tighter set 6.</summary>
    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }
}
