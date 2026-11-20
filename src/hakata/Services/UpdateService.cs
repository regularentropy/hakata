using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hakata.Services;

/// <summary>A published release newer than the running build.</summary>
internal sealed record UpdateResponse(string Tag, string Url, string ReleaseNotes);

internal interface IUpdateService
{
    /// <summary>The newer release, or null when this build is current or no feed is configured.</summary>
    Task<UpdateResponse?> CheckForUpdates();
}

/// <summary>
/// Ported from mpvmux: reads the GitHub "latest release" endpoint and compares its tag with the
/// running version. Nothing is downloaded or installed — the release page opens in the browser.
/// </summary>
internal sealed class UpdateService : IUpdateService
{
    readonly AppMetadata metadata;
    readonly HttpClient http;

    public UpdateService(AppMetadata metadata, HttpClient http)
    {
        this.metadata = metadata;
        this.http = http;
    }

    public async Task<UpdateResponse?> CheckForUpdates()
    {
        if (metadata.APILink.Length == 0) return null;

        // the client is the app-wide one shared with provider traffic, so the User-Agent travels on
        // this request instead of becoming a default header every other call would inherit
        using var request = new HttpRequestMessage(HttpMethod.Get, metadata.APILink);
        request.Headers.UserAgent.ParseAdd(metadata.Title);
        // the feed is tiny: bound the wait and cap the body, a hung or huge feed must not hang the check
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await http.SendAsync(request, cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"The update server returned an unexpected response ({response.StatusCode}).",
                null,
                response.StatusCode);
        }

        var json = await ProviderHttp.ReadTextCappedAsync(response.Content, 256_000, cts.Token).ConfigureAwait(false);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return null; } // a non-JSON feed claims no update instead of failing the check
        using (doc)
        {
        // a non-release JSON (no tag_name) claims no update instead of throwing KeyNotFoundException
        if (!doc.RootElement.TryGetProperty("tag_name", out var tagEl) || tagEl.ValueKind != JsonValueKind.String)
            return null;
        var tag = tagEl.GetString()?.Trim() ?? "";
        var version = TagVersion(tag);
        // an unreadable tag claims no update instead of failing the whole check
        if (version is null || metadata.Version >= version) return null;

        var notes = doc.RootElement.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        var url = doc.RootElement.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : "";
        return new UpdateResponse(tag.TrimStart('v', 'V'), url, notes);
        }
    }

    /// <summary>"v1.2.3" or "1.2.3-rc1": only the leading numbers compare, a suffix never throws.</summary>
    static Version? TagVersion(string tag)
    {
        var text = tag.TrimStart('v', 'V');
        var end = 0;
        while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '.')) end++;
        return Version.TryParse(text[..end].TrimEnd('.'), out var version) ? version : null;
    }
}
