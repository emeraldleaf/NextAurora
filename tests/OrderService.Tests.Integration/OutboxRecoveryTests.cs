using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NextAurora.Contracts.Events;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Xunit;

namespace OrderService.Tests.Integration;

/// <summary>
/// The crash half of the transactional-outbox guarantee. <see cref="OrderSagaTests"/> proves the
/// commit half (a rolled-back transaction discards the staged envelope, nothing leaks). This proves
/// the other direction: an envelope that WAS committed to <c>wolverine.outgoing_envelopes</c> but
/// never dispatched — the process died between the commit and the sender's turn — is found and
/// forwarded by Wolverine's durability agent under the app's own configuration
/// (<c>PersistMessagesWithSqlServer</c>, <c>UseDurableOutboxOnAllSendingEndpoints</c>, default
/// <c>DurabilityMode.Balanced</c>). Without this, "the outbox guarantees at-least-once delivery"
/// rests on Wolverine's documentation rather than on this repo's wiring.
///
/// <para>
/// <b>How the crash is simulated:</b> a crashed node's envelopes end up with <c>OwnerId = 0</c>
/// (<see cref="TransportConstants.AnyNode"/>) once its ownership is released — committed,
/// undispatched, claimed by nobody. The test writes an envelope into the outbox table in exactly
/// that state, using the app's own routing and serializer so the row is indistinguishable from
/// one <c>PlaceOrderHandler</c> left behind, then does nothing but wait for the recovery job.
/// </para>
/// </summary>
public sealed class OutboxRecoveryTests(OrderApiFactory factory) : IClassFixture<OrderApiFactory>
{
    private readonly OrderApiFactory _factory = factory;

    [Fact]
    public async Task DurabilityAgent_forwards_the_orphaned_envelope_when_no_node_owns_it()
    {
        // ARRANGE — Build the envelope as PlaceOrderHandler's publish would: routed by the app's
        // subscription rules (OrderPlacedEvent → the order-events exchange, a stub in this
        // fixture) and serialized by the app's default serializer. RouteForPublish returns one
        // envelope per subscriber; OrderPlacedEvent has exactly one.
        var runtime = _factory.Services.GetRequiredService<IWolverineRuntime>();
        var host = _factory.Services.GetRequiredService<IHost>();
        var orderPlaced = new OrderPlacedEvent
        {
            OrderId = Guid.NewGuid(),
            BuyerId = TestAuthHandler.BuyerId,
            PlacedAt = DateTime.UtcNow,
            TotalAmount = 25m,
            Currency = "USD",
            Lines = [new OrderLineContract { ProductId = Guid.NewGuid(), ProductName = "Recovery Test Product", Quantity = 1, UnitPrice = 25m }],
        };
        var envelope = runtime.RoutingFor(typeof(OrderPlacedEvent)).RouteForPublish(orderPlaced, null).Single();
        envelope.Data ??= envelope.Serializer!.Write(envelope);

        // ACT — Inside a tracked session, so the stub sending endpoint's record of the eventual
        // send is captured. The session must be open BEFORE the row is stored: the recovery job
        // could run between the store and the session start, and the send would go unrecorded.
        var recoveredInTime = false;
        async Task StoreOrphanAndWaitForRecoveryAsync(IMessageContext _)
        {
            // Persist the orphan. Owner 0 = no live node claims it.
            await runtime.Storage.Outbox.StoreOutgoingAsync(envelope, TransportConstants.AnyNode);
            (await runtime.Storage.Admin.AllOutgoingAsync()).Should().Contain(e => e.Id == envelope.Id,
                "the orphan must be in the outbox table before recovery can find it");

            // Then wait. Balanced mode runs the recovery job after ScheduledJobFirstExecution
            // (≈4 s) and every ScheduledJobPollingTime (5 s) after that. Wolverine deletes an
            // outgoing row only after the send succeeded, so "row gone" is the completion signal.
            recoveredInTime = await Polling.UntilAsync(
                async () => !(await runtime.Storage.Admin.AllOutgoingAsync()).Any(e => e.Id == envelope.Id),
                TimeSpan.FromSeconds(60));
        }

        var session = await host.TrackActivity()
            .Timeout(TimeSpan.FromSeconds(90))
            .ExecuteAndWaitAsync(StoreOrphanAndWaitForRecoveryAsync);

        // ASSERT — Four invariants:
        //  1) The recovery happened within the window. A false here with the row still present
        //     means the durability agent never picked the orphan up — the at-least-once claim
        //     would be false for a crashed node.
        //  2) The row is gone because it was SENT, not parked: the stub endpoint recorded a send
        //     carrying the same envelope id. Same id is what distinguishes "recovered the orphan"
        //     from "some other publish happened to go by."
        //  3-4) It was not discarded or dead-lettered — those are the two other ways a row leaves
        //     the outbox, and either would be a silent loss dressed up as a clean table.
        recoveredInTime.Should().BeTrue("the durability agent must forward an orphaned outgoing envelope");
        session.Sent.Envelopes().Should().Contain(e => e.Id == envelope.Id,
            "the recovered envelope must have gone through the sending endpoint");
        session.Discarded.Envelopes().Should().NotContain(e => e.Id == envelope.Id);
        session.MovedToErrorQueue.Envelopes().Should().NotContain(e => e.Id == envelope.Id);
    }
}
