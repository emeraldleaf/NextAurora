using PaymentService.Application.Commands;
using Wolverine;

namespace PaymentService.Api.Endpoints;

public static class PaymentEndpoints
{
    public static void MapPaymentEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/payments").WithTags("Payments");

        group.MapPost("/process", async (ProcessPaymentCommand command, IMessageBus bus) =>
        {
            var paymentId = await bus.InvokeAsync<Guid>(command);
            return Results.Accepted($"/api/payments/{paymentId}", new { Id = paymentId });
        });
    }
}
