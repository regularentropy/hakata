using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hakata.Infrastructure;
using Hakata.Services;

namespace Hakata.ViewModels;

/// <summary>The update window: what the found release is and where to get it. Nothing is downloaded
/// or installed — the release page opens in the browser.</summary>
internal sealed partial class UpdateViewModel : Observable
{
    readonly UpdateResponse resp;
    readonly AppMetadata metadata;

    /// <summary>Why the browser did not open; empty until the first failed attempt.</summary>
    [ObservableProperty] private string status = "";

    internal UpdateViewModel(UpdateResponse resp, AppMetadata metadata)
    {
        this.resp = resp;
        this.metadata = metadata;
    }

    public string Heading => "A new version is available";
    public string NewVersion => $"v{resp.Tag}";
    public string CurrentVersion => $"v{metadata.Version}";
    public string VersionSummary => $"You are running {CurrentVersion}.";
    // the notes are feed-controlled markdown rendered by a recursive parser: bound the parse
    // work (total length and per-line nesting depth) before the text can reach the engine
    public string ReleaseNotes => string.IsNullOrWhiteSpace(resp.ReleaseNotes)
        ? "No release notes were provided."
        : TextRules.BoundRenderedMarkdown(resp.ReleaseNotes.Trim());

    public bool HasStatus => Status.Length > 0;

    partial void OnStatusChanged(string value) => Notify(nameof(HasStatus));

    [RelayCommand]
    void OpenUpdateLink()
    {
        // resp.Url is the update feed's html_url: only a public http(s) URL may reach the OS handler
        if (!LinkGuard.OpenExternal(resp.Url))
            Status = "Could not open the release page: unsafe or unopenable link.";
    }
}
