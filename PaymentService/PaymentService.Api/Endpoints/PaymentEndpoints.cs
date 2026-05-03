using PaymentService.Application.Commands;
using Wolverine;

namespace PaymentService.Api.Endpoints;

/// <summary>
/// HTTP endpoint for PaymentService. There's only one — most payment processing flows from the
/// saga (<c>OrderPlacedHandler</c> → <c>ProcessPaymentCommand</c>), not from a direct HTTP call.
/// This endpoint exists for admin/manual processing scenarios (replay, retry-by-hand).
///
/// <para>
/// <b>Two protections stacked:</b>
/// </para>
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
        // Returns 202 Accepted: payment processing involves a gateway call, not a synchronous
        // commitment. The PaymentId in the location header lets the caller poll for status.
        group.MapPost("/process", async (ProcessPaymentCommand command, IMessageBus bus, CancellationToken ct) =>
        {
            var paymentId = await bus.InvokeAsync<Guid>(command, ct);
            return Results.Accepted($"/api/v1/payments/{paymentId}", new { Id = paymentId });
        }).RequireRateLimiting("payments").RequireAuthorization();
    }
}
