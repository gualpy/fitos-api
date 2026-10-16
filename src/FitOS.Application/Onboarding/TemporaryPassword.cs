using System.Security.Cryptography;

namespace FitOS.Application.Onboarding;

public static class TemporaryPassword
{
    // No look-alike characters (0/O, 1/l/I): it is read aloud or copied by hand.
    private const string Letters = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    private const string Digits = "23456789";

    public static string Generate(int length = 16)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            // Every 4th char is a digit, so the result always satisfies the "letter + non-letter" rule.
            var alphabet = i % 4 == 3 ? Digits : Letters;
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return new string(chars);
    }
}
