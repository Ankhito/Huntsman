using Dalamud.Game.ClientState.Conditions;

namespace Huntsman.Planning;

internal readonly record struct MountActionResult(bool Success, string Status, bool IsFatal = false);

internal sealed class MountService(PluginServices services, Configuration config)
{
    private const string MountRouletteCommand = "/gaction \"Mount Roulette\"";
    private const string DismountCommand = "/gaction \"Dismount\"";

    public bool IsMounted => services.Condition[ConditionFlag.Mounted];
    public string LastMountStatus { get; private set; } = "Mount not attempted.";
    public string LastDismountStatus { get; private set; } = "Dismount not attempted.";

    public void RecordMountSkipped(string reason) => LastMountStatus = $"Mount skipped: {reason}.";

    public bool CanAttemptMount(float distance, out string reason)
    {
        if (!config.AutoMountEnabled)
            return Reject("auto-mount disabled", out reason);
        if (distance < Math.Clamp(config.AutoMountMinDistance, 1f, 500f))
            return Reject($"target is only {distance:F1} yalms away", out reason);
        if (!services.ClientState.IsLoggedIn)
            return Reject("player is not logged in", out reason);
        if (services.Objects.LocalPlayer == null)
            return Reject("local player unavailable", out reason);
        if (IsMounted)
            return Reject("already mounted", out reason);
        if (IsUnsafeForMountAction(out reason))
            return false;

        reason = string.Empty;
        return true;
    }

    public bool CanAttemptDismount(out string reason)
    {
        if (!config.AutoDismountBeforeCombat)
            return Reject("auto-dismount disabled", out reason);
        if (!services.ClientState.IsLoggedIn)
            return Reject("player is not logged in", out reason);
        if (services.Objects.LocalPlayer == null)
            return Reject("local player unavailable", out reason);
        if (!IsMounted)
            return Reject("already dismounted", out reason);
        if (IsLoadingOrLocked(out reason))
            return false;

        reason = string.Empty;
        return true;
    }

    public MountActionResult TryMountRoulette(float distance)
    {
        if (!CanAttemptMount(distance, out var reason))
        {
            LastMountStatus = $"Mount skipped: {reason}.";
            return new MountActionResult(false, LastMountStatus);
        }

        try
        {
            services.Commands.ProcessCommand(MountRouletteCommand);
            LastMountStatus = "Mounting with Mount Roulette.";
            return new MountActionResult(true, LastMountStatus);
        }
        catch (Exception ex)
        {
            LastMountStatus = $"Mount command failed: {ex.GetBaseException().Message}.";
            services.Log.Warning(LastMountStatus);
            return new MountActionResult(false, LastMountStatus);
        }
    }

    public MountActionResult TryDismount()
    {
        if (!config.AutoDismountBeforeCombat && IsMounted)
        {
            LastDismountStatus = "Cannot start combat while mounted; auto-dismount is disabled.";
            return new MountActionResult(false, LastDismountStatus, true);
        }

        if (!CanAttemptDismount(out var reason))
        {
            var success = !IsMounted;
            LastDismountStatus = success ? "Already dismounted." : $"Dismount skipped: {reason}.";
            return new MountActionResult(success, LastDismountStatus, !success);
        }

        try
        {
            services.Commands.ProcessCommand(DismountCommand);
            LastDismountStatus = "Dismounting before target search.";
            return new MountActionResult(true, LastDismountStatus);
        }
        catch (Exception ex)
        {
            LastDismountStatus = $"Dismount command failed: {ex.GetBaseException().Message}.";
            services.Log.Warning(LastDismountStatus);
            return new MountActionResult(false, LastDismountStatus, true);
        }
    }

    private bool IsUnsafeForMountAction(out string reason)
    {
        if (services.Condition[ConditionFlag.InCombat])
            return Reject("in combat", out reason);
        if (services.Condition[ConditionFlag.Casting] || services.Condition[ConditionFlag.Casting87])
            return Reject("casting", out reason);
        if (IsLoadingOrLocked(out reason))
            return true;

        reason = string.Empty;
        return false;
    }

    private bool IsLoadingOrLocked(out string reason)
    {
        if (services.Condition[ConditionFlag.BetweenAreas] || services.Condition[ConditionFlag.BetweenAreas51])
            return Reject("between areas", out reason);
        if (services.Condition[ConditionFlag.Occupied])
            return Reject("occupied", out reason);
        if (services.Condition[ConditionFlag.WatchingCutscene] || services.Condition[ConditionFlag.WatchingCutscene78])
            return Reject("in cutscene", out reason);
        if (services.Condition[ConditionFlag.Unconscious])
            return Reject("dead or incapacitated", out reason);

        reason = string.Empty;
        return false;
    }

    private static bool Reject(string value, out string reason)
    {
        reason = value;
        return false;
    }
}
