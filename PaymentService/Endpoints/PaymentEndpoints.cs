using PaymentService.Features;
using Wolverine;

namespace PaymentService.Endpoints;

/// <summary>
/// HTTP endpoint for PaymentService. There's only one — most payment processing flows from the
/// saga (<c>OrderPlacedHandler</c> → <c>ProcessPaymentCommand</c>), not from a direct HTTP call.
/// This endpoint exists for admin/manual processing scenarios (replay, retry-by-hand).
///
/// <para>Two protections stacked:</para>
/// <list type="bullet">
///   <item><c>RequireRateLimiting("payments")</c> — fixed-window limiter (configured in Program.cs)
///         to prevent both abuse and the operational mistake of accidentally re-triggering a
///         flood of payments via a debug script.</item>
///   <item><c>RequireAuthorization()</c> — only authenticated users (in practice: admins via
///         token) can hit this. The saga path doesn't go through this endpoint, so it's not
///         affected.</item>
/// </list>
/// </summary>
public static class PaymentEndpoints
{
    public static void MapPaymentEndpoints(this WebApplication app)
    {
        var group = app.MapV1ApiGroup("Payments", "payments");

        // POST /api/v1/payments/process — manual processing trigger.
        // Returns 202 Accepted with PaymentId. The Acceptor handler (ProcessPaymentHandler)
        // validates + persists Payment(Pending) + publishes PaymentProcessingRequested and
        // returns the ID in milliseconds — NO gateway call on this code path. The Stripe
        // call lives in PaymentProcessingRequestedHandler, which runs on a Wolverine worker.
        // The Location header points at /api/v1/payments/{id} for the caller to poll status.
        // See CLAUDE.md "Performance Rules → Long-running work belongs on the message bus".
        group.MapPost("/process", async (ProcessPaymentCommand command, IMessageBus bus, CancellationToken ct) =>
        {
            var paymentId = await bus.InvokeAsync<Guid>(command, ct);
            return Results.Accepted($"/api/v1/payments/{paymentId}", new { Id = paymentId });
        }).RequireRateLimiting("payments").RequireAuthorization();
    }
}
