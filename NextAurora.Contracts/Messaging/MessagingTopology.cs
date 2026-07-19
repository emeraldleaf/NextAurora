namespace NextAurora.Contracts.Messaging;

/// <summary>
/// The single source of truth for RabbitMQ topology names. Every service's Program.cs wires
/// exchanges, queues, and bindings from these constants (MessagingExchanges / MessagingQueues) — never from inline string literals.
///
/// <para>
/// <b>Why constants and not strings at the call sites:</b> with Wolverine AutoProvision, a
/// typo'd exchange name (<c>"payment_events"</c> instead of <c>"payment-events"</c>) is
/// <i>silently created</i> as a brand-new exchange — the queue binds to it, no error is
/// raised anywhere, and the consumer simply never receives the real events. A shared
/// constant turns that silent starvation into a compile error. See CLAUDE.md.
/// </para>
/// <para>
/// <b>Topology shape:</b> one fanout exchange per event family (publisher-owned), one queue
/// per consumer bound to each exchange (naming: <c>{consumer}-{source}</c>), plus the direct
/// <c>send-notification</c> queue. Each <i>publisher</i> declares its own exchange AND its
/// consumers' queues + bindings, so the full topology for an event family exists before its
/// first publish — a fanout exchange with zero bindings silently discards messages, so
/// consumer-side-only declaration leaves a first-boot loss window (see CLAUDE.md trap; #168).
/// </para>
/// </summary>
public static class MessagingExchanges
{
    public const string OrderEvents = "order-events";
    public const string PaymentEvents = "payment-events";
    public const string ShippingEvents = "shipping-events";
}

/// <summary>Consumer queues — <c>{consumer}-{source}</c>, bound to the source exchange.</summary>
public static class MessagingQueues
{
    // order-events consumers
    public const string PaymentOrders = "payment-orders";
    public const string NotifyOrders = "notify-orders";

    // payment-events consumers
    public const string OrderPayments = "order-payments";
    public const string ShippingPayments = "shipping-payments";
    public const string NotifyPayments = "notify-payments";

    // shipping-events consumers
    public const string OrderShipping = "order-shipping";
    public const string NotifyShipping = "notify-shipping";

    // Direct queue (no exchange) — single consumer, fan-in only.
    public const string SendNotification = "send-notification";
}
