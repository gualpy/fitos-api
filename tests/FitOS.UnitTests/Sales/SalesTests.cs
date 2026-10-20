using FitOS.Application.Common;
using FitOS.Application.Products;
using FitOS.Application.Sales;
using FitOS.Domain.Customers;
using FitOS.Domain.Products;
using FitOS.Domain.Sales;
using FitOS.UnitTests.Members;

namespace FitOS.UnitTests.Sales;

public sealed class SaleCalculatorTests
{
    private static PricedLine Line(decimal unit, int qty, decimal iva) =>
        new(Guid.NewGuid(), null, "x", qty, unit, iva, SaleCalculator.LineTotal(unit, qty));

    [Fact]
    public void Line_total_is_price_times_quantity_rounded_to_cents()
    {
        Assert.Equal(7.50m, SaleCalculator.LineTotal(2.50m, 3));
        Assert.Equal(0.34m, SaleCalculator.LineTotal(0.1125m, 3)); // 0.3375 -> 0.34 (away from zero)
    }

    [Fact]
    public void Iva_is_computed_on_the_sum_of_each_rate_like_an_invoice()
    {
        // Three lines of 0.10 at 15 %: per line 0.015 rounds to 0.02 (0.06 total) but the invoice says 0.30 * 15 % = 0.045 -> 0.05.
        var totals = SaleCalculator.Totals([Line(0.10m, 1, 15), Line(0.10m, 1, 15), Line(0.10m, 1, 15)]);

        Assert.Equal(0.30m, totals.Subtotal);
        Assert.Equal(0.05m, totals.TaxTotal);
        Assert.Equal(0.35m, totals.Total);
    }

    [Fact]
    public void Different_rates_are_taxed_separately_and_zero_rate_adds_nothing()
    {
        var totals = SaleCalculator.Totals([Line(35m, 1, 15), Line(10m, 2, 5), Line(1.5m, 4, 0)]);

        Assert.Equal(61m, totals.Subtotal);
        Assert.Equal(5.25m + 1m, totals.TaxTotal);
        Assert.Equal(67.25m, totals.Total);
    }

    [Fact]
    public void Total_always_equals_subtotal_plus_tax()
    {
        var totals = SaleCalculator.Totals([Line(3.33m, 7, 15), Line(1.11m, 13, 5)]);

        Assert.Equal(totals.Subtotal + totals.TaxTotal, totals.Total);
        Assert.Equal(decimal.Round(totals.Total, 2), totals.Total);
    }
}

