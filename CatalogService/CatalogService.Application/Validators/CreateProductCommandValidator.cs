using CatalogService.Application.Commands;
using FluentValidation;

namespace CatalogService.Application.Validators;

public class CreateProductCommandValidator : AbstractValidator<CreateProductCommand>
{
    public CreateProductCommandValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(x => x.Description)
            .MaximumLength(2000);

        RuleFor(x => x.Price)
            .GreaterThan(0);

        RuleFor(x => x.Currency)
            .NotEmpty()
            .Length(3);

        RuleFor(x => x.CategoryId)
            .NotEmpty();

        RuleFor(x => x.SellerId)
            .NotEmpty();

        RuleFor(x => x.StockQuantity)
            .GreaterThanOrEqualTo(0);
    }
}
