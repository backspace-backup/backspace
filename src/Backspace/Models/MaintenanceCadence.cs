namespace Backspace.Models;

/// <summary>
/// How often a maintenance pass (prune or integrity-check) runs for a
/// given backup. Drives the BackupEditor's Pruning / Check policy combo
/// boxes and maps onto <see cref="Backup.PruneSchedule"/> (resp.
/// CheckSchedule) on save:
///   AfterEachBackup → schedule = null  (engine runs the pass inline)
///   Daily/Weekly/Monthly → schedule = {Frequency=…, TimeOfDay=…}
///                          (OS scheduler fires a standalone task)
///
/// Order is "tightest cadence first" because the UI lists them top-down;
/// AfterEachBackup reads as the "simplest, hands-off" default for small
/// repos, then Daily / Weekly / Monthly graduate users to scheduled
/// off-hours runs as their repos grow. We don't expose Hourly here —
/// nobody prunes or checks more than daily in practice, and surfacing
/// it makes the dropdown noisier without a real use case.
/// </summary>
public enum MaintenanceCadence
{
    AfterEachBackup,
    Daily,
    Weekly,
    Monthly,
}