public sealed class SaleServiceTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    private sealed class MemorySales : ISaleRepository
    {
        public List<Product> Products { get; } = [];

        public SaleDraft? Written { get; private set; }

        public Exception? FailWith { get; set; }

        public Task<IReadOnlyList<Product>> FindProductsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Product>>(Products.Where(p => ids.Contains(p.Id)).ToList());

        public Task CreateAsync(SaleDraft sale, CancellationToken cancellationToken)
        {
            if (FailWith is not null)
            {
                throw FailWith;
            }

            Written = sale;
            return Task.CompletedTask;
        }
    }

    private sealed class Codes : IPaymentCodes
    {
        public string? CodeFor(string method) => method == "cash" ? "01" : method == "transfer" ? "20" : null;
    }

    private sealed class Fixture
    {
        public MemorySales Sales { get; } = new();

        public MemoryCustomers Customers { get; } = new();

        public Product Whey { get; } = new() { TenantId = TenantId, Name = "Whey", Price = 35m, IvaRate = 15m, Stock = 10 };

        public Product Water { get; } = new() { TenantId = TenantId, Name = "Agua", Price = 1.5m, IvaRate = 0m, Stock = 100 };

        public Fixture()
        {
            Sales.Products.AddRange([Whey, Water]);
        }

        public SaleService Service() => new(Sales, Customers, new Codes(), new FixedTenant(TenantId), TimeProvider.System);
    }

    private static SaleInput Input(Fixture f, decimal pay, params (Product Product, int Qty)[] lines) =>
        new(null, lines.Select(l => new SaleLineInput(l.Product.Id, l.Qty)).ToList(), [new PaymentInput("cash", pay)]);

    [Fact]
    public async Task A_sale_is_priced_by_the_server_from_the_catalogue()
    {
        var f = new Fixture();

        var sale = await f.Service().CreateAsync(Input(f, 41.75m, (f.Whey, 1), (f.Water, 1)), Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(36.5m, sale.Totals.Subtotal);
        Assert.Equal(5.25m, sale.Totals.TaxTotal);
        Assert.Equal(41.75m, sale.Totals.Total);
        Assert.Equal(["Whey", "Agua"], sale.Lines.Select(l => l.Description));
        Assert.Equal(35m, sale.Lines[0].UnitPrice);
    }

    [Fact]
    public async Task Paying_a_different_amount_than_the_total_is_refused_and_nothing_is_written()
    {
        var f = new Fixture();

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            f.Service().CreateAsync(Input(f, 10m, (f.Whey, 1)), null, CancellationToken.None));

        Assert.Equal("payment_mismatch", ex.Code);
        Assert.Null(f.Sales.Written);
    }

    [Fact]
    public async Task Two_payments_that_add_up_are_accepted_and_get_their_sri_code_from_configuration()
    {
        var f = new Fixture();
        var input = new SaleInput(null, [new SaleLineInput(f.Water.Id, 4)], [new PaymentInput("cash", 2m), new PaymentInput("transfer", 4m)]);

        var sale = await f.Service().CreateAsync(input, null, CancellationToken.None);

        Assert.Equal(["01", "20"], sale.Payments.Select(p => p.SriCode));
        Assert.Equal(["01", "20"], f.Sales.Written!.Payments.Select(p => p.SriCode));
    }

    [Fact]
    public async Task The_same_product_twice_becomes_one_line_with_the_summed_quantity()
    {
        var f = new Fixture();
        var input = new SaleInput(null, [new SaleLineInput(f.Water.Id, 2), new SaleLineInput(f.Water.Id, 3)], [new PaymentInput("cash", 7.5m)]);

        var sale = await f.Service().CreateAsync(input, null, CancellationToken.None);

        var line = Assert.Single(sale.Lines);
        Assert.Equal(5, line.Quantity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10_001)]
    public async Task A_quantity_outside_the_allowed_range_is_invalid(int quantity)
    {
        var f = new Fixture();
        var input = new SaleInput(null, [new SaleLineInput(f.Water.Id, quantity)], [new PaymentInput("cash", 1.5m)]);

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => f.Service().CreateAsync(input, null, CancellationToken.None));

        Assert.Equal("invalid", ex.Errors["lines"]);
    }

    [Fact]
    public async Task A_cart_without_lines_or_payments_is_invalid()
    {
        var f = new Fixture();

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() =>
            f.Service().CreateAsync(new SaleInput(null, [], null), null, CancellationToken.None));

        Assert.Equal("required", ex.Errors["lines"]);
        Assert.Equal("required", ex.Errors["payments"]);
    }

    [Theory]
    [InlineData("bitcoin", 1.5)]
    [InlineData("cash", 0)]
    [InlineData("cash", -1)]
    [InlineData("cash", 1.505)]
    public async Task Unknown_methods_and_bad_amounts_are_invalid(string method, double amount)
    {
        var f = new Fixture();
        var input = new SaleInput(null, [new SaleLineInput(f.Water.Id, 1)], [new PaymentInput(method, (decimal)amount)]);

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => f.Service().CreateAsync(input, null, CancellationToken.None));

        Assert.Equal("invalid", ex.Errors["payments"]);
    }

    [Fact]
    public async Task A_product_that_does_not_exist_for_this_gym_is_not_found()
    {
        var f = new Fixture();
        var input = new SaleInput(null, [new SaleLineInput(Guid.NewGuid(), 1)], [new PaymentInput("cash", 1.5m)]);

        await Assert.ThrowsAsync<NotFoundException>(() => f.Service().CreateAsync(input, null, CancellationToken.None));
    }

    [Fact]
    public async Task An_inactive_product_cannot_be_sold()
    {
        var f = new Fixture();
        f.Water.IsActive = false;

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            f.Service().CreateAsync(Input(f, 1.5m, (f.Water, 1)), null, CancellationToken.None));

        Assert.Equal("product_inactive", ex.Code);
    }

    [Fact]
    public async Task An_unknown_customer_is_not_found_before_anything_is_written()
    {
        var f = new Fixture();
        var input = Input(f, 1.5m, (f.Water, 1)) with { CustomerId = Guid.NewGuid() };

        await Assert.ThrowsAsync<NotFoundException>(() => f.Service().CreateAsync(input, null, CancellationToken.None));
        Assert.Null(f.Sales.Written);
    }

    [Fact]
    public async Task A_known_customer_is_recorded_on_the_sale()
    {
        var f = new Fixture();
        var customer = new Customer { TenantId = TenantId, Name = "Ana" };
        f.Customers.Items.Add(customer);
        var input = Input(f, 1.5m, (f.Water, 1)) with { CustomerId = customer.Id };

        var sale = await f.Service().CreateAsync(input, null, CancellationToken.None);

        Assert.Equal(customer.Id, sale.CustomerId);
    }

    [Fact]
    public async Task Stock_running_out_during_the_write_surfaces_as_a_conflict()
    {
        var f = new Fixture();
        f.Sales.FailWith = new ConflictException("insufficient_stock");

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            f.Service().CreateAsync(Input(f, 1.5m, (f.Water, 1)), null, CancellationToken.None));

        Assert.Equal("insufficient_stock", ex.Code);
    }

    [Fact]
    public async Task Without_a_tenant_nothing_is_sold()
    {
        var f = new Fixture();
        var service = new SaleService(f.Sales, f.Customers, new Codes(), new FixedTenant(null), TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(Input(f, 1.5m, (f.Water, 1)), null, CancellationToken.None));
    }
}

