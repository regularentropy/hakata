using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Hakata.Models;
using Hakata.Services;
using Hakata.ViewModels;
using Hakata.Views;

namespace Hakata.Infrastructure;

/// <summary>Composition root: the only place the object graph is registered.</summary>
public static class AppServices
{
    public static IServiceCollection AddHakata(this IServiceCollection services)
    {
        var settings = SettingsService.Load();
        var history = new History();
        history.Load();

        services.AddSingleton(settings);
        services.AddSingleton(history);
        // no redirects: a 302 must not silently re-POST the chat body somewhere else; EnsureOk reports it.
        // Registered via factory so the DI container disposes the client with the graph (hot profile swap).
        // The connect callback is the credential gate: a request carrying an
        // Authorization header only leaves for https to a public host — every route on this client
        // (chat, models, speech, update check) inherits that.
        services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = ConnectGuardedAsync,
        })
        {
            Timeout = TimeSpan.FromMinutes(5),
        });
        services.AddSingleton(new SettingsService(settings));
        services.AddSingleton<IAudioRecorder, MicrophoneRecorder>();
        services.AddSingleton<ISpeechPlayer, SpeechPlayer>();
        services.AddSingleton(sp => new OpenAiCompatibleProvider(sp.GetRequiredService<HttpClient>(), settings));
        services.AddSingleton<ITranslationProvider>(sp => sp.GetRequiredService<OpenAiCompatibleProvider>());
        services.AddSingleton<ISpeechProvider>(sp => sp.GetRequiredService<OpenAiCompatibleProvider>());
        services.AddSingleton<IModelCatalog>(sp => sp.GetRequiredService<OpenAiCompatibleProvider>());
        services.AddSingleton(sp => new TranslationCoordinator(sp.GetRequiredService<ITranslationProvider>(), settings, history));

        services.AddTransient<TranslateViewModel>();
        // History stays the singleton above: resolving it must not build a second TranslateViewModel
        // graph (theme re-apply, auto-price probe, history.Changed leak) on every settings open
        services.AddTransient(sp => new SettingsViewModel(
            sp.GetRequiredService<SettingsService>(), sp.GetRequiredService<HttpClient>()));

        services.AddTransient<MainWindow>();
        services.AddTransient<SettingsWindow>();
        return services;
    }

    public static ServiceProvider Create() => new ServiceCollection().AddHakata().BuildServiceProvider();

    /// <summary>Socket-level credential guard on the main client: the host is
    /// resolved once and judged on exactly the returned addresses (LinkGuard.MaySendCredentials);
    /// a request with an Authorization header is refused unless it is https and every resolved
    /// address is public. The socket then connects to that judged address, so a DNS answer that
    /// changes between the check and the connect cannot slip a credential to a local host. A
    /// headerless request (no key configured) connects as before, so keyless local servers
    /// (Ollama/LM Studio on http://localhost) keep working.</summary>
    static async ValueTask<Stream> ConnectGuardedAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var request = context.InitialRequestMessage;
        var host = context.DnsEndPoint.Host;
        var addresses = await Dns.GetHostAddressesAsync(host, ct);
        if (!LinkGuard.MaySendCredentials(request, addresses))
            throw new HttpRequestException(
                $"refusing to send credentials to {request.RequestUri}: the destination must be https and resolve to public addresses only");
        Exception? last = null;
        foreach (var address in addresses)
        {
            var mapped = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            var socket = new Socket(mapped.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(mapped, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception e) { last = e; socket.Dispose(); }
        }
        throw last ?? new HttpRequestException($"no address resolved for {host}");
    }
}
