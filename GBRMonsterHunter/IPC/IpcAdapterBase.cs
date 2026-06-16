namespace GBRMonsterHunter.IPC;

internal abstract class IpcAdapterBase(PluginServices services, ThrottledLogger logger)
{
    protected PluginServices Services { get; } = services;
    protected ThrottledLogger Logger { get; } = logger;

    public bool Available { get; protected set; }
    public string? LastError { get; protected set; }

    public abstract void RefreshAvailability();

    protected void SetAvailability(string missingMessage, params Func<bool>[] checks)
    {
        try
        {
            Available = checks.Length == 0 || checks.All(check => check());
            LastError = Available ? null : missingMessage;
        }
        catch (Exception ex)
        {
            Available = false;
            LastError = ex.GetBaseException().Message;
        }
    }

    protected bool TryCall<T>(string operation, Func<T> call, out T? value)
    {
        try
        {
            value = call();
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            value = default;
            LastError = ex.GetBaseException().Message;
            Logger.Warning($"{GetType().Name}:{operation}", $"{GetType().Name} {operation} failed: {LastError}");
            return false;
        }
    }

    protected bool TryAction(string operation, Action action)
    {
        try
        {
            action();
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.GetBaseException().Message;
            Logger.Warning($"{GetType().Name}:{operation}", $"{GetType().Name} {operation} failed: {LastError}");
            return false;
        }
    }
}
