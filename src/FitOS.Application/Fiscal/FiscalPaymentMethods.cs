namespace FitOS.Application.Fiscal;

/// <summary>Maps POS payment methods (sale_payments.method) to SICOFAC's enum and the SRI code.</summary>
public static class FiscalPaymentMethods
{
    private static readonly Dictionary<string, (string Sicofac, string SriCode)> Map = new(StringComparer.Ordinal)
    {
        ["cash"] = ("no_utiliza_sist_financiero", "01"),
        ["debit_card"] = ("tarjeta_debito", "16"),
        ["credit_card"] = ("tarjeta_credito", "19"),
        ["transfer"] = ("transferencia_bancaria", "20"),
        ["other"] = ("otros", "20"),
    };

    public static string ToProviderMethod(string posMethod) => Lookup(posMethod).Sicofac;

    public static string ToSriCode(string posMethod) => Lookup(posMethod).SriCode;

    private static (string Sicofac, string SriCode) Lookup(string posMethod) =>
        Map.TryGetValue(posMethod, out var value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(posMethod), posMethod, "Unknown payment method.");
}
