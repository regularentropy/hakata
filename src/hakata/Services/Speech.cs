using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.Alsa;
using NAudio.SoundFile;
using Hakata.Models;

namespace Hakata.Services;

/// <summary>Microphone capture, feeding the mic button that dictates the source text.</summary>
public interface IAudioRecorder : IDisposable
{
    bool IsRecording { get; }

    /// <summary>Opens the default input device; throws when there is none, or Windows refuses the permission.</summary>
    void Start();

    /// <summary>Closes the device and returns everything captured as a WAV file.</summary>
    byte[] Stop();
}

/// <summary>
/// Records 16 kHz mono 16-bit PCM: the one shape every speech-to-text endpoint accepts, and about
/// 1.9 MB per minute. The device is opened on Start, so a headless run never touches the sound card.
/// </summary>
public sealed class MicrophoneRecorder : IAudioRecorder
{
    public const int SampleRate = 16000;
    const int Bits = 16;
    const int Channels = 1;

    IWaveIn? device;
    MemoryStream? pcm;
    readonly object bufferLock = new(); // NAudio's DataAvailable thread vs Stop()'s ToArray

    public bool IsRecording => device is not null;

    /// <summary>Five minutes of 16 kHz mono 16-bit PCM; past that a take is clipped, never buffered whole.</summary>
    const int MaxPcm = SampleRate * 2 * 60 * 5;

    /// <summary>The capture backend is per-OS — WinMM on Windows, ALSA on Linux — so nothing above this
    /// point knows which one it got. shortcut: no macOS backend (NAudio.MacOS is not referenced), the
    /// mic reports itself unavailable there.</summary>
    static IWaveIn NewMic(WaveFormat format)
    {
        if (OperatingSystem.IsWindows()) return new WaveIn { WaveFormat = format };
        if (OperatingSystem.IsLinux()) return new AlsaIn { WaveFormat = format };
        throw new PlatformNotSupportedException("Microphone capture needs Windows or Linux.");
    }

    public void Start()
    {
        if (device is not null) return;
        var buffer = new MemoryStream();
        var mic = NewMic(new WaveFormat(SampleRate, Bits, Channels));
        mic.DataAvailable += (_, e) =>
        {
            lock (bufferLock)
            {
                var room = MaxPcm - (int)buffer.Length;
                if (room > 0) buffer.Write(e.Buffer, 0, Math.Min(e.BytesRecorded, room));
            }
        };
        try
        {
            mic.StartRecording();
        }
        catch
        {
            mic.Dispose();
            buffer.Dispose();
            throw; // no device or no permission: the caller reports it, nothing is left half open
        }
        device = mic;
        pcm = buffer;
    }

    public byte[] Stop()
    {
        var buffer = pcm;
        var mic = device;
        pcm = null;
        device = null;
        if (mic is null || buffer is null) return Wav.FromPcm([], SampleRate, Channels, Bits);

        try { mic.StopRecording(); } catch (Exception) { /* already stopped by the OS */ }
        mic.Dispose();
        using (buffer)
        lock (bufferLock) return Wav.FromPcm(buffer.ToArray(), SampleRate, Channels, Bits);
    }

    public void Dispose() => Stop();
}

/// <summary>Plays one rendered speech clip: the sound button in the translation toolbar.</summary>
public interface ISpeechPlayer : IDisposable
{
    bool IsPlaying { get; }

    /// <summary>Plays the audio and returns when the clip ends or <see cref="Stop"/> cuts it short.</summary>
    void Play(byte[] audio, string mimeType);

    void Stop();
}

/// <summary>
/// Audio playback through NAudio: WinMM on Windows, ALSA on Linux, and the decoder follows — Media
/// Foundation/ACM by file extension on Windows, libsndfile by content elsewhere. Both decoders need a
/// seekable stream, so the bytes go to a temp file first.
/// The temp directory is captured at construction (per DI graph), so a hot profile swap cannot land a clip
/// in the other profile's directory. The device is opened on Play, so a headless run never touches the sound card.
/// </summary>
public sealed class SpeechPlayer : ISpeechPlayer
{
    // AppSettings.Dir is static and re-pointed by a profile swap, so it is read here, once per graph, not at Play time
    readonly string dir;

    public SpeechPlayer(AppSettings settings) => dir = AppSettings.Dir;

    IWavePlayer? device;

    public bool IsPlaying => device is not null;

