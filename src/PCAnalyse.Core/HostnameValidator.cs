using System.Text.RegularExpressions;

namespace PCAnalyse.Core;

public static class HostnameValidator
{
    private static readonly Regex Ipv4 = new(
        @"^(?:(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\.){3}(?:25[0-5]|2[0-4]\d|[01]?\d\d?)$",
        RegexOptions.CultureInvariant);

    private static readonly Regex HostName = new(
        @"^(?=.{1,253}$)[A-Za-z0-9](?:[A-Za-z0-9\-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9\-]{0,61}[A-Za-z0-9])?)*$",
        RegexOptions.CultureInvariant);

    public static bool TryNormalize(string? input, out string hostname, out string error)
    {
        hostname = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Hostname oder IP fehlt.";
            return false;
        }

        var value = input.Trim();
        if (value.StartsWith("\\\\", StringComparison.Ordinal))
        {
            value = value.TrimStart('\\').Split('\\', '/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? value;
        }

        value = value.Replace("http://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
            .Trim().TrimEnd('/');

        if (value.Contains(' ') || value.Contains('\\') || value.Contains('/') || value.Contains(':'))
        {
            error = "Nur Rechnername oder IPv4, ohne Pfad oder Port.";
            return false;
        }

        if (!Ipv4.IsMatch(value) && !HostName.IsMatch(value))
        {
            error = "Ungültiger Rechnername oder IP-Adresse.";
            return false;
        }

        hostname = value;
        return true;
    }
}
