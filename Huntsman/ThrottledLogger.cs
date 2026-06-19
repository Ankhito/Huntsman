namespace Huntsman;

internal sealed class ThrottledLogger(PluginServices services)
{
    private readonly Dictionary<string, DateTime> lastMessages = new();

    public void Warning(string key, string message, int cooldownMs = 5000)
    {
        var now = DateTime.UtcNow;
        if (lastMessages.TryGetValue(key, out var last) && (now - last).TotalMilliseconds < cooldownMs)
            return;

        lastMessages[key] = now;
        services.Log.Warning(message);
    }

    public void Info(string message) => services.Log.Information(message);
}
