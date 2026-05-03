using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NotificationService.Application.Interfaces;
using NotificationService.Domain.Interfaces;
using NotificationService.Infrastructure.Senders;
using NotificationService.Infrastructure.Services;

namespace NotificationService.Infrastructure;

/// <summary>
/// Composition root for NotificationService — the smallest of the lot because the service is
/// stateless. No DbContext, no repository. Two abstractions get bound to their dev-time
/// implementations: <see cref="ConsoleNotificationSender"/> (logs instead of emailing) and
/// <see cref="StubRecipientResolver"/> (placeholder addresses instead of a real lookup).
///
/// <para>
/// Production deployment would re-register both with real implementations
/// (e.g. <c>SendGridNotificationSender</c>, <c>GrpcRecipientResolver</c>) — handlers stay
/// unchanged.
/// </para>
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddNotificationInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<INotificationSender, ConsoleNotificationSender>();
        services.AddScoped<IRecipientResolver, StubRecipientResolver>();

        return services;
    }
}
