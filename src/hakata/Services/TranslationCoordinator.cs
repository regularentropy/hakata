using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hakata.Infrastructure;
using Hakata.Models;

namespace Hakata.Services;

/// <summary>A finished translation: the history entry it produced, how long the run took and what it cost.</summary>
public sealed record TranslationOutcome(HistoryEntry Entry, TimeSpan Elapsed, TokenUsage Usage, bool Truncated = false);

/// <summary>Owns the provider, the cancellation token of the current run, streaming coalescing and history.</summary>
public sealed class TranslationCoordinator(ITranslationProvider provider, AppSettings settings, History history)
{
    CancellationTokenSource? cts;
    CancellationTokenSource? auxCts;
    CancellationTokenSource? altCts;
    CancellationTokenSource? insightsCts;
    readonly object pendingLock = new();
    double pendingCost;
    TokenUsage? pendingUsage;
    readonly object idleLock = new();
    int active;
    TaskCompletionSource idle = NewIdle();

    static TaskCompletionSource NewIdle() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // active and idle are one pair and only move under idleLock, so a reader can never observe
    // active>0 together with the previous cycle's already-completed TCS.
    void Enter() { lock (idleLock) { if (active++ == 0) idle = NewIdle(); } }
    void Exit() { lock (idleLock) { if (--active == 0) idle.TrySetResult(); } }

    /// <summary>Completes when every in-flight request has finished (after Cancel they end promptly via the cancellation token).</summary>
    public Task WhenIdleAsync()
    {
        lock (idleLock) return active == 0 ? Task.CompletedTask : idle.Task;
    }

    /// <summary>Like <see cref="WhenIdleAsync"/>, but loops: a request can start between the idle completion
    /// and the caller's next line, and a profile swap must not dispose the graph on that request.</summary>
    public async Task UntilIdleAsync()
    {
        while (Volatile.Read(ref active) > 0) await WhenIdleAsync();
    }

    async Task<T> Track<T>(Task<T> task) { Enter(); try { return await task; } finally { Exit(); } }

    /// <summary>Tracks work that starts its own task: Enter runs before <paramref name="work"/> is invoked,
    /// so the counter cannot miss the start (a task started outside the counter lets idle complete early).</summary>
    internal async Task<T> TrackAsync<T>(Func<Task<T>> work)
    {
        Enter();
        try { return await work(); }
        finally { Exit(); }
    }

    /// <summary>Same for void work, e.g. a blocking Play wrapped in Task.Run.</summary>
    internal async Task TrackAsync(Func<Task> work)
    {
        Enter();
        try { await work(); }
        finally { Exit(); }
    }

    /// <summary>Cancels whatever is running; the next run starts a fresh token.</summary>
    public void Cancel()
    {
        cts?.Cancel();
        auxCts?.Cancel();
        altCts?.Cancel();
        insightsCts?.Cancel();
    }

    /// <summary>Streams the translation: <paramref name="onText"/> gets the whole text so far.</summary>
    public Task<TranslationOutcome> TranslateAsync(string text, string from, string to, QualityTier tier,
        Action<string> onText, Action<string>? onDetected = null) =>
        TrackAsync(() => TranslateCore(text, from, to, tier, onText, onDetected));

    async Task<TranslationOutcome> TranslateCore(string text, string from, string to, QualityTier tier,
        Action<string> onText, Action<string>? onDetected)
    {
        var ct = Begin();
        var started = DateTime.UtcNow;
        if (from == "auto")
        {
            TokenUsage? detected = null;
            var iso = await provider.DetectAsync(text, ct, u => detected = u);
            // the detect request is paid even when it answers nothing: it rides the run's entry
            AddPendingUsage(detected ?? TokenUsage.EstimatedFor(text, ""));
            if (iso != "auto") onDetected?.Invoke(iso);
        }

        var buffer = new StringBuilder();
        var pending = new StringBuilder();
        TokenUsage? reported = null;
        var truncated = false;
        var lastFlush = DateTime.UtcNow;
        await foreach (var chunk in provider.TranslateAsync(text, from, to, settings.Options, tier, ct,
            u => reported = u, cut => truncated = cut))
        {
            // the strip is per character, so nothing hidden survives even in a one-token chunk
            pending.Append(TextRules.StripHidden(chunk));
            // Coalesce model deltas: one callback per ~60ms instead of per token.
            if ((DateTime.UtcNow - lastFlush).TotalMilliseconds < 60) continue;
            buffer.Append(pending);
            pending.Clear();
            onText(buffer.ToString());
            lastFlush = DateTime.UtcNow;
        }
        buffer.Append(pending);
        onText(buffer.ToString());

        // providers that never send a usage chunk still get an honest, labelled estimate
        var usage = reported ?? TokenUsage.EstimatedFor(text, buffer.ToString());
        var entry = NewEntry(from, to, text, buffer.ToString(), usage);
        history.Add(entry);
        return new TranslationOutcome(entry, DateTime.UtcNow - started, usage, truncated);
    }

