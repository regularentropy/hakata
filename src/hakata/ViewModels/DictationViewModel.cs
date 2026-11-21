using System;
using System.Net.Http;
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
/// Dictation: one button, two steps — the first press records, the second sends the recording
/// to the provider, whose text lands in the source box.
/// </summary>
public sealed partial class DictationViewModel : Observable
{
    readonly IAudioRecorder recorder;
    readonly ISpeechProvider provider;
    readonly TranslationCoordinator coordinator;
    readonly AppSettings settings;
    readonly Action<string> status;
    readonly Action<bool> setError;
    readonly Action<string> appendToInput;
    readonly Action<double> addCost;

    public DictationViewModel(IAudioRecorder recorder, ISpeechProvider provider, TranslationCoordinator coordinator,
        AppSettings settings, Action<string> status, Action<bool> setError, Action<string> appendToInput, Action<double> addCost)
    {
        this.recorder = recorder;
        this.provider = provider;
        this.coordinator = coordinator;
        this.settings = settings;
        this.status = status;
        this.setError = setError;
        this.appendToInput = appendToInput;
        this.addCost = addCost;
    }

    [ObservableProperty] private bool isRecording;
    [ObservableProperty] private bool isTranscribing;
    CancellationTokenSource? cts;

    public MaterialIconKind MicIcon => IsRecording ? MaterialIconKind.Stop
        : IsTranscribing ? MaterialIconKind.CloudUpload
        : MaterialIconKind.Microphone;
    public string MicLabel => IsRecording ? "Stop" : "Speak";
    public string MicTip => IsRecording
        ? "Stop recording and transcribe it into the source box"
        : IsTranscribing ? "Uploading the recording to the speech provider"
        : "Dictate the source text: press to record, press again to transcribe";

    /// <summary>The button is inert while the take is uploading, so it must not look pressable.</summary>
    public bool MicEnabled => !IsTranscribing;

    partial void OnIsRecordingChanged(bool value) => Notify(nameof(MicIcon), nameof(MicLabel), nameof(MicTip));

    partial void OnIsTranscribingChanged(bool value) =>
        Notify(nameof(MicIcon), nameof(MicLabel), nameof(MicTip), nameof(MicEnabled));

    void MicUnavailable(Exception ex)
    {
        setError(true);
        status("Microphone unavailable: " + ex.Message);
    }

    [RelayCommand]
    async Task ToggleMic()
    {
        if (IsTranscribing) return;
        if (IsRecording)
        {
            await TranscribeRecordingAsync();
            return;
        }
        try
        {
            recorder.Start();
            setError(false);
            IsRecording = true;
            status("Recording — press Stop to transcribe");
        }
        catch (Exception ex)
        {
            MicUnavailable(ex);
        }
    }

    async Task TranscribeRecordingAsync()
    {
        IsRecording = false;
        byte[] wav;
        try
        {
            wav = recorder.Stop();
        }
        catch (Exception ex)
        {
            MicUnavailable(ex);
            return;
        }

        if (wav.Length <= Wav.HeaderBytes)
        {
            status("Nothing recorded");
            return;
        }

        try
        {
            IsTranscribing = true;
            status($"Transcribing with {settings.EffectiveSpeechModel}…");
            cts?.Dispose();
            cts = new CancellationTokenSource();
            var text = await coordinator.TrackAsync(() => provider.TranscribeAsync(wav, cts.Token, addCost));
            if (text.Length == 0)
            {
                status("No speech recognised");
                return;
            }
            // dictation extends what is already typed instead of throwing it away
            appendToInput(text);
            status("Transcribed");
        }
        catch (OperationCanceledException)
        {
            status(cts?.IsCancellationRequested == true ? "Transcription cancelled" : "Transcription timed out");
        }
        catch (HttpRequestException ex) when (ex.Message.StartsWith("404", StringComparison.Ordinal))
        {
            setError(true);
            var url = string.IsNullOrWhiteSpace(settings.SpeechUrl)
                ? ProviderUrls.AudioUrl(settings.SpeechRoute.BaseUrl)
                : ProviderUrls.OverrideUrl(settings.SpeechUrl);
            // the provider answered: it just has no transcription route there, so name it and point at the fix
            status($"No transcription endpoint at {url} — set one under Settings → Speech to text.");
        }
        catch (Exception ex)
        {
            setError(true);
            status("Transcription failed: " + ex.Message);
        }
        finally
        {
            IsTranscribing = false;
        }
    }

    /// <summary>Esc while recording drops the take: the user asked to stop, not to upload.</summary>
    public void DiscardRecording()
    {
        IsRecording = false;
        try { recorder.Stop(); } catch (Exception) { /* the device is gone either way */ }
        status("Recording discarded");
    }

    /// <summary>Drops an upload already in flight, so its transcript cannot land after the box was cleared.</summary>
    public void Cancel() => cts?.Cancel();
}
