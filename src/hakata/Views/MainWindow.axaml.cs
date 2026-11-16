using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Hakata.Infrastructure;
using Hakata.Models;
using Hakata.Services;
using Hakata.ViewModels;

namespace Hakata.Views;

public partial class MainWindow : Window, TranslateViewModel.IKeyboardHost
{
    readonly SettingsService settingsService;
    readonly IServiceProvider services;

    /// <summary>The keyboard shortcuts' window-side capabilities (see TranslateViewModel.Keyboard.cs).</summary>
    void TranslateViewModel.IKeyboardHost.FocusInput() => InputBox.Focus();
    Task TranslateViewModel.IKeyboardHost.OpenSettingsAsync() => OpenSettingsAsync();
    IClipboard? TranslateViewModel.IKeyboardHost.GetClipboard() => Clipboard;
    readonly DispatcherTimer agoTimer;
    readonly DispatcherTimer disarmTimer;
    bool outputPinned = true;
    double lastOutputOffset;
    double sidebarMinWidth;
    double sidebarMaxWidth;
    internal TranslateViewModel ViewModel { get; }

    /// <summary>XAML designer entry point; the app resolves the real graph in <see cref="AppServices"/>.</summary>
    public MainWindow() : this(AppServices.Create()) { }

    public MainWindow(IServiceProvider services)
    {
        this.services = services;
        settingsService = services.GetRequiredService<SettingsService>();
        ViewModel = services.GetRequiredService<TranslateViewModel>();
        ViewModel.KeyboardHost = this;

        InitializeComponent();
        DataContext = ViewModel;
        // the title names the active profile; the axaml Title stays the default-profile fallback.
        // Resolved from the run's data dir (not the pointer): --profile launches never write the
        // pointer, and a hot-swap persists it only after this window is already constructed.
        var profile = Profiles.ActiveNameForDir(Profiles.BaseDir, AppSettings.Dir);
        if (profile != Profiles.DefaultName) Title = $"Translate — {profile}";

        // The sidebar lives in a fixed-width column the GridSplitter drags; the width itself is
        // Chrome.SidebarWidth, persisted in settings. Hiding zeroes the column (MinWidth too, or
        // layout would clamp it back) and showing restores the stored width.
        sidebarMinWidth = BodyGrid.ColumnDefinitions[0].MinWidth;
        sidebarMaxWidth = BodyGrid.ColumnDefinitions[0].MaxWidth;
        BodyGrid.ColumnDefinitions[0].Width = new GridLength(AppSettings.DefaultSidebarWidth); // initial value; ApplySidebarColumn sets the stored one
        ViewModel.Chrome.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChromeViewModel.SidebarVisible) ||
                e.PropertyName == nameof(ChromeViewModel.SidebarWidth))
                ApplySidebarColumn();
        };
        SidebarSplitter.DragCompleted += (_, _) =>
            ViewModel.Chrome.SidebarWidth = BodyGrid.ColumnDefinitions[0].ActualWidth;
        ApplySidebarColumn();

        RestoreGeometry();
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        // Ctrl+click on the sound button re-renders the clip; tunnelled so the button never sees the
        // press and does not also run its plain command
        SpeakButton.AddHandler(PointerPressedEvent, OnSpeakForcePressed, RoutingStrategies.Tunnel);
        Opened += (_, _) =>
        {
            RestorePosition();
            InputBox.Focus();
            // First run: nothing can be translated yet, so the setup dialog is the only useful next step.
            if (!settingsService.Settings.HasProvider)
                Dispatcher.UIThread.Post(() => _ = OpenSettingsAsync(), DispatcherPriority.Background);
        };
        Closing += (_, _) =>
        {
            SaveGeometry();
            try { settingsService.Save(); } catch (Exception ex) { Program.LogCrash(ex); } // a locked file must not crash the exit
        };

        SourceBox.ItemFilter = FuzzyMatch.Matches;
        TargetBox.ItemFilter = FuzzyMatch.Matches;
        // Click or Down opens the full language list; focusing alone must not leave a drop-down behind.
        PickerUi.EnableOnDemand(this);
        // Keep the streaming text in view unless the user has scrolled up.
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        OutputScroll.ScrollChanged += (_, _) =>
        {
            var offset = OutputScroll.Offset.Y;
            if (Math.Abs(offset - lastOutputOffset) < 0.5) return; // extent growth is not a user scroll
            lastOutputOffset = offset;
            outputPinned = OutputScroll.Extent.Height - OutputScroll.Viewport.Height - offset <= 24;
        };
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);

        agoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        agoTimer.Tick += (_, _) => ViewModel.History.RefreshAgo();
        agoTimer.Start();

        // A destructive button that was clicked once stays armed for a few seconds, then disarms.
        disarmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        disarmTimer.Tick += (_, _) =>
        {
            disarmTimer.Stop();
            ViewModel.History.ClearArmed = false;
            ViewModel.Presets.DeleteArmed = false;
        };
        ViewModel.History.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HistoryViewModel.ClearArmed) && ViewModel.History.ClearArmed)
                RestartDisarmTimer();
        };
        ViewModel.Presets.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PresetsViewModel.DeleteArmed) && ViewModel.Presets.DeleteArmed)
                RestartDisarmTimer();
        };

        // a closed window (hot profile swap replaces it) must not leave its timers ticking
        Closed += (_, _) => { agoTimer.Stop(); disarmTimer.Stop(); };
    }

    void RestartDisarmTimer()
    {
        disarmTimer.Stop();
        disarmTimer.Start();
    }

    void ApplySidebarColumn()
    {
        var column = BodyGrid.ColumnDefinitions[0];
        if (ViewModel.Chrome.SidebarVisible)
        {
            column.MinWidth = sidebarMinWidth;
            // a stored width from an older build can sit outside the clamps
            column.Width = new GridLength(Math.Clamp(ViewModel.Chrome.SidebarWidth, sidebarMinWidth, sidebarMaxWidth));
        }
        else
        {
            // the dragged width is already in Chrome.SidebarWidth (the splitter's DragCompleted
            // wrote it), so hiding only zeroes the column; showing restores the stored value
            column.MinWidth = 0;
            column.Width = new GridLength(0);
        }
    }

    /// <summary>Entry point for the --translate smoke test.</summary>
    internal Task TranslateNow() => ViewModel.TranslateAsync();

    internal Task RunExtrasForTest() => ViewModel.RunExtrasAsync();

    /// <summary>
    /// The source box grows with its text, so the empty area of the pane still has to put the caret
    /// in it: the click lands on the scroll viewer around it.
    /// </summary>
    void OnInputAreaClick(object? sender, PointerPressedEventArgs e) => InputBox.Focus();

    /// <summary>Shift+click ranges and Ctrl+click toggles are the list's own; the model just needs the set.</summary>
    void OnHistorySelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        ViewModel.History.SetSelection(HistoryList.SelectedItems?.OfType<HistoryEntry>() ?? []);

    /// <summary>Ctrl+click on the sound button: render the text again instead of replaying the cached clip.</summary>
    void OnSpeakForcePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        ViewModel.Speak.ForceToggleCommand.Execute(null);
    }

    // ---- window geometry -------------------------------------------------

    void RestoreGeometry()
    {
        var settings = settingsService.Settings;
        if (settings.WindowWidth >= MinWidth) Width = settings.WindowWidth;
        if (settings.WindowHeight >= MinHeight) Height = settings.WindowHeight;
    }

    /// <summary>Reapplies a saved position only when it still lands on a connected screen, then the
    /// maximized state with it.</summary>
    void RestorePosition()
    {
        var settings = settingsService.Settings;
        if (settings.WindowX is { } x && settings.WindowY is { } y)
        {
            var point = new PixelPoint(x, y);
            if (Screens.ScreenFromPoint(point) is not null)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = point;
            }
        }

        if (settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    void SaveGeometry()
    {
        var settings = settingsService.Settings;
        settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState != WindowState.Normal) return; // maximised bounds are not the normal size
        settings.WindowWidth = Width;
        settings.WindowHeight = Height;
        settings.WindowX = Position.X;
        settings.WindowY = Position.Y;
    }

    // ---- keyboard --------------------------------------------------------

    async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        try
        {
            await ViewModel.HandleKeyAsync(e);
        }
        catch (Exception ex)
        {
            Program.LogCrash(ex); // a clipboard or dialog failure is not worth the process
        }
    }

    // ---- clipboard / dialogs / drop --------------------------------------

    async void OnPaste(object? sender, RoutedEventArgs e) => await SafeUi.Guard(ViewModel.PasteAsync);

