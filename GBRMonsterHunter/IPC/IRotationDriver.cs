namespace GBRMonsterHunter.IPC;

internal interface IRotationDriver
{
    string DriverName { get; }
    bool Available { get; }
    string? LastError { get; }
    string StatusDetail { get; }

    void RefreshAvailability();
    bool PrepareForCombat();
    bool ResumeCombat();
}
