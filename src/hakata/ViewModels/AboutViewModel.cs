using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hakata.Infrastructure;
using Hakata.Models;
using Hakata.Services;

namespace Hakata.ViewModels;

/// <summary>
/// The About tab: the build, its links and the update check ported from mpvmux. The check is a live
/// action, not a form field — it runs on click and reports beside the button, not through Save.
/// </summary>
public sealed partial class AboutViewModel : Observable
{
    readonly AppMetadata metadata;
    readonly IUpdateService updates;

    /// <summary>The release the last check found; null means this build is current.</summary>
    UpdateResponse? update;

    /// <summary>Raised when a check finds a release; the update window opens from it.</summary>
    internal event Action<UpdateResponse>? UpdateFound;

    /// <summary>Result of the last check, success or failure. Empty before the first one.</summary>
    [ObservableProperty] private string status = "";

    /// <summary>Persisted at Save: quiet check at launch, only a newer release shows itself.</summary>
    [ObservableProperty] private bool autoCheck;

    internal AboutViewModel(AppMetadata metadata, IUpdateService updates, AppSettings settings)
    {
        this.metadata = metadata;
        this.updates = updates;
        autoCheck = settings.CheckUpdatesOnLaunch;
    }

    public string SoftwareName => metadata.Title;
    public string VersionLabel => "v" + metadata.Version;

    /// <summary>Stated on the page, as GPLv3 asks of a program that shows its own about box.</summary>
    public string LicenseLabel => "GPL-3.0-or-later";

    /// <summary>© year holder, the holder being the project file's Company.</summary>
    public string Copyright => $"© {DateTime.Now.Year} {metadata.Author}";

    /// <summary>False while the build has no release feed: the button stays visible but disabled.</summary>
    public bool HasReleaseFeed => metadata.APILink.Length > 0;
    public bool NoReleaseFeed => !HasReleaseFeed;
    public bool HasRepository => metadata.RepositoryLink.Length > 0;

    public bool HasStatus => Status.Length > 0;
    public bool HasUpdate => update is not null;
    public string UpdateSummary => update is null ? "" : $"v{update.Tag} is available — you are running {VersionLabel}.";

    partial void OnStatusChanged(string value) => Notify(nameof(HasStatus));

    void SetUpdate(UpdateResponse? value)
    {
        update = value;
        Notify(nameof(HasUpdate), nameof(UpdateSummary));
    }

    /// <summary>Reopens the update window for the release the last check found.</summary>
    [RelayCommand]
    void ShowDetails() => UpdateFound?.Invoke(update!);

    [RelayCommand]
    async Task Check()
    {
        // no feed: the button still answers, instead of sitting disabled with no explanation
        if (!HasReleaseFeed)
        {
            SetUpdate(null);
            Status = "This build has no release feed configured, so there is nothing to check.";
            return;
        }

        Status = "Checking…";
        try
        {
            var found = await updates.CheckForUpdates();
            // the same release found again must not stack a second modal window; ShowDetails reopens it on demand
            var opensWindow = found is not null && (update is null || update.Tag != found.Tag);
            SetUpdate(found);
            // a found release also opens the update window (wired in SettingsWindow)
            if (opensWindow) UpdateFound?.Invoke(found!);
            // a found release explains itself in the card below the button
            Status = update is null ? "You are running the latest version." : "";
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException)
        {
            SetUpdate(null);
            Status = "Could not reach the internet. Check your connection.";
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            SetUpdate(null);
            Status = "Could not reach the software repository.";
        }
        catch (Exception ex)
        {
            SetUpdate(null);
            Status = "Could not check for updates: " + ex.Message;
        }
    }

    [RelayCommand]
    void OpenRepository() => Open(metadata.RepositoryLink);

    [RelayCommand]
    void OpenLicense() => Open("https://www.gnu.org/licenses/gpl-3.0.html");

    void Open(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = "Could not open the browser: " + ex.Message;
        }
    }
}
