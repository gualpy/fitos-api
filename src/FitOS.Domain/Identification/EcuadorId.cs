namespace FitOS.Domain.Identification;

/// <summary>Structure and check-digit validation of Ecuadorian cédula and RUC numbers.</summary>
public static class EcuadorId
{
    private static readonly int[] CedulaCoefficients = [2, 1, 2, 1, 2, 1, 2, 1, 2];

    private static readonly int[] PrivateCoefficients = [4, 3, 2, 7, 6, 5, 4, 3, 2];

    private static readonly int[] PublicCoefficients = [3, 2, 7, 6, 5, 4, 3, 2];

    public static bool IsValidCedula(string? value) =>
        value is { Length: 10 } && AllDigits(value) && ValidProvince(value) && value[2] < '6' && CedulaCheck(value);

    /// <summary>Natural person (cédula + 001...), private company (3rd digit 9) or public entity (3rd digit 6).</summary>
    public static bool IsValidRuc(string? value)
    {
        if (value is not { Length: 13 } || !AllDigits(value) || !ValidProvince(value))
        {
            return false;
        }

        return value[2] switch
        {
            < '6' => CedulaCheck(value[..10]) && value[10..] != "000",
            '6' => Modulo11(value, PublicCoefficients, checkIndex: 8) && value[9..] != "0000",
            '9' => Modulo11(value, PrivateCoefficients, checkIndex: 9) && value[10..] != "000",
            _ => false,
        };
    }

    private static bool AllDigits(string value) => value.All(char.IsAsciiDigit);

    private static bool ValidProvince(string value)
    {
        var province = int.Parse(value[..2], System.Globalization.CultureInfo.InvariantCulture);
        return province is (>= 1 and <= 24) or 30;
    }

    private static bool CedulaCheck(string value)
    {
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            var product = (value[i] - '0') * CedulaCoefficients[i];
            sum += product > 9 ? product - 9 : product;
        }

        return (10 - (sum % 10)) % 10 == value[9] - '0';
    }

    private static bool Modulo11(string value, int[] coefficients, int checkIndex)
    {
        var sum = 0;
        for (var i = 0; i < coefficients.Length; i++)
        {
            sum += (value[i] - '0') * coefficients[i];
        }

        var remainder = sum % 11;
        var check = remainder == 0 ? 0 : 11 - remainder;
        return check != 10 && check == value[checkIndex] - '0';
    }
}
