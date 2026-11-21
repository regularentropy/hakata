using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Hakata.Services;
using Markdown.Avalonia.Utils;
using MdEngine = Markdown.Avalonia.Markdown;

namespace Hakata.Views;

/// <summary>Builds the markdown engines the app renders remote text with, all sharing one guarded
/// image resolver: a resource URL found in rendered content is fetched exactly like a validated
/// link, never through the package's default resolver.</summary>
internal static class MarkdownEngines
{
    /// <summary>The resolver every app engine uses; the test suite obtains the same instance here.</summary>
    internal static readonly IPathResolver GuardedPathResolver = new GuardedImageResolver();

    /// <summary>An engine wired the way the update window wires its viewer.</summary>
    internal static MdEngine CreateGuarded()
    {
        var engine = new MdEngine();
        engine.Plugins.PathResolver = GuardedPathResolver;
        return engine;
    }
}

/// <summary>Resolves the image URLs a rendered markdown body contains. Only an absolute http(s)
/// URL that passes the link guard is fetched, and through the same guarded client a link click
/// uses: the socket connects to the exact address that was validated, redirects are not followed,
/// and the body is capped. Every other value — file:, data:, avares:, relative or local paths,
/// non-public hosts — is refused, so rendered content can neither open a local file nor probe
/// this machine or the local network.</summary>
sealed class GuardedImageResolver : IPathResolver
{
    /// <summary>Upper bound for one rendered image; a bigger body is refused, not truncated.</summary>
    internal const int MaxImageBytes = 8 * 1024 * 1024;

    public string? AssetPathRoot { get; set; }
    public IEnumerable<string>? CallerAssemblyNames { get; set; }

    public async Task<Stream?> ResolveImageResource(string relativeOrAbsolutePath)
    {
        if (!LinkGuard.IsFetchableLink(relativeOrAbsolutePath, out var uri))
            throw new InvalidDataException(
                $"the rendered image resource '{relativeOrAbsolutePath}' is not a fetchable http(s) URL");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await LinkGuard.LinkHttp.GetAsync(uri, cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"the image server answered {(int)response.StatusCode} {response.ReasonPhrase}");
        var body = await ProviderHttp.ReadBytesCappedAsync(response.Content, MaxImageBytes, cts.Token).ConfigureAwait(false);
        return new MemoryStream(body, writable: false);
    }
}
