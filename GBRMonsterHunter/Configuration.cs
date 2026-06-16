using Dalamud.Configuration;
using Dalamud.Plugin;

namespace GBRMonsterHunter;

internal enum RotationDriverKind
{
    RotationSolverReborn,
    WrathCombo,
}

[Serializable]
internal sealed class Configuration : IPluginConfiguration
{
    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public int Version { get; set; } = 1;
    public string TeleporterCommandTemplate { get; set; } = "/tp {0}";
    public uint CombatClassJobId { get; set; }
    public RotationDriverKind RotationDriver { get; set; } = RotationDriverKind.RotationSolverReborn;
    public float ArrivalDistance { get; set; } = 12f;
    public float TargetSearchRadius { get; set; } = 35f;
    public double NavigationTimeoutSeconds { get; set; } = 180.0;
    public double TargetSearchTimeoutSeconds { get; set; } = 30.0;
    public bool AutoMountEnabled { get; set; } = true;
    public bool AutoDismountBeforeCombat { get; set; } = true;
    public float AutoMountMinDistance { get; set; } = 35f;

    public void Initialize(IDalamudPluginInterface pi) => pluginInterface = pi;

    public void Save() => pluginInterface?.SavePluginConfig(this);
}
