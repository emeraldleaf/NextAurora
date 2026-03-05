using PaymentService.Domain.Entities;

namespace PaymentService.Tests.Unit.Builders;

public class PaymentBuilder
{
    private Guid _orderId = Guid.NewGuid();
    private decimal _amount = 99.99m;
    private string _currency = "USD";
    private string _provider = "Stripe";

    public static PaymentBuilder Default() => new();

    public PaymentBuilder WithOrderId(Guid id) { _orderId = id; return this; }
    public PaymentBuilder WithAmount(decimal a) { _amount = a; return this; }
    public PaymentBuilder WithCurrency(string c) { _currency = c; return this; }
    public PaymentBuilder WithProvider(string p) { _provider = p; return this; }

    public Payment Build() => Payment.Create(_orderId, _amount, _currency, _provider);
}
