namespace KeyboardWtf.Services.Ai;

using System.Collections.Generic;
using KeyboardWtf.Models;
using KeyboardWtf.Services.PaidStaging;

/// <summary>
/// Singleton registry of every AI provider the app knows about, implemented or not.
/// <see cref="Current"/> returns the one currently selected by <see cref="KeyboardWtfState.SelectedAiProvider"/>,
/// falling back to Claude if the selection somehow ends up unregistered.
/// </summary>
internal static class AiProviderRegistry
{
    private static readonly Dictionary<AiProvider, IAiProvider> _providers = new();
    private static IAiProvider _fallback;

    /// <summary>
    /// Called once from app startup after <see cref="ClaudeApiService"/> is up.
    /// </summary>
    public static void Initialize(ClaudeApiService claudeService, PaidAccountService paidAccount = null)
    {
        _providers.Clear();

        var claude = new ClaudeProvider(claudeService);
        _providers[AiProvider.Claude] = claude;
        _fallback = claude;

        _providers[AiProvider.OpenAi] = new OpenAiProvider();
        _providers[AiProvider.Gemini] = paidAccount?.Enabled == true
            ? new PaidCloudAiProvider(paidAccount)
            : new GeminiProvider();
        _providers[AiProvider.DeepSeek]   = new ComingSoonProvider(AiProvider.DeepSeek,   "DeepSeek");
        _providers[AiProvider.Perplexity] = new ComingSoonProvider(AiProvider.Perplexity, "Perplexity");

        AppLog.Info($"AI providers registered: {_providers.Count} total, {CountImplemented()} implemented");
    }

    public static IReadOnlyCollection<IAiProvider> All => _providers.Values;

    public static IAiProvider Get(AiProvider id) =>
        _providers.TryGetValue(id, out var p) ? p : _fallback;

    public static IAiProvider Current => Get(KeyboardWtfState.SelectedAiProvider);

    private static int CountImplemented()
    {
        var n = 0;
        foreach (var p in _providers.Values) if (p.IsImplemented) n++;
        return n;
    }
}
