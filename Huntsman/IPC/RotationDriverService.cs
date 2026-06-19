namespace Huntsman.IPC;

internal sealed class RotationDriverService(
    Configuration config,
    RotationSolverRebornIpc rotationSolver,
    WrathComboIpc wrathCombo) : IRotationDriver, IDisposable
{
    private IRotationDriver? activeDriver;

    public string DriverName => SelectedDriver.DriverName;
    public bool Available => activeDriver?.Available ?? false;
    public string? LastError => Available ? null : SelectedDriver.LastError;
    public string StatusDetail => activeDriver != null
        ? $"{activeDriver.DriverName}: {activeDriver.StatusDetail}"
        : $"{SelectedDriver.DriverName}: {SelectedDriver.LastError ?? "missing"}";

    private IRotationDriver SelectedDriver => config.RotationDriver switch
    {
        RotationDriverKind.WrathCombo => wrathCombo,
        _ => rotationSolver,
    };

    public void RefreshAvailability()
    {
        var selected = SelectedDriver;
        selected.RefreshAvailability();
        activeDriver = selected.Available ? selected : null;
    }

    public bool PrepareForCombat()
    {
        RefreshAvailability();
        return activeDriver?.PrepareForCombat() ?? false;
    }

    public bool ResumeCombat() => activeDriver?.ResumeCombat() ?? true;

    public void Dispose() => rotationSolver.Dispose();
}
