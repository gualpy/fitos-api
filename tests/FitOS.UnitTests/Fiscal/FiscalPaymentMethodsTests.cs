using FitOS.Application.Fiscal;

namespace FitOS.UnitTests.Fiscal;

public sealed class FiscalPaymentMethodsTests
{
    [Theory]
    [InlineData("cash", "no_utiliza_sist_financiero", "01")]
    [InlineData("debit_card", "tarjeta_debito", "16")]
    [InlineData("credit_card", "tarjeta_credito", "19")]
    [InlineData("transfer", "transferencia_bancaria", "20")]
    [InlineData("other", "otros", "20")]
    public void Maps_pos_methods_to_provider_and_sri_codes(string pos, string provider, string sri)
    {
        Assert.Equal(provider, FiscalPaymentMethods.ToProviderMethod(pos));
        Assert.Equal(sri, FiscalPaymentMethods.ToSriCode(pos));
    }

    [Fact]
    public void Unknown_method_fails_loudly()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FiscalPaymentMethods.ToProviderMethod("bitcoin"));
    }
}
