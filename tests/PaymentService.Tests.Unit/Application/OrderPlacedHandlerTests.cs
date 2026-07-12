using AwesomeAssertions;
using NextAurora.Contracts.Events;
using PaymentService.Features;

namespace PaymentService.Tests.Unit.Application;

public class OrderPlacedHandlerTests
{
    [Fact]
    public void Handle_TranslatesEventIntoProcessPaymentCommand()
    {
        // ARRANGE — Build an OrderPlacedEvent as it would arrive over RabbitMQ.
        // OrderPlacedHandler is a static "Wolverine cascading message" — it returns the
        // next command, and Wolverine handles dispatch. The whole class exists for one
        // reason: ProcessPaymentCommand is also reachable from the HTTP endpoint, so we
        // keep one Handler that owns the work and a thin event-translator on top. That
        // way both code paths (saga + manual admin POST) run identical business logic.
        var orderId = Guid.NewGuid();
        var buyerId = Guid.NewGuid();
        var @event = new OrderPlacedEvent
        {
            OrderId = orderId,
            BuyerId = buyerId,
            PlacedAt = DateTime.UtcNow,
            TotalAmount = 99.99m,
            Currency = "USD",
            Lines = []
        };

        // ACT — Pure function: same input → same output. No async, no I/O.
        var command = OrderPlacedHandler.Handle(@event);

        // ASSERT — Every field that ProcessPayment needs comes from the event verbatim.
        // The Lines collection isn't carried — payment doesn't need per-line detail (the
        // total is what gets charged); if a future field is needed, this test is where
        // the assertion lands.
        command.OrderId.Should().Be(orderId);
        command.BuyerId.Should().Be(buyerId);
        command.Amount.Should().Be(99.99m);
        command.Currency.Should().Be("USD");
    }

    [Fact]
    public void Handle_PreservesCurrencyVerbatim()
    {
        // ARRANGE — Currency isn't normalized in the translator (no .ToUpper, no validation).
        // Validation belongs to ProcessPaymentCommandValidator downstream. This test exists
        // to lock in the "no transformation" contract — if someone later "normalizes" the
        // currency code here, it might silently diverge from what OrderService stored.
        var @event = new OrderPlacedEvent
        {
            OrderId = Guid.NewGuid(),
            BuyerId = Guid.NewGuid(),
            PlacedAt = DateTime.UtcNow,
            TotalAmount = 1m,
            Currency = "eur",
            Lines = []
        };

        // ACT — Call the static translator.
        var command = OrderPlacedHandler.Handle(@event);

        // ASSERT — Lowercase currency flows through verbatim (no normalization).
        command.Currency.Should().Be("eur");
    }
}
