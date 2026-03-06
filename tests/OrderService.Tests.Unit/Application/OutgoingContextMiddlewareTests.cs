using System.Diagnostics;
using FluentAssertions;
using NextAurora.ServiceDefaults.Messaging;
using Wolverine;

namespace OrderService.Tests.Unit.Application;

public class OutgoingContextMiddlewareTests
{
    [Fact]
    public void Before_WhenActivityHasUserIdBaggage_StampsXUserIdHeader()
    {
        var activity = new Activity("test");
        activity.Start();
        activity.SetBaggage("user.id", "user-123");
        using (activity)
        {
            var envelope = new Envelope();

            OutgoingContextMiddleware.Before(envelope);

            envelope.Headers["X-User-Id"].Should().Be("user-123");
        }
    }

    [Fact]
    public void Before_WhenActivityHasSessionIdBaggage_StampsXSessionIdHeader()
    {
        var activity = new Activity("test");
        activity.Start();
        activity.SetBaggage("session.id", "sess-abc");
        using (activity)
        {
            var envelope = new Envelope();

            OutgoingContextMiddleware.Before(envelope);

            envelope.Headers["X-Session-Id"].Should().Be("sess-abc");
        }
    }

    [Fact]
    public void Before_WhenActivityHasNoBaggage_DoesNotAddUserOrSessionHeaders()
    {
        var activity = new Activity("test");
        activity.Start();
        using (activity)
        {
            var envelope = new Envelope();

            OutgoingContextMiddleware.Before(envelope);

            envelope.Headers.Should().NotContainKey("X-User-Id");
            envelope.Headers.Should().NotContainKey("X-Session-Id");
        }
    }

    [Fact]
    public void Before_WhenNoActivityCurrent_DoesNotThrow()
    {
        var envelope = new Envelope();

        var act = () => OutgoingContextMiddleware.Before(envelope);

        act.Should().NotThrow();
    }

    [Fact]
    public void Before_WhenActivityHasBothUserAndSession_StampsBothHeaders()
    {
        var activity = new Activity("test");
        activity.Start();
        activity.SetBaggage("user.id", "user-999");
        activity.SetBaggage("session.id", "sess-xyz");
        using (activity)
        {
            var envelope = new Envelope();

            OutgoingContextMiddleware.Before(envelope);

            envelope.Headers["X-User-Id"].Should().Be("user-999");
            envelope.Headers["X-Session-Id"].Should().Be("sess-xyz");
        }
    }
}