/// <summary>The New button: clear both panes, then put the caret in the source box so typing starts the next translation.</summary>
    void OnNew(object? sender, RoutedEventArgs? e)
{
    ViewModel.ClearAllCommand.Execute(null);
    InputBox.Focus();
}

    async void OnCopy(object? sender, RoutedEventArgs e) => await SafeUi.Guard(ViewModel.CopyOutputAsync);

    /// <summary>Copy button inside one alternative row; the row's <see cref="Alternative"/> is the data context.</summary>
    async void OnCopyAlternative(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: Alternative { Text.Length: > 0 } alt }) return;
        await SafeUi.Guard(() => ViewModel.CopyTextAsync(alt.Text));
    }

    async void OnSettings(object? sender, RoutedEventArgs e) => await SafeUi.Guard(OpenSettingsAsync);

    /// <summary>Opening the model menu rebuilds it from the user-added models (default + extras) — no network.</summary>
    void OnModelButtonClick(object? sender, RoutedEventArgs e) => _ = ViewModel.LoadModelMenuAsync();

    void OnModelPicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string id }) ViewModel.PickModel(id);
    }

    /// <summary>Export button: renders (or reuses the cached) spoken translation and saves it as a
    /// plain audio file via the OS save dialog.</summary>
    async void OnExportSound(object? sender, RoutedEventArgs e) => await SafeUi.Guard(ExportSoundAsync);

    async Task ExportSoundAsync()
    {
        var clip = await ViewModel.Speak.ClipForExportAsync();
        if (clip is null) return;
        var (audio, mime) = clip.Value;
        var ext = mime switch
        {
            "audio/mpeg" => ".mp3",
            "audio/wav" or "audio/x-wav" => ".wav",
            "audio/ogg" => ".ogg",
            "audio/aiff" or "audio/x-aiff" => ".aiff",
            "audio/flac" => ".flac",
            "audio/webm" => ".webm",
            _ => ".audio",
        };
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the spoken translation",
            SuggestedFileName = "translation" + ext,
            DefaultExtension = ext.TrimStart('.'),
            FileTypeChoices = [new FilePickerFileType("Audio") { Patterns = ["*" + ext] }],
        });
        if (file is null) return; // cancelled
        await using var stream = await file.OpenWriteAsync();
        await stream.WriteAsync(audio);
        ViewModel.Status = $"Saved {file.Name}";
    }

    async Task OpenSettingsAsync()
    {
        await services.GetRequiredService<SettingsWindow>().ShowDialog(this);
        ViewModel.Chrome.SidebarWidth = settingsService.Settings.SidebarWidth; // a width reset in Display applies at once
        ViewModel.RefreshProviderLabel();
        ViewModel.Presets.Reload(); // the Settings window's Presets tab may have added, renamed, reordered or imported presets
    }

    void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer?.TryGetText() is not { Length: > 0 } text) return;
        ViewModel.InputText = text;
        e.Handled = true;
    }

    // ---- streaming -------------------------------------------------------

    void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TranslateViewModel.OutputText) && ViewModel.IsBusy && outputPinned)
            OutputScroll.ScrollToEnd();
    }
}
