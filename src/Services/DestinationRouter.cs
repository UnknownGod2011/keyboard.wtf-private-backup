namespace KeyboardWtf.Services;

using KeyboardWtf.Destinations;
using KeyboardWtf.Models;
using KeyboardWtf.Services.Ai;

public sealed class DestinationRouter
{
    public Task<bool> SendAsync(string destinationName, string text) =>
        SendAsync(destinationName, text, null, CancellationToken.None);

    internal async Task<bool> SendAsync(
        string destinationName,
        string text,
        TextInsertionTarget insertionTarget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text))
        {
            KeyboardWtfState.LastSendResult = "Empty";
            return false;
        }

        var destination = DestinationRegistry.All.FirstOrDefault(d =>
            string.Equals(d.Name, destinationName, StringComparison.OrdinalIgnoreCase));
        if (destination == null)
        {
            KeyboardWtfState.LastSendResult = $"No {destinationName}";
            AppLog.Warning($"Destination not found: {destinationName}");
            return false;
        }

        if (!destination.IsAvailable)
        {
            KeyboardWtfState.LastSendResult = $"{destination.Name} not configured";
            AppLog.Warning($"{destination.Name}: destination unavailable");
            return false;
        }

        var routedText = await ResolveTextAsync(destination, text, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var ok = destination is TypeOutDestination typeOut
            ? await typeOut.SendAsync(
                routedText,
                insertionTarget ?? TextInsertionTarget.Capture(),
                cancellationToken)
            : await destination.SendAsync(routedText);
        KeyboardWtfState.LastSendDestination = destination.Name;
        KeyboardWtfState.LastSendResult = ok ? $"Sent to {destination.Name}" : $"Failed: {destination.Name}";
        return ok;
    }

    public async Task<string> FormatForDestinationAsync(string destinationName, string text)
    {
        var destination = DestinationRegistry.All.FirstOrDefault(d =>
            string.Equals(d.Name, destinationName, StringComparison.OrdinalIgnoreCase));
        return destination == null
            ? text
            : await ResolveTextAsync(destination, text, CancellationToken.None);
    }

    private static async Task<string> ResolveTextAsync(
        IDestination destination,
        string text,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(destination.AiPrompt) || !KeyboardWtfState.UseAi)
            return text;

        var provider = AiProviderRegistry.Current;
        if (provider == null || !provider.IsAvailable)
        {
            AppLog.Warning($"{destination.Name}: AI provider not configured; using raw transcript");
            return text;
        }

        KeyboardWtfState.IsProcessingAi = true;
        try
        {
            var prompt = KeyboardWtfState.GetEffectivePrompt(destination.Name, destination.AiPrompt);
            var formatted = await provider.ReformatAsync(
                text,
                prompt,
                KeyboardWtfState.SelectedLanguage,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(formatted))
                KeyboardWtfState.FormattedOutputs[destination.Name] = formatted;
            return string.IsNullOrWhiteSpace(formatted) ? text : formatted;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warning(ex, $"{destination.Name}: AI formatting failed; using raw transcript");
            return text;
        }
        finally
        {
            KeyboardWtfState.IsProcessingAi = false;
        }
    }
}
