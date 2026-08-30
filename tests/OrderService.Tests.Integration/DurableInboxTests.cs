using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NextAurora.Contracts.Events;
using NextAurora.Contracts.Messaging;
using OrderService.Domain;
using OrderService.Infrastructure.Data;
using RabbitMQ.Client;
using Wolverine.Persistence.Durability;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace OrderService.Tests.Integration;

/// <summary>
/// Proves the durable inbox does what the first VPS deploy showed it doing: when the broker hands
/// the same envelope back a second time, <c>wolverine.incoming_envelopes</c> rejects it before any
/// handler runs. Until this test, that guarantee rested on one production log line and on
/// <c>UseDurableInboxOnAllListeners()</c> being present in <c>Program.cs</c>; nothing pinned the
/// two together.
///
/// <para>
/// <b>Why this needs the real broker:</b> the inbox guards <i>listeners</i>. With transports
/// stubbed, <c>PublishMessageAndWaitAsync</c> hands a message straight to the local pipeline and
/// the inbox is never consulted — the idempotency the saga tests see there comes from the handler's
/// own status guard, a different layer. A redelivery has to arrive over AMQP.
/// </para>
/// <para>
/// <b>Why publish raw AMQP instead of going through Wolverine:</b> a redelivery carries the SAME
/// envelope id, and Wolverine's bus mints a fresh id on every publish. The test builds the message
/// with Wolverine's own routing, serializer and <see cref="RabbitMqEnvelopeMapper"/>, so the bytes
/// and properties on the wire are exactly what the publishing service would emit — then sends them
/// twice. From the listener's side this is indistinguishable from the broker redelivering after a
/// lost ack.
/// </para>
/// </summary>
public sealed class DurableInboxTests(OrderApiRabbitFactory factory) : IClassFixture<OrderApiRabbitFactory>
{
    private readonly OrderApiRabbitFactory _factory = factory;

    [Fact]
    public async Task Redelivered_envelope_is_rejected_by_the_durable_inbox_before_the_handler()
    {
        // ARRANGE — A Placed order, and a PaymentCompletedEvent for it built as PaymentService
        // would build it: routed to the order-payments queue OrderService listens on, serialized
        // by the app's serializer, AMQP properties (MessageId = envelope id, Type = message type)
        // written by the same mapper the RabbitMQ transport uses.
        var orderId = await SeedPlacedOrderAsync();
        var runtime = _factory.Services.GetRequiredService<IWolverineRuntime>();
        var host = _factory.Services.GetRequiredService<IHost>();
        var paymentCompleted = new PaymentCompletedEvent
        {
            PaymentId = Guid.NewGuid(),
            OrderId = orderId,
            BuyerId = TestAuthHandler.BuyerId,
            Amount = 25m,
            Provider = "stripe-test",
            CompletedAt = DateTime.UtcNow,
        };

        var queueUri = new Uri($"rabbitmq://queue/{MessagingQueues.OrderPayments}");
        var envelope = runtime.RoutingFor(typeof(PaymentCompletedEvent)).RouteToDestination(paymentCompleted, queueUri, null);
        var body = envelope.Data ?? envelope.Serializer!.Write(envelope);
        var properties = new BasicProperties();
        var listener = runtime.Endpoints.EndpointFor(queueUri)
            ?? throw new InvalidOperationException($"OrderService declares no endpoint for {queueUri} — is the ListenToRabbitQueue line still in Program.cs?");
        new RabbitMqEnvelopeMapper(listener, runtime).MapEnvelopeToOutgoing(envelope, properties);

        // ACT 1 — First delivery: the normal path. The durable listener stores the envelope in
        // wolverine.incoming_envelopes, the handler runs, the order moves to Paid.
        await host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(TimeSpan.FromSeconds(60))
            .WaitForMessageToBeReceivedAt<PaymentCompletedEvent>(host)
            .ExecuteAndWaitAsync(_ => PublishRawAsync(properties, body));
        var paidInTime = await Polling.UntilAsync(async () => await GetOrderStatusAsync(orderId) == OrderStatus.Paid, TimeSpan.FromSeconds(15));

        // ASSERT 1 — Three preconditions, each guarding the second half against a false pass:
        //  1) The first delivery reached the handler. If it didn't, a "rejection" of the second
        //     one would prove nothing.
        //  2) The inbox holds the handled envelope's id. That row is what makes a redelivery
        //     detectable; without it the second delivery would simply be handled again.
        //  3) Nothing matching the rejection matcher has been logged yet. If it had, the matcher
        //     is too loose and ASSERT 2 would pass for the wrong reason.
        paidInTime.Should().BeTrue("the first delivery must reach the handler and move the order to Paid");
        (await runtime.Storage.Inbox.ExistsAsync(envelope, CancellationToken.None)).Should().BeTrue(
            "the durable inbox must hold the handled envelope's id");
        _factory.Logs.Entries.Should().NotContain(e => IsInboxRejection(e, envelope.Id),
            "the first delivery is not a duplicate; if this fires the matcher is wrong");

        // ACT 2 — The redelivery: the same bytes, the same MessageId, onto the same queue.
        await PublishRawAsync(properties, body);
        var rejectedInTime = await Polling.UntilAsync(
            () => Task.FromResult(_factory.Logs.Entries.Any(e => IsInboxRejection(e, envelope.Id))),
            TimeSpan.FromSeconds(20));

        // ASSERT 2 — The listener rejected the envelope id against the inbox table and logged the
        // DuplicateIncomingEnvelopeException — the line the first VPS deploy produced — and the
        // order is still exactly where the first delivery left it.
        rejectedInTime.Should().BeTrue(
            "Wolverine's durable receiver must reject the redelivered envelope before the handler. Wolverine log tail: {0}",
            string.Join(" | ", _factory.Logs.Entries
                .Where(e => e.Category.StartsWith("Wolverine", StringComparison.Ordinal))
                .TakeLast(12)
                .Select(e => $"[{e.Level}] {e.Message}")));
        (await GetOrderStatusAsync(orderId)).Should().Be(OrderStatus.Paid);
    }

    private static bool IsInboxRejection(LogEntry entry, Guid envelopeId) =>
        entry.Exception is DuplicateIncomingEnvelopeException
        || entry.Message.Contains(nameof(DuplicateIncomingEnvelopeException), StringComparison.Ordinal)
        || (entry.Message.Contains("uplicate", StringComparison.Ordinal)
            && entry.Message.Contains(envelopeId.ToString(), StringComparison.OrdinalIgnoreCase));

    private async Task PublishRawAsync(BasicProperties properties, byte[] body)
    {
        var connectionFactory = new ConnectionFactory { Uri = new Uri(_factory.AmqpConnectionString) };
        await using var connection = await connectionFactory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        // Default exchange + routing key = queue name delivers straight to the queue the app
        // declared through AutoProvision at startup.
        await channel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: MessagingQueues.OrderPayments,
            mandatory: false,
            basicProperties: properties,
            body: body);
    }

    private async Task<Guid> SeedPlacedOrderAsync()
    {
        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var line = OrderLine.Create(Guid.NewGuid(), "Inbox Test Product", quantity: 1, unitPrice: 25m);
        var order = Order.Create(TestAuthHandler.BuyerId, "USD", [line]);
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order.Id;
    }

    private async Task<OrderStatus> GetOrderStatusAsync(Guid orderId)
    {
        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        return await db.Orders.AsNoTracking().Where(o => o.Id == orderId).Select(o => o.Status).SingleAsync();
    }
}
