using System.Security.Cryptography;

namespace PCAnalyse.Core;

public static class PairingToken
{
    public static string Create() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public static bool EqualsFixed(string left, string right)
    {
        try
        {
            var a = Convert.FromHexString(left);
            var b = Convert.FromHexString(right);
            return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
