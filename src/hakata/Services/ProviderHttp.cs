using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Hakata.Infrastructure;

namespace Hakata.Services;

/// <summary>The transport plumbing shared by every provider route: auth, status checks, capped
/// body reads with a deadline, and the line reader the SSE stream is parsed from.</summary>
internal static class ProviderHttp
{
    internal static readonly JsonSerializerOptions JsonOpts = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>A whole JSON body past this is truncated: no real chat, listing or error body is bigger.</summary>
    internal const int MaxBody = 4_000_000;

    /// <summary>How long a non-streaming body may take to arrive before the read is cut.</summary>
    static readonly TimeSpan BodyDeadline = TimeSpan.FromMinutes(5);

    /// <summary>How long one read on the SSE stream may receive nothing before the stream is taken as
    /// stalled. Under ResponseHeadersRead the HttpClient timeout does not cover the response body, so a
    /// provider that sends its headers plus one chunk and then goes silent is cut here, not waited out.</summary>
    internal static readonly TimeSpan StreamIdle = TimeSpan.FromSeconds(60);

    /// <summary>One SSE line longer than this reads as a broken stream, not as content.</summary>
    internal const int MaxSseLine = 1_000_000;

    /// <summary>Bearer auth when a key is on file; a missing key stays headerless (local servers).</summary>
    internal static void WithAuth(HttpRequestMessage req, string? apiKey)
    {
        var key = apiKey?.Trim() ?? "";
        if (key.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    internal static async Task EnsureOk(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        var body = await ReadTextCappedAsync(res.Content, 64_000, ct);
        throw new HttpRequestException($"{(int)res.StatusCode} {res.ReasonPhrase}: {Describe(body)}");
    }

    /// <summary>A body read up to <paramref name="cap"/> bytes; the rest of the stream is dropped.</summary>
    internal static async Task<string> ReadTextCappedAsync(HttpContent content, int cap, CancellationToken ct) =>
        Encoding.UTF8.GetString(await ReadUpToAsync(content, cap, strict: false, ct));

    /// <summary>A body read up to <paramref name="cap"/> bytes; unlike text, an over-long one is an error,
    /// because a truncated file (audio, image) is garbage rather than a shorter answer.</summary>
    internal static Task<byte[]> ReadBytesCappedAsync(HttpContent content, int cap, CancellationToken ct) =>
        ReadUpToAsync(content, cap, strict: true, ct);

    static async Task<byte[]> ReadUpToAsync(HttpContent content, int cap, bool strict, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        // a body that stalls or drips past the deadline is cut, not waited out
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(BodyDeadline);
        using var collected = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        var capped = false; // the loop ended on the cap, not on the end of the body
        while ((read = await stream.ReadAsync(buffer.AsMemory(), deadline.Token)) > 0)
        {
            var room = cap - (int)collected.Length;
            if (read <= room)
            {
                collected.Write(buffer, 0, read);
                continue;
            }
            if (room > 0) collected.Write(buffer, 0, room);
            if (strict) throw TooBig(cap);
            capped = true;
            break;
        }
        // a body shorter than the length the response headers promised was cut mid-transfer
        if (!capped && content.Headers.ContentLength is > 0 && collected.Length < content.Headers.ContentLength)
            throw CutShort(collected.Length, content.Headers.ContentLength.Value);
        return collected.ToArray();
    }

    internal static HttpRequestException CutShort(long collected, long declared) =>
        new($"the provider's answer was cut short: {collected} of {declared} bytes");

    internal static HttpRequestException TooBig(int cap) =>
        new($"the provider answered with more than the {cap} byte limit");

    /// <summary>Lines read in fixed chunks, so one line past <paramref name="cap"/> is discarded instead of
    /// buffered whole. Yields null for that line: a decoder fed one unbounded line would grow the process.
    /// With <paramref name="idleTimeout"/> set, a read that receives nothing for that long ends the stream
    /// as stalled; the deadline restarts after every chunk read.</summary>
    internal static async IAsyncEnumerable<string?> ReadLines(StreamReader reader, int cap,
        [EnumeratorCancellation] CancellationToken ct, TimeSpan? idleTimeout = null)
    {
        var buffer = new char[8192];
        var pending = new StringBuilder();
        var dropped = false; // the line being collected went past the cap and is thrown away
        int read;
        while ((read = await ReadChunk(reader, buffer, idleTimeout, ct)) > 0)
        {
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != '\n') continue;
                if (!dropped) pending.Append(buffer, start, i - start);
                yield return dropped ? null : pending.ToString().TrimEnd('\r');
                pending.Clear();
                dropped = false;
                start = i + 1;
            }
            if (dropped) continue;
            pending.Append(buffer, start, read - start);
            if (pending.Length > cap) { pending.Clear(); dropped = true; }
        }
        if (dropped) yield return null;
        else if (pending.Length > 0) yield return pending.ToString().TrimEnd('\r');
    }

    /// <summary>One chunk read. The idle deadline is a fresh linked token per read, so it restarts after
    /// every chunk; a deadline hit means the stream stalled — an error, not the caller's own cancellation.</summary>
    static async Task<int> ReadChunk(StreamReader reader, char[] buffer, TimeSpan? idleTimeout, CancellationToken ct)
    {
        if (idleTimeout is not { } idle) return await reader.ReadAsync(buffer.AsMemory(), ct);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(idle);
        try
        {
            return await reader.ReadAsync(buffer.AsMemory(), linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException($"the stream stalled: no data for {idle.TotalSeconds:0.##}s");
        }
    }

    /// <summary>One short, quotable line out of a provider error body, instead of the raw JSON blob.</summary>
    internal static string Describe(string body)
    {
        if (Jsons.TryParse(body) is { ValueKind: JsonValueKind.Object } root)
        {
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String) return Clip(error.GetString() ?? "");
                if (error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("message", out var nested) && nested.ValueKind == JsonValueKind.String)
                    return Clip(nested.GetString() ?? "");
            }
            if (root.TryGetProperty("message", out var direct) && direct.ValueKind == JsonValueKind.String)
                return Clip(direct.GetString() ?? "");
        }
        return Clip(body);
    }

    static string Clip(string text)
    {
        var single = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (single.Length == 0) return "no details from the provider";
        if (single.Length <= 160) return single;
        // one back from the cut when it would split a surrogate pair
        var cut = char.IsHighSurrogate(single[159]) ? 159 : 160;
        return single[..cut] + "…";
    }
}
