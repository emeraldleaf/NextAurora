using System.Security.Claims;
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
        // The 202's Location header points at /api/v1/payments/{id}, but no GET endpoint
        // exists at that route yet (the only GET in this service is the demo /listener route),
        // so status polling is not implemented. Add GET /api/v1/payments/{id:guid} with the
        // buyer-scoped 404 pattern before treating Location as a poll contract.
        // See CLAUDE.md "Performance Rules → Long-running work belongs on the message bus".
        //
        // BuyerId is derived from the JWT NameIdentifier claim, NOT the request body.
        // The HTTP body is ProcessPaymentRequest (no BuyerId field); the internal
        // ProcessPaymentCommand sets BuyerId from the trusted claim. Without this, an
        // authenticated buyer could submit any BuyerId in the body and attribute payments
        // to other buyers. See CLAUDE.md "Security Requirements → Server-controlled fields".
        group.MapPost("/process", async (ProcessPaymentRequest body, HttpContext httpContext, IMessageBus bus, CancellationToken ct) =>
        {
            var sub = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (sub is null || !Guid.TryParse(sub, out var buyerId))
                return Results.Unauthorized();

            var command = new ProcessPaymentCommand(body.OrderId, body.Amount, body.Currency, buyerId);
            var paymentId = await bus.InvokeAsync<Guid>(command, ct);
            return Results.Accepted($"/api/v1/payments/{paymentId}", new { Id = paymentId });
        }).RequireRateLimiting("payments").RequireAuthorization();
    }
}
