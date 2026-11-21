using System;
using System.IO;
using Hakata.Services;
using Hakata.ViewModels;

namespace Hakata;

public static partial class SelfTest
{
    /// <summary>Security-audit entries C1/C2 (2026-11-16): a URL taken from a remote response may
    /// only reach the OS handler after LinkGuard validation. C1: UpdateViewModel.OpenUpdateLink
    /// on the feed's html_url. C2's window wiring is checked in UpdaterTest (--updatertest), which
    /// owns the headless window. The invariant: LinkGuard.OpenExternal refuses non-http(s) schemes
    /// and non-public hosts before any Process.Start.</summary>
    static void SecurityLinks()
    {
        // refused before any dispatch: hostile schemes, local targets, malformed values
        string?[] hostile =
        {
            "file:///etc/hostname", "FILE:///etc/hostname", "file:/etc/hostname", "file://localhost/etc/hostname",
            "javascript:alert(1)", "data:text/html,<script>1</script>", @"\\evil\share\x.exe",
            "https://127.0.0.1/x", "https://[::1]/x", "https://0.0.0.0/", "https://localhost/x",
            "https://host.local/x", "https://host.internal/x", " file:///etc/hostname",
            "file:///etc/hostname\r\nhttps://github.com", "", null, "   ",
        };
        foreach (var h in hostile)
            Check(!LinkGuard.OpenExternal(h), $"OpenExternal refuses [{h ?? "<null>"}] without dispatching");

        // the guard itself accepts the legit shape (public http(s)); no dispatch attempted here
        Check(LinkGuard.IsFetchableLink("https://github.com/regularentropy/hakata", out _),
            "IsFetchableLink accepts a public https URL");
        Check(!LinkGuard.IsFetchableLink("https://127.0.0.1", out _), "IsFetchableLink rejects loopback");
        Check(!LinkGuard.IsFetchableLink("https://user:pass@127.0.0.1/", out _), "IsFetchableLink rejects loopback behind userinfo");

        // C1: the update window's button refuses a feed html_url that is not a public http(s) URL
        var vm = new UpdateViewModel(new UpdateResponse("9.9.9", "file:///etc/hostname", "notes"), new AppMetadata());
        Check(!vm.HasStatus, "update vm: no status before the first open attempt");
        vm.OpenUpdateLinkCommand.Execute(null);
        Check(vm.HasStatus && vm.Status.Length > 0,
            "update vm: a non-http html_url is refused with a status message and never dispatched");
    }

    /// <summary>Security-scan entry R1 (2026-11-21): provider-returned audio bytes must pass a managed
    /// WAV-header guard before any native decoder parses them — libsndfile CVE-2026-37555 overflows a
    /// 32-bit int on samplesperblock*blocks. Invariant: a hostile IMA-ADPCM header is rejected in managed
    /// code (the guard's InvalidDataException, so no SoundFileReader/AudioFileReader is ever constructed),
    /// while a sane clip and the exact int32 boundary still decode. Payload layout matches the stored PoC
    /// .security-scan/pocs/R1-1.cs.</summary>
    static void SecuritySpeechWavGuard()
    {
        static byte[] ImaAdpcm(uint dataLen, int blockAlign = 32771, int spb = 65535)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write("RIFF"u8); w.Write(36u + dataLen); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(20u);
            w.Write((ushort)0x11); w.Write((ushort)1); w.Write(8000u);
            w.Write(8000u * (uint)blockAlign / (uint)spb);
            w.Write((ushort)blockAlign); w.Write((ushort)4); w.Write((ushort)2); w.Write((ushort)spb);
            w.Write("data"u8); w.Write(dataLen);
            return ms.ToArray(); // bare 48-byte header (fmt chunk 20); the fileLen argument carries the sparse size
        }

        // overflow: 65535 * 32769 = 2147516415 > int.MaxValue — rejected when the real file is that big
        var hostile = ImaAdpcm(32769u * 32771u);
        try { SpeechPlayer.ThrowIfHostileWav(hostile, 48L + 32769u * 32771u); Check(false, "speech wav guard: overflow header rejected"); }
        catch (InvalidDataException) { Check(true, "speech wav guard: samplesperblock*blocks > int32.MaxValue rejected in managed code"); }

        // boundary: 65535 * 32768 = 2147450880 <= int.MaxValue — accepted
        var boundary = ImaAdpcm(32768u * 32771u);
        try { SpeechPlayer.ThrowIfHostileWav(boundary, 48L + 32768u * 32771u); Check(true, "speech wav guard: product <= int32.MaxValue accepted"); }
        catch (InvalidDataException) { Check(false, "speech wav guard: int32 boundary wrongly rejected"); }

        // declared-huge but real-small: clamped to the real size like libsndfile — accepted
        try { SpeechPlayer.ThrowIfHostileWav(hostile, hostile.Length); Check(true, "speech wav guard: declared size clamped to real file length"); }
        catch (InvalidDataException) { Check(false, "speech wav guard: clamped small-real-file header wrongly rejected"); }

        // samplesperblock not matching blockAlign (libsndfile requires equality) — rejected
        var mismatch = ImaAdpcm(1024, spb: 12345);
        try { SpeechPlayer.ThrowIfHostileWav(mismatch, mismatch.Length); Check(false, "speech wav guard: spb mismatch rejected"); }
        catch (InvalidDataException) { Check(true, "speech wav guard: IMA ADPCM samplesperblock mismatch rejected"); }

        // zero channels / zero blockAlign — rejected
        var zeroCh = ImaAdpcm(1024);
        zeroCh[22] = 0; zeroCh[23] = 0; // channels field
        try { SpeechPlayer.ThrowIfHostileWav(zeroCh, zeroCh.Length); Check(false, "speech wav guard: zero channels rejected"); }
        catch (InvalidDataException) { Check(true, "speech wav guard: zero channels rejected"); }

        // sane PCM clip from the recorder passes the guard
        var pcm = Wav.FromPcm(new byte[3200], MicrophoneRecorder.SampleRate, 1, 16);
        SpeechPlayer.ThrowIfHostileWav(pcm, pcm.Length);
        Check(true, "speech wav guard: recorder PCM WAV accepted");

        // non-RIFF bytes pass through to the native decoder (the guard owns RIFF/WAVE only)
        SpeechPlayer.ThrowIfHostileWav(new byte[] { 0xFF, 0xFB, 0x90, 0x00 }, 4);
        Check(true, "speech wav guard: non-RIFF bytes pass through");

        // sink level: the hostile sparse file through OpenAudio must fail with the guard's
        // InvalidDataException — pre-fix this reached libsndfile (SoundFileException, or abort rc 134)
        var path = Path.Combine(Path.GetTempPath(), "hakata-selftest-" + Guid.NewGuid().ToString("n") + ".wav");
        try
        {
            using (var fs = new FileStream(path, FileMode.CreateNew)) { fs.Write(hostile); fs.SetLength(48L + 32769u * 32771u); } // data chunk starts at 48
            try { SpeechPlayer.OpenAudio(path); Check(false, "speech wav guard: OpenAudio rejects the hostile file"); }
            catch (InvalidDataException) { Check(true, "speech wav guard: OpenAudio throws the guard's InvalidDataException before any native decoder"); }
            catch (Exception e) { Check(false, $"speech wav guard: OpenAudio threw {e.GetType().Name} (native decoder reached) — {e.Message}"); }
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
