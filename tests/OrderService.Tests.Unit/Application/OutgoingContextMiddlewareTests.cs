using System.Diagnostics;
using AwesomeAssertions;
using NextAurora.ServiceDefaults.Messaging;
using Wolverine;

namespace OrderService.Tests.Unit.Application;

public class OutgoingContextMiddlewareTests
{
    [Fact]
    public void Before_WhenActivityHasUserIdBaggage_StampsXUserIdHeader()
    {
        // ARRANGE — The Wolverine-side counterpart to CorrelationIdMiddleware. When a
        // handler publishes an outgoing message, this middleware reads the current
        // Activity's baggage and stamps it onto the envelope's headers. That's how the
        // user/session/correlation context survives the network hop into another service.
        // Here: simulate the saga case where a handler invoked by an authenticated user
        // publishes a downstream command.
        var activity = new Activity("test");
        activity.Start();
        activity.SetBaggage("user.id", "user-123");
        using (activity)
        {
            var envelope = new Envelope();

            // ACT — Invoke the middleware.
            new OutgoingContextMiddleware().Before(envelope);

            // ASSERT — Header stamped with the same value as the baggage. The downstream
            // service's ContextPropagationMiddleware reads X-User-Id from the envelope to
            // restore the baggage; this is the symmetric pair to that read.
            envelope.Headers["X-User-Id"].Should().Be("user-123");
        }
    }

    [Fact]
    public void Before_WhenActivityHasSessionIdBaggage_StampsXSessionIdHeader()
    {
        // ARRANGE — Same shape as the user.id case, but for session.id.
        var activity = new Activity("test");
        activity.Start();
        activity.SetBaggage("session.id", "sess-abc");
        using (activity)
        {
            var envelope = new Envelope();

            // ACT — Invoke the middleware.
            new OutgoingContextMiddleware().Before(envelope);

            // ASSERT — Session header stamped from baggage verbatim.
            envelope.Headers["X-Session-Id"].Should().Be("sess-abc");
        }
    }

    [Fact]
    public void Before_WhenActivityHasNoBaggage_DoesNotAddUserOrSessionHeaders()
    {
        // ARRANGE — Background-service publishing. No user request triggered this work
        // — for example, PaymentRecoveryJob publishing PaymentFailedEvent on a sweep tick.
        // The activity has no user/session baggage. Headers must NOT be added; empty or
        // missing headers on the consumer side cleanly signal "no associated user" and
        // the logger scope omits those keys to avoid null-key pollution.
        var activity = new Activity("test");
        activity.Start();
        using (activity)
        {
            var envelope = new Envelope();

            // ACT — Invoke the middleware.
            new OutgoingContextMiddleware().Before(envelope);

            // ASSERT — Two invariants, one per omitted key:
            //  1) No X-User-Id header — Dictionary<string, object> rejects null keys with
            //     ArgumentNullException at the consumer's ContextPropagationMiddleware when
            //     it tries to add them to the logger scope (Meziantou MA0002). Omitting the
            //     header upstream is what keeps that path clean for unauthenticated work.
            //  2) No X-Session-Id header — same reasoning. Session is client-generated; a
            //     server-initiated publish (sweeper, recovery job) has no session to carry.
            //     Logger scope omits the key entirely rather than logging "SessionId=null".
            envelope.Headers.Should().NotContainKey("X-User-Id");
            envelope.Headers.Should().NotContainKey("X-Session-Id");
        }
    }

    [Fact]
    public void Before_WhenNoActivityCurrent_DoesNotThrow()
    {
        // ARRANGE — Edge case: the middleware runs in a context where Activity.Current
        // is null (e.g. very early startup, or an Aspire dashboard test scenario). It
        // MUST tolerate this gracefully — throwing here would crash whatever code is
        // publishing the message, which is wildly inappropriate for an observability
        // middleware that's supposed to be transparent when telemetry is off.
        var envelope = new Envelope();

        // ACT — Wrap so AwesomeAssertions can confirm no exception is thrown.
        var act = () => new OutgoingContextMiddleware().Before(envelope);

        // ASSERT — No throw. Headers may or may not be set; the contract here is just
        // "don't crash the publish path".
        act.Should().NotThrow();
    }

    [Fact]
    public void Before_WhenActivityHasBothUserAndSession_StampsBothHeaders()
    {
        // ARRANGE — The common case in production: an authenticated browser request
        // (user + session). Both headers must be stamped — partial propagation would
        // make the audit trail useless (e.g. "we know which session sent this but not
        // which user", which doesn't match what the HTTP layer captured).
        var activity = new Activity("test");
        activity.Start();
        activity.SetBaggage("user.id", "user-999");
        activity.SetBaggage("session.id", "sess-xyz");
        using (activity)
        {
            var envelope = new Envelope();

            // ACT — Invoke the middleware.
            new OutgoingContextMiddleware().Before(envelope);

            // ASSERT — Both headers present, both correct.
            envelope.Headers["X-User-Id"].Should().Be("user-999");
            envelope.Headers["X-Session-Id"].Should().Be("sess-xyz");
        }
    }
}
