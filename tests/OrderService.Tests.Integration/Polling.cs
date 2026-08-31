namespace OrderService.Tests.Integration;

/// <summary>
/// Waits for an asynchronous side effect that no tracked session can wait on — a durability-agent
/// timer, a log line written by a listener that rejected a message before any handler ran.
/// Returns <c>false</c> on timeout rather than throwing, so the caller can assert with a message
/// that says what was being waited for.
///
/// <para>
/// The condition receives the <see cref="CancellationToken"/> so a cancel lands during a pending
/// probe, not just between probes: an in-flight DB read would otherwise hold the loop past the
/// cancellation for the length of that read.
/// </para>
/// </summary>
internal static class Polling
{
    public static async Task<bool> UntilAsync(Func<CancellationToken, Task<bool>> condition, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition(ct))
                return true;
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }

        return await condition(ct);
    }
}
