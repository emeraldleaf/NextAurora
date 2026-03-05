using FluentAssertions;
using NSubstitute;
using NextAurora.Contracts.Events;
using PaymentService.Application.Commands;
using PaymentService.Application.Handlers;
using PaymentService.Domain.Entities;
using PaymentService.Domain.Interfaces;

namespace PaymentService.Tests.Unit.Application;

public class ProcessPaymentHandlerTests
{
    private readonly IPaymentRepository _repository = Substitute.For<IPaymentRepository>();
    private readonly IPaymentGateway _gateway = Substitute.For<IPaymentGateway>();
    private readonly IEventPublisher _eventPublisher = Substitute.For<IEventPublisher>();
    private readonly ProcessPaymentHandler _sut;

    public ProcessPaymentHandlerTests()
    {
        _sut = new ProcessPaymentHandler(_repository, _gateway, _eventPublisher);
    }

    private static ProcessPaymentCommand ValidCommand() =>
        new(Guid.NewGuid(), 99.99m, "USD");

    [Fact]
    public async Task Handle_WhenGatewaySucceeds_CompletesPaymentAndPublishesEvent()
    {
        var command = ValidCommand();
        _gateway.ProcessPaymentAsync(command.Amount, command.Currency, Arg.Any<CancellationToken>())
            .Returns(new PaymentGatewayResult(true, "txn_success"));

        var result = await _sut.Handle(command, CancellationToken.None);

        result.Should().NotBeEmpty();
        await _repository.Received(1).AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());
        await _repository.Received(1).UpdateAsync(Arg.Is<Payment>(p => p.Status == PaymentStatus.Completed), Arg.Any<CancellationToken>());
        await _eventPublisher.Received(1).PublishAsync(Arg.Any<PaymentCompletedEvent>(), "payment-events", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenGatewayFails_FailsPaymentAndPublishesEvent()
    {
        var command = ValidCommand();
        _gateway.ProcessPaymentAsync(command.Amount, command.Currency, Arg.Any<CancellationToken>())
            .Returns(new PaymentGatewayResult(false, "", "Card declined"));

        var result = await _sut.Handle(command, CancellationToken.None);

        result.Should().NotBeEmpty();
        await _repository.Received(1).UpdateAsync(Arg.Is<Payment>(p => p.Status == PaymentStatus.Failed), Arg.Any<CancellationToken>());
        await _eventPublisher.Received(1).PublishAsync(Arg.Any<PaymentFailedEvent>(), "payment-events", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_CreatesPaymentBeforeCallingGateway()
    {
        var command = ValidCommand();
        var callOrder = new List<string>();
        _repository.AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("add"));
        _gateway.ProcessPaymentAsync(Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PaymentGatewayResult(true, "txn_123"))
            .AndDoes(_ => callOrder.Add("gateway"));

        await _sut.Handle(command, CancellationToken.None);

        callOrder.Should().ContainInOrder("add", "gateway");
    }

    [Fact]
    public async Task Handle_UpdatesPaymentAfterResult()
    {
        var command = ValidCommand();
        _gateway.ProcessPaymentAsync(command.Amount, command.Currency, Arg.Any<CancellationToken>())
            .Returns(new PaymentGatewayResult(true, "txn_123"));

        await _sut.Handle(command, CancellationToken.None);

        await _repository.Received(1).UpdateAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_CompletedEventContainsCorrectFields()
    {
        var command = ValidCommand();
        _gateway.ProcessPaymentAsync(command.Amount, command.Currency, Arg.Any<CancellationToken>())
            .Returns(new PaymentGatewayResult(true, "txn_abc"));

        await _sut.Handle(command, CancellationToken.None);

        await _eventPublisher.Received(1).PublishAsync(
            Arg.Is<PaymentCompletedEvent>(e =>
                e.OrderId == command.OrderId &&
                e.Amount == command.Amount &&
                e.Provider == "Stripe"),
            "payment-events",
            Arg.Any<CancellationToken>());
    }

    [Fact(Skip = "Known bug: No idempotency check — duplicate OrderId creates a second Payment")]
    public async Task Handle_DuplicateOrderId_CreatesSecondPayment()
    {
        var command = ValidCommand();
        _gateway.ProcessPaymentAsync(Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PaymentGatewayResult(true, "txn_123"));

        await _sut.Handle(command, CancellationToken.None);
        await _sut.Handle(command, CancellationToken.None);

        // Handler should check for existing payment via GetByOrderIdAsync before creating
        await _repository.Received(1).AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());
    }
}
