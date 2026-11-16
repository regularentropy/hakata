using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Material.Icons;
using Hakata.Models;

namespace Hakata.Infrastructure;

/// <summary>
/// A language or model picker opens its list on a click or a Down arrow, not on focus: tabbing
/// through a form must not leave a drop-down behind at every step.
/// </summary>
public static class PickerUi
{
    public static void EnableOnDemand(Control root)
    {
        root.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        root.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        Open(e.Source);
    }

    static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down || e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Alt)) return;
        if (Open(e.Source)) e.Handled = true;
    }

    static bool Open(object? source)
    {
        if (source is not Visual visual) return false;
        if (visual.FindAncestorOfType<AutoCompleteBox>(includeSelf: true) is not { IsDropDownOpen: false } box)
            return false;
        box.IsDropDownOpen = true;
        return true;
    }
}

/// <summary>Applies the theme to the running app and returns the icon the toggle button shows.</summary>
public static class ThemeUi
{
    public static MaterialIconKind Apply(ThemeMode theme)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = theme switch
            {
                ThemeMode.Dark => ThemeVariant.Dark,
                ThemeMode.System => ThemeVariant.Default,
                _ => ThemeVariant.Light,
            };
        return Icon(theme);
    }

    /// <summary>Light → dark → follow the OS → light.</summary>
    public static ThemeMode Next(ThemeMode theme) => theme switch
    {
        ThemeMode.Light => ThemeMode.Dark,
        ThemeMode.Dark => ThemeMode.System,
        _ => ThemeMode.Light,
    };

    /// <summary>The icon shows what the next click does, so dark mode offers a sun.</summary>
    static MaterialIconKind Icon(ThemeMode theme) => theme switch
    {
        ThemeMode.Dark => MaterialIconKind.WhiteBalanceSunny,
        ThemeMode.System => MaterialIconKind.BrightnessAuto,
        _ => MaterialIconKind.WeatherNight,
    };
}

/// <summary>
/// True when the bound index equals the parameter. Lets a row of toggles drive one int property,
/// which is how the tone options became segmented buttons instead of dropdowns.
/// </summary>
public sealed class IndexMatchConverter : IValueConverter
{
    public static readonly IndexMatchConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int index && int.TryParse(parameter?.ToString(), out var wanted) && index == wanted;

    // IsChecked binds two-way by default: a click on a pill must not write back a value, the command does that
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}

/// <summary>Shared boilerplate for modal prompt windows: resolve with default when closed, show as a
/// dialog when an owner exists, else as a top-level window.</summary>
public static class ModalUi
{
    public static Task<T?> ShowModal<T>(Window window, TaskCompletionSource<T?> completion, Window? owner)
    {
        window.Closed += (_, _) => completion.TrySetResult(default);
        if (owner is null) window.Show(); else window.ShowDialog(owner);
        return completion.Task;
    }

    /// <summary>Cancel/Esc: resolve with null and close. The XAML wires IsCancel to this.</summary>
    public static void Cancel<T>(Window window, TaskCompletionSource<T?> completion)
    {
        completion.TrySetResult(default);
        window.Close();
    }
}

/// <summary>Runs a dialog action and logs the crash instead of letting it escape an async void handler.</summary>
public static class SafeUi
{
    public static async Task Guard(Func<Task> action)
    {
        try { await action(); } catch (Exception ex) { Program.LogCrash(ex); }
    }
}
