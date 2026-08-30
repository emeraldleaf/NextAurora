namespace OrderService.Tests.Integration;

/// <summary>
/// Waits for an asynchronous side effect that no tracked session can wait on — a durability-agent
/// timer, a log line written by a listener that rejected a message before any handler ran.
/// Returns <c>false</c> on timeout rather than throwing, so the caller can assert with a message
/// that says what was being waited for.
/// </summary>
internal static class Polling
{
    public static async Task<bool> UntilAsync(Func<Task<bool>> condition, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
                return true;
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }

        return await condition();
    }
}
