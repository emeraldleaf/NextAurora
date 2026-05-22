namespace PaymentService.Infrastructure;

/// <summary>
/// Tuning knobs for <see cref="PaymentRecoveryJob"/>. Defaults are conservative for the
/// portfolio/demo scale — bump down in environments where stuck Pending recovery matters.
/// </summary>
public class PaymentRecoveryOptions
{
    /// <summary>
    /// A payment is considered "stale" if it has been in <c>Pending</c> for this long without
    /// a terminal transition. The gateway timeout + reasonable retry budget should fit inside
    /// this window — set too low and you'll fail legitimately slow payments; too high and the
    /// buyer waits longer than they should before being able to retry.
    /// </summary>
    public TimeSpan StaleThreshold { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How often the job wakes and attempts a sweep. The sweep itself only runs if the
    /// distributed lock can be acquired without waiting (instances that lose the race for the
    /// lock simply skip until next interval).
    /// </summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Logical name of the distributed lock used to serialize sweeps across replicas. SQL
    /// Server's <c>sp_getapplock</c> takes this verbatim.
    /// </summary>
    public string LockName { get; set; } = "payments-recovery";
}
