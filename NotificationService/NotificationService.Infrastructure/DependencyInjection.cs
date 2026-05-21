using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NotificationService.Application.Interfaces;
using NotificationService.Infrastructure.Senders;

namespace NotificationService.Infrastructure;

/// <summary>
/// Composition root for NotificationService — the smallest of the lot because the service is
/// stateless. One abstraction (<see cref="INotificationSender"/>) bound to its dev-time
/// implementation (<see cref="ConsoleNotificationSender"/>, which logs instead of dispatching).
/// Production deployment re-registers it with a real adapter (SendGrid, Twilio, SES) — handlers
/// stay unchanged.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddNotificationInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<INotificationSender, ConsoleNotificationSender>();
        return services;
    }
}
