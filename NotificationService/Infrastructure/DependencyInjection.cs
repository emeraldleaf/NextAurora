using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NotificationService.Features;

namespace NotificationService.Infrastructure;

/// <summary>
/// Composition root for NotificationService. One abstraction
/// (<see cref="INotificationSender"/>) bound to its dev-time implementation
/// (<see cref="ConsoleNotificationSender"/>). Production deployment re-registers it with a real
/// adapter (SendGrid, Twilio, SES) — handlers stay unchanged.
///
/// <para>
/// <b>"Ready for the factory, not yet wearing it."</b> Today there's exactly one adapter, so
/// a single <c>AddScoped</c> is the right shape and the handler can take <c>INotificationSender</c>
/// directly. The factory pattern (<c>.NET</c> keyed services) earns its keep once a second
/// adapter ships AND per-call selection becomes a real decision — see CLAUDE.md.
/// </para>
/// <para>
/// <b>Future shape, sketched for when SendGrid/Twilio land:</b>
/// <code>
/// services.AddKeyedScoped&lt;INotificationSender, ConsoleNotificationSender&gt;("console");
/// services.AddKeyedScoped&lt;INotificationSender, SendGridNotificationSender&gt;("email");
/// services.AddKeyedScoped&lt;INotificationSender, TwilioNotificationSender&gt;("sms");
/// </code>
/// And in <c>SendNotificationHandler</c>, resolve per-call via the Channel routing key:
/// <code>
/// var sender = serviceProvider.GetRequiredKeyedService&lt;INotificationSender&gt;(request.Channel);
/// </code>
/// Don't hand-roll an <c>INotificationSenderFactory</c> — <see cref="IServiceProvider"/>'s
/// keyed-services API IS the canonical factory. See CLAUDE.md.
/// </para>
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddNotificationInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<INotificationSender, ConsoleNotificationSender>();
        return services;
    }
}
