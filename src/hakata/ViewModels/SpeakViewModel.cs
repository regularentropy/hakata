using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using Hakata.Infrastructure;
using Hakata.Models;
using Hakata.Services;

namespace Hakata.ViewModels;

/// <summary>
/// The sound button: renders the translation through the provider's /audio/speech route and plays it.
/// One press starts the clip, a second press cuts it short.
/// </summary>
public sealed partial class SpeakViewModel : Observable, IDisposable
{
    readonly ISpeechProvider provider;
    readonly AppSettings settings;
    readonly ISpeechPlayer player;
    readonly TranslationCoordinator coordinator;
    readonly SoundCache cache;
    readonly History history;
    readonly Func<HistoryEntry?> currentEntry;
    readonly Func<string> textToSpeak;
    readonly Action<string> status;
    readonly Action<bool> setError;
    readonly Action<HistoryEntry?, double> addCost;

    public SpeakViewModel(ISpeechProvider provider, AppSettings settings, ISpeechPlayer player, History history,
        Func<HistoryEntry?> currentEntry, Func<string> textToSpeak, Action<string> status, Action<bool> setError,
        Action<HistoryEntry?, double> addCost, TranslationCoordinator coordinator, SoundCache? cache = null)
    {
        this.provider = provider;
        this.settings = settings;
        this.player = player;
        this.coordinator = coordinator;
        this.history = history;
        this.currentEntry = currentEntry;
        this.cache = cache ?? new SoundCache(Path.Combine(AppSettings.Dir, "sound-cache"));
        this.textToSpeak = textToSpeak;
        this.status = status;
        this.setError = setError;
        this.addCost = addCost;
        // deleting, starring or clearing an entry changes which clips are allowed to stay
        history.Changed += Prune;
    }

    // the history outlives a profile swap's view model; an undisposed subscription would leak it
    public void Dispose() => history.Changed -= Prune;

    /// <summary>
    /// Favorites keep their audio at any age; besides them the newest <see cref="AppSettings.SoundCacheEntries"/>
    /// entries do. With caching switched off nothing is kept.
    /// </summary>
    void Prune()
    {
        if (!settings.CacheSound)
        {
            cache.Prune([]);
            return;
        }
        var keep = new List<string>();
        var index = 0;
        foreach (var entry in history.Entries) // newest first
        {
            if (index++ < settings.SoundCacheEntries || entry.Favorite) keep.Add(entry.Id);
        }
        cache.Prune(keep);
    }

    [ObservableProperty] private bool isSpeaking;
    CancellationTokenSource? cts;
    CancellationTokenSource? exportCts;

    public MaterialIconKind SoundIcon => IsSpeaking ? MaterialIconKind.Stop : MaterialIconKind.VolumeHigh;
    public string SoundLabel => IsSpeaking ? "Stop" : "Listen";
    public string SoundTip => IsSpeaking
        ? "Stop the spoken translation"
        : "Hear the translation read out by the provider's text-to-speech model. Ctrl+click renders it again";

    partial void OnIsSpeakingChanged(bool value) => Notify(nameof(SoundIcon), nameof(SoundLabel), nameof(SoundTip));

    // concurrent presses are the whole point: the second one is the Stop, so the running press
    // must not disable the button while it waits for the provider and for the clip to end
    [RelayCommand(AllowConcurrentExecutions = true)]
    Task Toggle() => Run(force: false);

    /// <summary>Ctrl+click on the sound button: render again instead of replaying the cached clip.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    Task ForceToggle() => Run(force: true);

    /// <summary>The spoken translation as a plain audio file for the save dialog: the cached clip when
    /// there is one, else a fresh render billed to the current entry like any other speech.</summary>
    public async Task<(byte[] Audio, string MimeType)?> ClipForExportAsync()
    {
        var text = textToSpeak().Trim();
        if (text.Length == 0)
        {
            status("Translate first, then export the audio");
            return null;
        }
        var (entry, key) = CacheKeyFor(text);
        if (entry is not null && settings.CacheSound && cache.Get(entry.Id, key) is { } hit)
            return (hit.Audio, hit.MimeType);
        exportCts?.Dispose();
        exportCts = new CancellationTokenSource();
        try
        {
            var (audio, mimeType) = await RenderAsync(text, entry, key, exportCts.Token);
            status("Audio ready to save");
            return (audio, mimeType);
        }
        catch (OperationCanceledException)
        {
            return null; // a cancel during export is a stopped export, not a stuck "Rendering speech…"
        }
    }

    /// <summary>Renders the clip, bills it to <paramref name="entry"/> and stores it when the cache is
    /// on. The sound button and the export path share it so both bill and store the same way.</summary>
    async Task<(byte[] Audio, string MimeType)> RenderAsync(string text, HistoryEntry? entry, string key, CancellationToken ct)
    {
        status($"Rendering speech with {settings.EffectiveSoundModel}…");
        var (audio, mimeType, cost) = await coordinator.TrackAsync(() => provider.SpeakAsync(text, ct));
        addCost(entry, cost); // billed to the entry that was current when the render started
        // no entry means nothing owns this clip, so it is played and not stored
        if (entry is not null && settings.CacheSound)
            await coordinator.TrackAsync(() => Task.Run(() => cache.Put(entry.Id, key, audio, mimeType)));
        return (audio, mimeType);
    }

    /// <summary>The entry that would own a fresh render of <paramref name="text"/>, plus its sound-cache
    /// key (empty when there is no current entry to bill or cache against).</summary>
    (HistoryEntry? Entry, string Key) CacheKeyFor(string text)
    {
        var entry = currentEntry();
        return (entry, entry is null
            ? ""
            : SoundCache.Key(settings.SoundRoute.BaseUrl, settings.SoundUrl, settings.EffectiveSoundModel,
                settings.EffectiveSoundVoice, text));
    }

    async Task Run(bool force)
    {
        if (IsSpeaking)
        {
            cts?.Cancel();   // a render still in flight never reaches the player
            player.Stop();   // a clip already playing ends here
            return;
        }

        var text = textToSpeak().Trim();
        if (text.Length == 0)
        {
            status("Translate first, then listen");
            return;
        }

        cts?.Dispose();
        cts = new CancellationTokenSource();
        var ct = cts.Token;
        IsSpeaking = true;
        setError(false);
        Prune(); // a lowered limit or a fresh favorite applies before the lookup
        var (entry, key) = CacheKeyFor(text);
        try
        {
            var cached = entry is not null && settings.CacheSound && !force ? cache.Get(entry.Id, key) : null;
            var fromCache = cached is not null;
            if (cached is { } hit)
            {
                status("Speaking cached audio…");
                await coordinator.TrackAsync(() => Task.Run(() => player.Play(hit.Audio, hit.MimeType)));
            }
            else
            {
                var (audio, mimeType) = await RenderAsync(text, entry, key, ct);
                status("Speaking…");
                await coordinator.TrackAsync(() => Task.Run(() => player.Play(audio, mimeType)));
            }
            status(ct.IsCancellationRequested ? "Playback stopped" : fromCache ? "Played from cache" : "Playback finished");
        }
        catch (OperationCanceledException)
        {
            status("Playback stopped");
        }
        catch (Exception ex)
        {
            setError(true);
            status("Sound failed: " + ex.Message);
        }
        finally
        {
            IsSpeaking = false;
        }
    }

    /// <summary>A profile swap must be able to end both a render and a playing clip.</summary>
    public void Cancel()
    {
        cts?.Cancel();
        exportCts?.Cancel();
        player.Stop();
    }
}