public sealed class ProductServiceTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    private sealed class MemoryProducts : IProductRepository
    {
        public List<Product> Items { get; } = [];

        public List<(Guid Product, int Delta, string Reason)> Moves { get; } = [];

        public Task<IReadOnlyList<Product>> ListAsync(string? text, bool includeInactive, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Product>>(Items.Where(p => includeInactive || p.IsActive).ToList());

        public Task<Product?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Items.FirstOrDefault(p => p.Id == id));

        public Task<Product?> ReadAsync(Guid id, CancellationToken cancellationToken) => GetAsync(id, cancellationToken);

        public Task CreateAsync(Product product, Guid? userId, CancellationToken cancellationToken)
        {
            Items.Add(product);
            return Task.CompletedTask;
        }

        public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> AdjustStockAsync(Guid productId, int delta, string reason, Guid? userId, CancellationToken cancellationToken)
        {
            var product = Items.Single(p => p.Id == productId);
            if (product.Stock + delta < 0)
            {
                return Task.FromResult(false);
            }

            product.Stock += delta;
            Moves.Add((productId, delta, reason));
            return Task.FromResult(true);
        }
    }

    private static ProductService Service(MemoryProducts repo) => new(repo, new FixedTenant(TenantId));

    private static ProductInput Valid() => new("SKU-1", "7501", "Proteína Whey 1kg", "Suplementos", 35m, 15m, 15);

    [Fact]
    public async Task A_valid_product_is_created_for_the_current_gym_with_its_opening_stock()
    {
        var repo = new MemoryProducts();

        var product = await Service(repo).CreateAsync(Valid(), null, CancellationToken.None);

        Assert.Equal(TenantId, product.TenantId);
        Assert.Equal(15, product.Stock);
        Assert.True(product.IsActive);
    }

    [Fact]
    public async Task Blank_optional_fields_are_stored_as_nothing_and_text_is_trimmed()
    {
        var repo = new MemoryProducts();

        var product = await Service(repo).CreateAsync(Valid() with { Sku = "  ", Barcode = null, Category = " Bebidas ", Name = "  Agua  " }, null, CancellationToken.None);

        Assert.Null(product.Sku);
        Assert.Null(product.Barcode);
        Assert.Equal("Bebidas", product.Category);
        Assert.Equal("Agua", product.Name);
    }

    [Fact]
    public async Task Invalid_fields_are_all_reported_at_once()
    {
        var input = new ProductInput(new string('s', 51), new string('b', 51), " ", new string('c', 51), -1m, 12m, -3);

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => Service(new MemoryProducts()).CreateAsync(input, null, CancellationToken.None));

        Assert.Equal("required", ex.Errors["name"]);
        Assert.Equal("too_long", ex.Errors["sku"]);
        Assert.Equal("too_long", ex.Errors["barcode"]);
        Assert.Equal("too_long", ex.Errors["category"]);
        Assert.Equal("invalid", ex.Errors["price"]);
        Assert.Equal("invalid", ex.Errors["ivaRate"]);
        Assert.Equal("invalid", ex.Errors["stock"]);
    }

    [Theory]
    [InlineData(1.999)]
    [InlineData(100000.01)]
    public async Task A_price_with_more_than_cents_or_above_the_ceiling_is_invalid(double price)
    {
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() =>
            Service(new MemoryProducts()).CreateAsync(Valid() with { Price = (decimal)price }, null, CancellationToken.None));

        Assert.Equal("invalid", ex.Errors["price"]);
    }

    [Fact]
    public async Task Update_changes_the_data_but_never_the_stock()
    {
        var repo = new MemoryProducts();
        var service = Service(repo);
        var product = await service.CreateAsync(Valid(), null, CancellationToken.None);

        var updated = await service.UpdateAsync(product.Id, Valid() with { Name = "Whey 2kg", Price = 60m, Stock = 999 }, CancellationToken.None);

        Assert.Equal("Whey 2kg", updated.Name);
        Assert.Equal(60m, updated.Price);
        Assert.Equal(15, updated.Stock);
    }

    [Fact]
    public async Task Updating_or_reading_a_missing_product_is_not_found()
    {
        var service = Service(new MemoryProducts());

        await Assert.ThrowsAsync<NotFoundException>(() => service.UpdateAsync(Guid.NewGuid(), Valid(), CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetAsync(Guid.NewGuid(), CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => service.SetActiveAsync(Guid.NewGuid(), false, CancellationToken.None));
    }

    [Fact]
    public async Task Products_are_deactivated_and_reactivated_never_deleted()
    {
        var repo = new MemoryProducts();
        var service = Service(repo);
        var product = await service.CreateAsync(Valid(), null, CancellationToken.None);

        Assert.False((await service.SetActiveAsync(product.Id, false, CancellationToken.None)).IsActive);
        Assert.True((await service.SetActiveAsync(product.Id, true, CancellationToken.None)).IsActive);
    }

    [Fact]
    public async Task Stock_is_adjusted_up_and_down_with_a_reason()
    {
        var repo = new MemoryProducts();
        var service = Service(repo);
        var product = await service.CreateAsync(Valid(), null, CancellationToken.None);

        await service.AdjustStockAsync(product.Id, new StockAdjustmentInput(10, "purchase"), null, CancellationToken.None);
        var after = await service.AdjustStockAsync(product.Id, new StockAdjustmentInput(-5, " Adjustment "), null, CancellationToken.None);

        Assert.Equal(20, after.Stock);
        Assert.Equal([(product.Id, 10, "purchase"), (product.Id, -5, "adjustment")], repo.Moves);
    }

    [Fact]
    public async Task Stock_can_never_go_below_zero()
    {
        var repo = new MemoryProducts();
        var service = Service(repo);
        var product = await service.CreateAsync(Valid(), null, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            service.AdjustStockAsync(product.Id, new StockAdjustmentInput(-16, "adjustment"), null, CancellationToken.None));

        Assert.Equal("insufficient_stock", ex.Code);
        Assert.Equal(15, product.Stock);
    }

    [Theory]
    [InlineData(null, "adjustment")]
    [InlineData(0, "adjustment")]
    [InlineData(1_000_001, "adjustment")]
    [InlineData(5, null)]
    [InlineData(5, "sale")]
    [InlineData(5, "theft")]
    public async Task A_stock_change_needs_a_non_zero_delta_and_a_manual_reason(int? delta, string? reason)
    {
        var repo = new MemoryProducts();
        var service = Service(repo);
        var product = await service.CreateAsync(Valid(), null, CancellationToken.None);

        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            service.AdjustStockAsync(product.Id, new StockAdjustmentInput(delta, reason), null, CancellationToken.None));
        Assert.Empty(repo.Moves);
    }
}
