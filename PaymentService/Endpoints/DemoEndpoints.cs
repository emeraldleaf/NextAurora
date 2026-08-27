using NextAurora.Contracts.Messaging;
using Wolverine.Runtime;
using NextAurora.ServiceDefaults;

namespace PaymentService.Endpoints;

/// <summary>
/// The demo kill switch (#208): pause and revive PaymentService's RabbitMQ listener so a
/// viewer can watch the durability guarantees live — kill the consumer, place an order,
/// see the saga hold at awaiting-payment while <c>OrderPlacedEvent</c> sits in the durable
/// <c>payment-orders</c> queue, revive, watch it drain and the saga complete. Nothing is
/// simulated: the pause stops the real Wolverine listening agent, and the broker genuinely
/// holds the message until the consumer acks (see CLAUDE.md "Durability ≠ replay").
///
/// <para>Guardrails, because this is a public, state-changing control on a demo box:</para>
/// <list type="bullet">
///   <item>Mapped ONLY when <c>DemoMode=true</c> — absent (404) in any non-demo deployment.</item>
///   <item><c>PauseAsync(AutoReviveAfter)</c> — Wolverine restarts the listener itself after
///         the pause window, so one visitor can't leave the demo dead for the next.</item>
///   <item><c>RequireAuthorization()</c> + the shared <c>"payments"</c> rate-limit policy —
///         no anonymous or hammering clients.</item>
/// </list>
/// </summary>
public static class DemoEndpoints
{
    /// <summary>One visitor's kill lasts at most this long before Wolverine self-revives.</summary>
    private static readonly TimeSpan AutoReviveAfter = TimeSpan.FromSeconds(60);

    private static readonly Uri ListenerUri = new($"rabbitmq://queue/{MessagingQueues.PaymentOrders}");

    public static void MapDemoEndpoints(this WebApplication app)
    {
        // The gate: no DemoMode, no endpoints — a prod deployment without the flag exposes
        // nothing (requests 404, indistinguishable from any unknown route).
        if (!app.Configuration.GetValue<bool>("DemoMode"))
            return;

        var group = app.MapV1ApiGroup("Demo", "demo");

        group.MapGet("/listener", (IWolverineRuntime runtime) =>
            Results.Ok(CurrentStatus(runtime))).RequireAuthorization();

        group.MapPost("/listener/pause", async (IWolverineRuntime runtime) =>
        {
            var agent = runtime.Endpoints.FindListeningAgent(ListenerUri);
            if (agent is null)
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: "Listener not available.");

            // PauseAsync schedules the restart itself — the auto-revive guardrail is
            // Wolverine's own mechanism, not a timer we could forget to fire.
            await agent.PauseAsync(AutoReviveAfter);
            return Results.Ok(CurrentStatus(runtime));
        }).RequireRateLimiting("payments").RequireAuthorization().RequireTurnstile();

        group.MapPost("/listener/resume", async (IWolverineRuntime runtime) =>
        {
            var agent = runtime.Endpoints.FindListeningAgent(ListenerUri);
            if (agent is null)
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: "Listener not available.");

            await agent.StartAsync();
            return Results.Ok(CurrentStatus(runtime));
        }).RequireRateLimiting("payments").RequireAuthorization().RequireTurnstile();
    }

    private static DemoListenerStatus CurrentStatus(IWolverineRuntime runtime)
    {
        var agent = runtime.Endpoints.FindListeningAgent(ListenerUri);
        // Stubbed transports (integration tests) have no rabbitmq listener — report that
        // honestly instead of failing the status probe.
        var listening = agent?.Status.ToString() ?? "Unavailable";
        return new DemoListenerStatus(listening, (int)AutoReviveAfter.TotalSeconds);
    }
}

/// <param name="Status">Wolverine's ListeningStatus name (Accepting, Stopped, TooBusy) or "Unavailable".</param>
/// <param name="AutoReviveSeconds">Upper bound a pause can last before self-revive.</param>
public record DemoListenerStatus(string Status, int AutoReviveSeconds);
