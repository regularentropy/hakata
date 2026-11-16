using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Hakata.Models;
using Hakata.Services;
using Avalonia.VisualTree;
using Hakata.Infrastructure;
using Hakata.ViewModels;

namespace Hakata.Views;

public partial class SettingsWindow : Window
{
    readonly SettingsViewModel viewModel;
    readonly History history;
    // captured at construction: a hot profile swap re-points AppSettings.Dir, but this window's
    // key-file operations belong to the profile it was opened for
    readonly string dir = AppSettings.Dir;
    bool passwordArmed;

    public SettingsWindow() : this(new SettingsViewModel(new SettingsService(new AppSettings())), new History()) { }

    public SettingsWindow(SettingsViewModel viewModel, History history)
    {
        this.viewModel = viewModel;
        this.history = history;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
        // closing abandons the view model; its pending probe must not keep running past the dialog
        Closed += (_, _) => viewModel.Providers.CancelPending();
        // the button labels the current protection state of the key file
        var keyPath = HistoryCrypto.KeyPath(dir);
        var isPassword = File.Exists(keyPath) && HistoryCrypto.IsPasswordHeader(File.ReadAllText(keyPath));
        HistoryPasswordButton.Content = isPassword
            ? "Remove password protection"
            : "Protect history with a password";
        ChangePasswordButton.IsVisible = isPassword;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        // a click or Down opens any picker, including the provider rows a template builds; tabbing
        // through the form must not open a drop-down at every step
        PickerUi.EnableOnDemand(this);

        // the profiles tab binds straight to its child VM, like the other tabs' paths do
        ProfilesTab.DataContext = viewModel.Profiles;
        PresetsTab.DataContext = viewModel.Presets;
        AboutTab.DataContext = viewModel.About;
        // a release found on the About page opens the update window, owned by this dialog
        viewModel.About.UpdateFound += u =>
        {
            // both surfaces: the window over this dialog and the main window's header cog, which otherwise stays a plain cog until restart
            if (Owner is MainWindow main) main.ViewModel.Chrome.ShowUpdate(u.Tag);
            UpdaterWindow.ShowFor(this, u, new AppMetadata());
        };
        // first run is "Set up your provider": what the build is can wait until a provider is on file
        AboutTab.IsVisible = !viewModel.IsFirstRun;
        // profiles can change while the dialog is closed (another instance, the picker): reload on
        // open and on every activation, so changes made elsewhere appear without closing the dialog
        Opened += (_, _) => viewModel.Profiles.Refresh();
        Activated += (_, _) => viewModel.Profiles.Refresh();

        // first run: the empty provider row is there to be typed into, so put the caret in it
        if (viewModel.IsFirstRun) Opened += (_, _) => FirstProviderField()?.Focus();
    }

    /// <summary>Set wraps the existing data key with a password (the stored translations are not
    /// re-encrypted); remove rewrites the key file machine-bound. Removing asks once, since it
    /// downgrades protection silently otherwise.</summary>
    async void OnHistoryPassword(object? sender, RoutedEventArgs e)
    {
        await SafeUi.Guard(async () =>
        {
            var keyPath = HistoryCrypto.KeyPath(dir);
            var isPassword = File.Exists(keyPath) && HistoryCrypto.IsPasswordHeader(File.ReadAllText(keyPath));
            if (isPassword)
            {
                if (!passwordArmed)
                {
                    passwordArmed = true;
                    HistoryPasswordButton.Content = "Click again to remove the password";
                    return;
                }
                passwordArmed = false;
                var dek = history.DataKey ?? HistoryCrypto.LoadOrCreateKey(dir);
                if (dek is null)
                {
                    viewModel.Error = "The history is locked — unlock it at startup first";
                    return;
                }
                WriteKeyFile(keyPath, HistoryCrypto.MachineKeyFileBytes(dek));
                HistoryPasswordButton.Content = "Protect history with a password";
                ChangePasswordButton.IsVisible = false;
                return;
            }
            var password = await PasswordWindow.PromptSetAsync(this);
            if (password is null) return;
            var key = history.DataKey ?? HistoryCrypto.LoadOrCreateKey(dir);
            if (key is null)
            {
                viewModel.Error = "Could not read the history key";
                return;
            }
            WriteKeyFile(keyPath, Encoding.UTF8.GetBytes(HistoryCrypto.WrapPassword(key, password)));
            history.AdoptKey(key);
            HistoryPasswordButton.Content = "Remove password protection";
            ChangePasswordButton.IsVisible = true;
        });
    }

