using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace Huntsman.Automation;

internal sealed record CombatJobOption(uint ClassJobId, string Label);

internal sealed class CombatJobService(PluginServices services, Configuration config)
{
    private static readonly HashSet<uint> CombatClassJobIds =
    [
        1, 2, 3, 4, 5, 6, 7,
        19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30,
        31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42,
    ];

    private uint pendingJobId;
    private DateTime pendingStartedAt = DateTime.MinValue;

    public string StatusText { get; private set; } = "No combat job switch configured.";

    public IReadOnlyList<CombatJobOption> GetCombatJobs()
    {
        return services.Data.GetExcelSheet<ClassJob>()
            .Where(job => CombatClassJobIds.Contains(job.RowId))
            .Select(job =>
            {
                var abbreviation = job.Abbreviation.ExtractText();
                var name = job.Name.ExtractText();
                var label = string.IsNullOrWhiteSpace(abbreviation)
                    ? name
                    : $"{abbreviation} - {name}";
                return new CombatJobOption(job.RowId, label);
            })
            .Where(option => !string.IsNullOrWhiteSpace(option.Label))
            .OrderBy(option => option.ClassJobId)
            .ToList();
    }

    public string GetSelectedJobLabel()
    {
        if (config.CombatClassJobId == 0)
            return "None";

        return GetCombatJobs().FirstOrDefault(job => job.ClassJobId == config.CombatClassJobId)?.Label
            ?? $"ClassJob {config.CombatClassJobId}";
    }

    public bool EnsureReadyForDropHunt()
    {
        var desiredJobId = config.CombatClassJobId;
        if (desiredJobId == 0)
        {
            StatusText = "No combat job switch configured.";
            return true;
        }

        var currentJobId = services.Objects.LocalPlayer?.ClassJob.RowId ?? 0;
        if (currentJobId == desiredJobId)
        {
            if (pendingJobId == desiredJobId && (DateTime.UtcNow - pendingStartedAt).TotalSeconds < 2)
            {
                StatusText = $"Waiting for {GetSelectedJobLabel()} to settle.";
                return false;
            }

            pendingJobId = 0;
            pendingStartedAt = DateTime.MinValue;
            StatusText = $"Ready on {GetSelectedJobLabel()}.";
            return true;
        }

        if (IsBlocked())
        {
            StatusText = $"Waiting to switch to {GetSelectedJobLabel()}.";
            return false;
        }

        if (pendingJobId == desiredJobId)
        {
            StatusText = $"Switching to {GetSelectedJobLabel()}.";
            return false;
        }

        if (!TryEquipFirstGearsetForJob(desiredJobId, out var gearsetIndex))
        {
            StatusText = $"No gearset found for {GetSelectedJobLabel()}.";
            return false;
        }

        pendingJobId = desiredJobId;
        pendingStartedAt = DateTime.UtcNow;
        StatusText = $"Equipped gearset {gearsetIndex + 1} for {GetSelectedJobLabel()}.";
        return false;
    }

    private bool IsBlocked()
    {
        return services.Condition[ConditionFlag.BetweenAreas]
            || services.Condition[ConditionFlag.BetweenAreas51]
            || services.Condition[ConditionFlag.Gathering]
            || services.Condition[ConditionFlag.ExecutingGatheringAction]
            || services.Condition[ConditionFlag.Crafting]
            || services.Condition[ConditionFlag.PreparingToCraft]
            || services.Condition[ConditionFlag.ExecutingCraftingAction]
            || services.Condition[ConditionFlag.Casting]
            || services.Condition[ConditionFlag.Casting87];
    }

    private static unsafe bool TryEquipFirstGearsetForJob(uint jobId, out int gearsetIndex)
    {
        gearsetIndex = -1;
        var gearsetModule = RaptureGearsetModule.Instance();
        if (gearsetModule == null)
            return false;

        for (var i = 0; i < 100; i++)
        {
            if (gearsetModule->Entries[i].ClassJob != jobId)
                continue;

            gearsetModule->EquipGearset(i);
            gearsetIndex = i;
            return true;
        }

        return false;
    }
}
