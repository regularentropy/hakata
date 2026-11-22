using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.Input;

namespace Hakata.ViewModels;

/// <summary>Keyboard shortcuts and the clipboard actions behind them, split out of
/// <see cref="TranslateViewModel"/>. The window forwards its tunnelled KeyDown here; the three
/// things a model cannot do itself — focus the source box, open the Settings dialog, reach the
/// clipboard — go through <see cref="KeyboardHost"/>, which MainWindow assigns at construction.</summary>
public partial class TranslateViewModel
{
    internal IKeyboardHost? KeyboardHost { get; set; }

    internal interface IKeyboardHost
    {
        void FocusInput();
        Task OpenSettingsAsync();
        IClipboard? GetClipboard();
    }

    [RelayCommand]
    public async Task HandleKeyAsync(KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (ctrl && e.Key == Key.Enter)
        {
            e.Handled = true;
            // Route through the command so its re-entry guard applies: a second Ctrl+Enter must not start
            // a run that cancels the one already streaming.
            if (TranslateCommand.CanExecute(null)) TranslateCommand.Execute(null);
            return;
        }

        if (ctrl && e.Key == Key.K)
        {
            KeyboardHost?.FocusInput();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.N)
        {
            ClearAllCommand.Execute(null);
            KeyboardHost?.FocusInput();
            e.Handled = true;
            return;
        }

        if (ctrl && shift && e.Key == Key.V)
        {
            e.Handled = true;
            await PasteAsync();
            return;
        }

        if (ctrl && e.Key == Key.OemComma)
        {
            e.Handled = true;
            if (KeyboardHost is { } host) await host.OpenSettingsAsync();
            return;
        }

        // quick preset switch: the options panel can be hidden, so the picker is not always reachable
        if (ctrl && e.Key is >= Key.D1 and <= Key.D9)
        {
            e.Handled = true;
            Presets.SelectIndex(e.Key - Key.D1);
            return;
        }
        if (ctrl && e.Key is >= Key.NumPad1 and <= Key.NumPad9)
        {
            e.Handled = true;
            Presets.SelectIndex(e.Key - Key.NumPad1);
            return;
        }
        if (ctrl && e.Key == Key.P)
        {
            e.Handled = true;
            Presets.CycleNext();
            return;
        }

        if (e.Key == Key.Escape)
        {
            Cancel();
            e.Handled = true;
        }
    }

    /// <summary>Ctrl+Shift+V and the paste button: the clipboard's text into the source box.</summary>
    public async Task PasteAsync()
    {
        if (KeyboardHost?.GetClipboard() is { } cb && await cb.TryGetTextAsync() is { Length: > 0 } text)
            InputText = text;
    }

    /// <summary>The copy button: the translation onto the clipboard.</summary>
    [RelayCommand]
    public Task CopyOutputAsync()
    {
        if (string.IsNullOrEmpty(OutputText)) return Task.CompletedTask;
        return CopyTextAsync(OutputText);
    }

    /// <summary>Copies text and shows a two-second "Copied" acknowledgement — not the loss of the
    /// run's own status line, which comes back when the acknowledgement ends.</summary>
    public async Task CopyTextAsync(string text)
    {
        if (KeyboardHost?.GetClipboard() is { } cb) await cb.SetTextAsync(text);
        if (Status != "Copied") statusBeforeCopy = Status;
        Status = "Copied";
        copyStatusCts?.Cancel();
        var cts = new CancellationTokenSource();
        copyStatusCts = cts;
        _ = RestoreStatusAfterCopyAsync(cts.Token);
    }

    CancellationTokenSource? copyStatusCts;
    string statusBeforeCopy = "";

    async Task RestoreStatusAfterCopyAsync(CancellationToken token)
    {
        try { await Task.Delay(2000, token); }
        catch (OperationCanceledException) { return; }
        if (Status == "Copied") Status = statusBeforeCopy;
    }
}