    /// <summary>Change re-wraps the same data key with a new password (the stored translations are
    /// not re-encrypted). When the store is still locked, the current password is verified first.</summary>
    async void OnChangePassword(object? sender, RoutedEventArgs e)
    {
        await SafeUi.Guard(async () =>
        {
            var keyPath = HistoryCrypto.KeyPath(dir);
            if (!File.Exists(keyPath) || !HistoryCrypto.IsPasswordHeader(File.ReadAllText(keyPath))) return;
            var dek = history.DataKey;
            if (dek is null)
            {
                var current = await PasswordWindow.PromptUnlockAsync(this,
                    pw => HistoryCrypto.UnwrapPassword(File.ReadAllText(keyPath), pw) is not null);
                if (current is null) return;
                dek = HistoryCrypto.UnwrapPassword(File.ReadAllText(keyPath), current);
            }
            if (dek is null) { viewModel.Error = "Could not read the history key"; return; }
            var password = await PasswordWindow.PromptSetAsync(this);
            if (password is null) return;
            WriteKeyFile(keyPath, Encoding.UTF8.GetBytes(HistoryCrypto.WrapPassword(dek, password)));
            passwordArmed = false;
            HistoryPasswordButton.Content = "Remove password protection";
        });
    }

    /// <summary>tmp + rename for history.key: a crash mid-write leaves the previous key file intact
    /// instead of an unreadable one, and the tmp is 0600 off Windows before any byte lands.</summary>
    static void WriteKeyFile(string keyPath, byte[] bytes)
    {
        StoreFile.WriteAtomic(keyPath, tmp => StoreFile.WriteRestrictedBytes(tmp, bytes));
        StoreFile.RestrictFile(keyPath);
    }

    /// <summary>Export writes the presets as JSON; import merges only names or ids not on file yet.
    /// Both report through Error, this dialog's own status line.</summary>
    async void OnPresetsExport(object? sender, RoutedEventArgs e) =>
        await SafeUi.Guard(async () =>
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export presets",
                SuggestedFileName = "hakata-presets.json",
                DefaultExtension = "json",
                FileTypeChoices = [new FilePickerFileType("Presets") { Patterns = ["*.json"] }]
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync(viewModel.Presets.ToJson());
            viewModel.Error = $"Saved {file.Name}";
        });

    async void OnPresetsImport(object? sender, RoutedEventArgs e) =>
        await SafeUi.Guard(async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import presets",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Presets") { Patterns = ["*.json"] }]
            });
            var file = files.FirstOrDefault();
            if (file is null) return;
            var localPath = file.TryGetLocalPath();
            string text;
            if (localPath is not null)
            {
                text = await File.ReadAllTextAsync(localPath);
            }
            else
            {
                await using var stream = await file.OpenReadAsync();
                using var reader = new StreamReader(stream);
                text = await reader.ReadToEndAsync();
            }
            var added = viewModel.Presets.MergeJson(text);
            viewModel.Error = added > 0 ? $"Added {added} preset{(added == 1 ? "" : "s")}" : "No new presets in that file";
        });

    /// <summary>Double-clicking a profile row switches to it, like Open in the startup picker.</summary>
    void OnProfileDoubleTapped(object? sender, TappedEventArgs e)
    {
        var profiles = viewModel.Profiles;
        if (profiles.Selected is { IsRunning: false } && profiles.SwitchCommand.CanExecute(null))
            profiles.SwitchCommand.Execute(null);
    }

    /// <summary>The name field of the first provider row, through the item container the list built for it.</summary>
    TextBox? FirstProviderField() =>
        ProviderList.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();

    /// <summary>Esc cancels; Enter saves everywhere. A picker keeps Enter for its suggestion.</summary>
    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            viewModel.CancelCommand.Execute(null);
            return;
        }

        if (e.Key != Key.Enter) return;
        if (FocusManager?.GetFocusedElement() is Visual focused &&
            focused.FindAncestorOfType<AutoCompleteBox>(includeSelf: true) is not null) return;
        e.Handled = true;
        viewModel.SaveCommand.Execute(null);
    }
}
