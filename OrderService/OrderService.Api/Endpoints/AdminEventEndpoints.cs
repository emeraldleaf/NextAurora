using System.Diagnostics;
using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using NextAurora.ServiceDefaults.Filters;
using OrderService.Infrastructure.Data;
using OrderService.Infrastructure.EventLog;
using EventLogAlias = OrderService.Infrastructure.EventLog.EventLogEntry;

namespace OrderService.Api.Endpoints;

public static class AdminEventEndpoints
{
    public static void MapAdminEventEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/events")
            .WithTags("Admin Events")
            .AddEndpointFilter<AdminKeyEndpointFilter>();

        group.MapGet("/", async (
            string? correlationId,
            string? eventType,
            string? entityId,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int page,
            int pageSize,
            OrderDbContext db) =>
        {
            page = page < 1 ? 1 : page;
            pageSize = pageSize is < 1 or > 100 ? 50 : pageSize;

            var query = db.EventLogs.AsNoTracking();

            if (!string.IsNullOrEmpty(correlationId)) query = query.Where(e => e.CorrelationId == correlationId);
            if (!string.IsNullOrEmpty(eventType)) query = query.Where(e => e.EventType == eventType);
            if (!string.IsNullOrEmpty(entityId)) query = query.Where(e => e.EntityId == entityId);
            if (from.HasValue) query = query.Where(e => e.OccurredAt >= from.Value);
            if (to.HasValue) query = query.Where(e => e.OccurredAt <= to.Value);

            var total = await query.CountAsync();
            var items = await query.OrderBy(e => e.OccurredAt)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(e => new EventLogDto(e.Id, e.EventType, e.Topic, e.CorrelationId, e.EntityId, e.OccurredAt, e.PublishedAt, e.IsReplay, e.OriginalEventId))
                .ToListAsync();

            return Results.Ok(new { Total = total, Page = page, PageSize = pageSize, Items = items });
        });

        group.MapPost("/{eventId:guid}/replay", async (Guid eventId, OrderDbContext db, ServiceBusClient sbClient) =>
        {
            var entry = await db.EventLogs.FindAsync(eventId);
            if (entry is null) return Results.NotFound();

            var correlationId = Activity.Current?.GetBaggageItem("correlation.id")
                ?? entry.CorrelationId;

            var message = new ServiceBusMessage(entry.Payload)
            {
                ContentType = "application/json",
                Subject = entry.EventType,
                CorrelationId = correlationId
            };

            if (correlationId is not null)
                message.ApplicationProperties["X-Correlation-Id"] = correlationId;

            message.ApplicationProperties["X-Replay"] = "true";
            message.ApplicationProperties["X-Replay-Of"] = eventId.ToString();

            await using var sender = sbClient.CreateSender(entry.Topic);
            await sender.SendMessageAsync(message);

            var replayEntry = EventLogAlias.CreateReplay(
                entry.EventType, entry.Topic, entry.Payload,
                correlationId, entry.EntityId, eventId);
            replayEntry.SetPublished();
            db.EventLogs.Add(replayEntry);
            await db.SaveChangesAsync();

            return Results.Accepted($"/admin/events/{replayEntry.Id}", new { ReplayEventLogId = replayEntry.Id });
        });

        group.MapPost("/replay-chain", async (string correlationId, OrderDbContext db, ServiceBusClient sbClient) =>
        {
            if (string.IsNullOrEmpty(correlationId))
                return Results.BadRequest("correlationId is required");

            var events = await db.EventLogs
                .Where(e => e.CorrelationId == correlationId && !e.IsReplay)
                .OrderBy(e => e.OccurredAt)
                .ToListAsync();

            if (events.Count == 0) return Results.NotFound();

            foreach (var entry in events)
            {
                var message = new ServiceBusMessage(entry.Payload)
                {
                    ContentType = "application/json",
                    Subject = entry.EventType,
                    CorrelationId = correlationId
                };
                message.ApplicationProperties["X-Correlation-Id"] = correlationId;
                message.ApplicationProperties["X-Replay"] = "true";
                message.ApplicationProperties["X-Replay-Of"] = entry.Id.ToString();

                await using var sender = sbClient.CreateSender(entry.Topic);
                await sender.SendMessageAsync(message);

                var replayEntry = EventLogAlias.CreateReplay(
                    entry.EventType, entry.Topic, entry.Payload,
                    correlationId, entry.EntityId, entry.Id);
                replayEntry.SetPublished();
                db.EventLogs.Add(replayEntry);
            }

            await db.SaveChangesAsync();
            return Results.Accepted(null as string, new { ReplayedCount = events.Count });
        });
    }
}

internal sealed record EventLogDto(
    Guid Id,
    string EventType,
    string Topic,
    string? CorrelationId,
    string? EntityId,
    DateTimeOffset OccurredAt,
    DateTimeOffset? PublishedAt,
    bool IsReplay,
    Guid? OriginalEventId);
