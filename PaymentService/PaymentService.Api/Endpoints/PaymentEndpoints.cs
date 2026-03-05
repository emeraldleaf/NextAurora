using MediatR;
using PaymentService.Application.Commands;

namespace PaymentService.Api.Endpoints;

public static class PaymentEndpoints
{
    public static void MapPaymentEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/payments").WithTags("Payments");

        group.MapPost("/process", async (ProcessPaymentCommand command, IMediator mediator) =>
        {
            var paymentId = await mediator.Send(command);
            return Results.Accepted($"/api/payments/{paymentId}", new { Id = paymentId });
        });
    }
}
