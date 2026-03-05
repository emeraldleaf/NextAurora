namespace PaymentService.Domain.Interfaces;

public interface IEventPublisher
{
    Task PublishAsync<T>(T @event, string topicName, CancellationToken ct = default) where T : class;
}