    public void Play(byte[] audio, string mimeType)
    {
        Stop();
        var path = Path.Combine(dir, $"hakata-speech-{Guid.NewGuid():n}{Extension(mimeType)}");
        WaveStream? reader = null;
        IWavePlayer? output = null;
        try
        {
            // the clip is the user's text spoken aloud: created owner-only (0600 off Windows), never world-readable
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(path, options)) file.Write(audio);
            using var done = new ManualResetEventSlim();
            reader = OpenAudio(path); // inside the try so a bad clip still reaches the delete
            output = NewOut();
            output.PlaybackStopped += (_, _) => done.Set();
            // assigned before the Init/Play gap: a Stop landing in that window must not be lost
            device = output;
            output.Init(reader);
            output.Play();
            done.Wait(); // ends with the clip, or as soon as Stop reaches the device
        }
        finally
        {
            device = null;
            output?.Dispose();
            reader?.Dispose();
            try { File.Delete(path); } catch (Exception) { /* the decoder still holds it; the app dir keeps it private */ }
        }
    }

    public void Stop()
    {
        try { device?.Stop(); } catch (Exception) { /* the device is already gone */ }
    }

    public void Dispose() => Stop();

    /// <summary>WinMM on Windows, ALSA on Linux. shortcut: no macOS backend, so playback reports itself
    /// unavailable there instead of silently doing nothing.</summary>
    static IWavePlayer NewOut()
    {
        if (OperatingSystem.IsWindows()) return new WaveOut();
        if (OperatingSystem.IsLinux()) return new AlsaOut();
        throw new PlatformNotSupportedException("Audio playback needs Windows or Linux.");
    }

    /// <summary>Windows reads by file extension (mp3, wav, aiff); libsndfile reads by content, so the
    /// temp file's extension only has to be plausible there. The header guard runs first on every OS,
    /// so no native decoder ever parses a hostile WAV.</summary>
    internal static WaveStream OpenAudio(string path)
    {
        ThrowIfHostileWav(path);
        if (OperatingSystem.IsWindows()) return new AudioFileReader(path);
        if (OperatingSystem.IsLinux()) return new SoundFileReader(path);
        throw new PlatformNotSupportedException("Audio playback needs Windows or Linux.");
    }

    /// <summary>Managed pre-check of a RIFF/WAVE header before any native decoder parses the file.
    /// CVE-2026-37555 (GHSA-x46m-7mvp-6fvq): libsndfile's WAV IMA-ADPCM path computes
    /// sf.frames = samplesperblock * blocks in a 32-bit int and overflows on a hostile header — a heap
    /// overflow on builds without the open-time validate, an abort on 1.2.2-4. The product is recomputed
    /// here in 64-bit and rejected before the native library sees the bytes.</summary>
    internal static void ThrowIfHostileWav(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var head = new byte[(int)Math.Min(f.Length, 64 * 1024L)];
        int read = 0;
        while (read < head.Length)
        {
            int n = f.Read(head, read, head.Length - read);
            if (n <= 0) break;
            read += n;
        }
        if (read < head.Length) Array.Resize(ref head, read);
        ThrowIfHostileWav(head, f.Length);
    }

    /// <summary>Byte-level form of the guard. <paramref name="fileLen"/> is the real file size; it clamps
    /// the declared data-chunk length exactly like libsndfile does, so both sides compute the same blocks.</summary>
    internal static void ThrowIfHostileWav(byte[] wav, long fileLen)
    {
        if (wav.Length < 12) return; // too short to be a WAV; the native decoder rejects it on its own
        var riff = Encoding.ASCII.GetString(wav, 0, 4);
        if (riff is not ("RIFF" or "RIFX")) return; // not a WAV container; no WAV overflow math applies
        if (Encoding.ASCII.GetString(wav, 8, 4) != "WAVE") return;
        bool big = riff == "RIFX";
        ushort U16(int o) => big ? BinaryPrimitives.ReadUInt16BigEndian(wav.AsSpan(o, 2))
                                 : BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(o, 2));
        uint U32(int o) => big ? BinaryPrimitives.ReadUInt32BigEndian(wav.AsSpan(o, 4))
                               : BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(o, 4));

        // Two independent parses of the head, because a chunk walk desyncs on inconsistent chunk sizes:
        // (a) walk chunks by declared size; (b) literal marker scan. If EITHER parse yields a fmt+data
        // pair, its values are validated; a hostile pair in either parse is rejected. A file with no
        // parseable pair is left to the native decoder, which rejects it (frames compute from nothing).
        int walkFmt = -1; long walkFmtSize = 0; long walkData = -1; long walkDeclared = 0;
        long pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var id = Encoding.ASCII.GetString(wav, (int)pos, 4);
            long size = U32((int)pos + 4);
            if (id == "fmt " && walkFmt < 0) { walkFmt = (int)pos + 8; walkFmtSize = size; }
            else if (id == "data" && walkData < 0) { walkData = pos + 8; walkDeclared = size; }
            pos += 8 + size + (size & 1); // chunks are word-aligned
        }
        int litFmtMark = IndexOfAscii(wav, "fmt ", 0);
        int litDataMark = litFmtMark >= 0 ? IndexOfAscii(wav, "data", litFmtMark + 4) : -1;
        int litFmt = litFmtMark >= 8 ? litFmtMark + 8 : -1;           // fmt data starts after the marker+size
        long litFmtSize = litFmtMark >= 8 ? U32(litFmtMark - 8) : 0;
        long litData = litDataMark >= 8 ? litDataMark + 8 : -1;
        long litDeclared = litDataMark >= 8 ? U32(litDataMark - 8) : 0;

        if (walkFmt >= 0 && walkData >= 0) CheckPair(walkFmt, walkFmtSize, walkData, walkDeclared);
        if (litFmt >= 0 && litData >= 0 && (litFmt != walkFmt || litData != walkData)) CheckPair(litFmt, litFmtSize, litData, litDeclared);

        void CheckPair(int fmt, long fmtSize, long data, long declared)
        {
            if (fmt + 16 > wav.Length) return;
            ushort tag = U16(fmt), channels = U16(fmt + 2), blockAlign = U16(fmt + 12), bits = U16(fmt + 14);
            if (channels == 0 || blockAlign == 0)
                throw new InvalidDataException("hostile WAV header: zero channels or blockAlign");
            long clamped = Math.Min(declared, Math.Max(0, fileLen - data));
            long blocks = clamped / blockAlign;
            if (tag == 0x11) // WAVE_FORMAT_IMA_ADPCM — the CVE-2026-37555 codec
            {
                if (fmtSize < 20 || fmt + 20 > wav.Length)
                    throw new InvalidDataException("hostile WAV header: truncated IMA ADPCM fmt chunk");
                int spb = U16(fmt + 18);
                long required = blockAlign < 4L * channels ? -1 : 2L * (blockAlign - 4L * channels) / channels + 1;
                if (required < 0 || spb != required)
                    throw new InvalidDataException("hostile WAV header: IMA ADPCM samplesperblock does not match blockAlign");
                if ((long)spb * blocks > int.MaxValue)
                    throw new InvalidDataException("hostile WAV header: IMA ADPCM samplesperblock*blocks overflows int32 (CVE-2026-37555)");
            }
            else if (tag == 1) // PCM
            {
                if (bits == 0) throw new InvalidDataException("hostile WAV header: PCM with zero bit depth");
                if (clamped * 8 / ((long)bits * channels) > int.MaxValue)
                    throw new InvalidDataException("hostile WAV header: PCM frame count overflows int32");
            }
        }
    }

    static int IndexOfAscii(byte[] hay, string needle, int start)
    {
        for (int i = Math.Max(0, start); i + 4 <= hay.Length; i++)
            if (hay[i] == needle[0] && hay[i + 1] == needle[1] && hay[i + 2] == needle[2] && hay[i + 3] == needle[3])
                return i;
        return -1;
    }

    /// <summary>NAudio decodes these by file extension on Windows. shortcut: ogg, flac and pcm have no
    /// Windows decoder here, the play attempt fails with its own message; add a decoder when a provider
    /// returns one by default. On Linux libsndfile decodes them and only the extension is optimistic.</summary>
    static string Extension(string mimeType) => mimeType switch
    {
        "audio/wav" or "audio/x-wav" or "audio/wave" => ".wav",
        "audio/aiff" or "audio/x-aiff" => ".aiff",
        _ => ".mp3",
    };
}

/// <summary>A canonical 44-byte RIFF header over raw PCM. Transcribers read the header, so it has to be right.</summary>
public static class Wav
{
    public const int HeaderBytes = 44;

    /// <summary>Playback seconds of a WAV file, read from its own header. 0 when the header is not one.</summary>
    public static double Seconds(byte[] wav)
    {
        if (wav.Length < HeaderBytes || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF") return 0;
        var byteRate = BitConverter.ToInt32(wav, 28);
        var dataBytes = BitConverter.ToInt32(wav, 40);
        return byteRate > 0 && dataBytes > 0 ? dataBytes / (double)byteRate : 0;
    }

    public static byte[] FromPcm(byte[] pcm, int sampleRate, int channels, int bits)
    {
        var blockAlign = channels * bits / 8;
        using var stream = new MemoryStream(HeaderBytes + pcm.Length);
        using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8);
        w.Write(36 + pcm.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);                                  // PCM chunk size
        w.Write((short)1);                            // format: PCM
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(sampleRate * blockAlign);             // byte rate
        w.Write((short)blockAlign);
        w.Write((short)bits);
        w.Write("data"u8);
        w.Write(pcm.Length);
        w.Write(pcm);
        w.Flush();
        return stream.ToArray();
    }
}
