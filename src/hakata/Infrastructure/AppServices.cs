using System;
using System.Net.Http;
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
        services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
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
}
