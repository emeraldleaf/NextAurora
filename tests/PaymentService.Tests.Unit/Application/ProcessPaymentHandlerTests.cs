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
        new(Guid.NewGuid(), 99.99m, "USD", Guid.NewGuid());

    [Fact]
    public async Task Handle_WhenGatewaySucceeds_CompletesPaymentAndPublishesEvent()
    {
        // Arrange
        var command = ValidCommand();
        _gateway.ProcessPaymentAsync(command.Amount, command.Currency, Arg.Any<CancellationToken>())
            .Returns(new PaymentGatewayResult(true, "txn_success"));

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.Should().NotBeEmpty();
        await _repository.Received(1).AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());
        await _repository.Received(1).UpdateAsync(Arg.Is<Payment>(p => p.Status == PaymentStatus.Completed), Arg.Any<CancellationToken>());
        await _eventPublisher.Received(1).PublishAsync(Arg.Any<PaymentCompletedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenGatewayFails_FailsPaymentAndPublishesEvent()
    {
        // Arrange
        var command = ValidCommand();
        _gateway.ProcessPaymentAsync(command.Amount, command.Currency, Arg.Any<CancellationToken>())
            .Returns(new PaymentGatewayResult(false, "", "Card declined"));

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.Should().NotBeEmpty();
        await _repository.Received(1).UpdateAsync(Arg.Is<Payment>(p => p.Status == PaymentStatus.Failed), Arg.Any<CancellationToken>());
        await _eventPublisher.Received(1).PublishAsync(Arg.Any<PaymentFailedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_CreatesPaymentBeforeCallingGateway()
    {
        // Arrange
        var command = ValidCommand();
        var callOrder = new List<string>();
        _repository.AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("add"));
        _gateway.ProcessPaymentAsync(Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PaymentGatewayResult(true, "txn_123"))
            .AndDoes(_ => callOrder.Add("gateway"));

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        callOrder.Should().ContainInOrder("add", "gateway");
    }

    [Fact]
    public async Task Handle_UpdatesPaymentAfterResult()
    {
        // Arrange
        var command = ValidCommand();
        _gateway.ProcessPaymentAsync(command.Amount, command.Currency, Arg.Any<CancellationToken>())
            .Returns(new PaymentGatewayResult(true, "txn_123"));

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        await _repository.Received(1).UpdateAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_CompletedEventContainsCorrectFields()
    {
        // Arrange
        var command = ValidCommand();
        _gateway.ProcessPaymentAsync(command.Amount, command.Currency, Arg.Any<CancellationToken>())
            .Returns(new PaymentGatewayResult(true, "txn_abc"));

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        await _eventPublisher.Received(1).PublishAsync(
            Arg.Is<PaymentCompletedEvent>(e =>
                e.OrderId == command.OrderId &&
                e.Amount == command.Amount &&
                e.Provider == "Stripe"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenPaymentExistsForOrder_ReturnsExistingPaymentId()
    {
        // Arrange
        var command = ValidCommand();
        var existingPayment = Payment.Create(command.OrderId, command.Amount, command.Currency, "Stripe");
        _repository.GetByOrderIdAsync(command.OrderId, Arg.Any<CancellationToken>()).Returns(existingPayment);

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.Should().Be(existingPayment.Id);
        await _repository.DidNotReceive().AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());
        await _gateway.DidNotReceive().ProcessPaymentAsync(Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