    public Task<(string Description, List<Alternative> Items)> AlternativesAsync(
        string source, string existing, string from, string to, Action<TokenUsage>? onUsage = null) =>
        Track(provider.AlternativesAsync(source, existing, from, to, BeginExtra(ref altCts), onUsage));

    public Task<List<WordInsight>> InsightsAsync(string source, string output, string from, string to, Action<TokenUsage>? onUsage = null) =>
        Track(provider.InsightsAsync(source, output, from, to, BeginExtra(ref insightsCts), onUsage));

    /// <summary>
    /// Runs beside a finished translation, so it gets its own token: a new run would cancel
    /// a translation the user started meanwhile.
    /// </summary>
    public Task<(string Source, string Translation)> TransliterationAsync(
        string source, string output, string from, string to, Action<TokenUsage>? onUsage = null)
    {
        auxCts?.Cancel();
        auxCts = new CancellationTokenSource();
        return Track(provider.TransliterationAsync(source, output, from, to, auxCts.Token, onUsage));
    }

    /// <summary>Keeps a cancelled run's partial output instead of dropping what the user already sees.</summary>
    public HistoryEntry Commit(string text, string output, string from, string to, TokenUsage? usage = null)
    {
        var actual = usage ?? TokenUsage.EstimatedFor(text, output);
        var entry = NewEntry(from, to, text, output, actual);
        history.Add(entry);
        return entry;
    }

    /// <summary>A speech cost that has no entry yet (a dictation before any run) rides on the next entry.</summary>
    public void AddPendingCost(double cost) { lock (pendingLock) pendingCost += cost; }

    /// <summary>An extra request (detect, or an extra billed to no entry) rides on the next entry with its tokens.</summary>
    public void AddPendingUsage(TokenUsage usage)
    {
        lock (pendingLock)
        {
            pendingUsage = pendingUsage is { } held
                ? held with { PromptTokens = held.PromptTokens + usage.PromptTokens,
                              CompletionTokens = held.CompletionTokens + usage.CompletionTokens,
                              Estimated = held.Estimated || usage.Estimated }
                : usage;
            pendingCost += Pricing.Cost(usage, settings) ?? 0;
        }
    }

    HistoryEntry NewEntry(string from, string to, string text, string output, TokenUsage usage)
    {
        // the detect (and any other unplaced extra) rides the run's own tokens
        double extraCost;
        TokenUsage total;
        lock (pendingLock)
        {
            total = pendingUsage is { } extra
                ? usage with { PromptTokens = usage.PromptTokens + extra.PromptTokens,
                               CompletionTokens = usage.CompletionTokens + extra.CompletionTokens,
                               Estimated = usage.Estimated || extra.Estimated }
                : usage;
            pendingUsage = null;
            extraCost = pendingCost;
            pendingCost = 0;
        }
        var cost = (Pricing.Cost(usage, settings) ?? 0) + extraCost;
        return new HistoryEntry
        {
            From = from,
            To = to,
            Source = text,
            Output = output,
            At = DateTime.UtcNow,
            PromptTokens = total.PromptTokens,
            CompletionTokens = total.CompletionTokens,
            UsageEstimated = total.Estimated,
            InputPricePerMTok = settings.InputPricePerMTok,
            OutputPricePerMTok = settings.OutputPricePerMTok,
            Cost = cost,
        };
    }

    CancellationToken Begin()
    {
        cts?.Cancel();
        cts = new CancellationTokenSource();
        altCts?.Cancel();
        insightsCts?.Cancel();
        return cts.Token;
    }

    /// <summary>A fresh token for one extra request; restarting the same extra cancels its previous one.</summary>
    static CancellationToken BeginExtra(ref CancellationTokenSource? slot)
    {
        slot?.Cancel();
        slot = new CancellationTokenSource();
        return slot.Token;
    }
}
