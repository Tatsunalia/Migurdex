using System.Net;
using System.Net.Sockets;

namespace Migurdex.Shared;

/// <summary>Shared SSRF guard for API extractor requests and CLI downloads.</summary>
public static class NetworkGuard
{
    public static async Task<bool> ResolvesToBlockedAddressAsync(
        string host,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host)
            || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(host, out var literal))
        {
            return IsBlockedAddress(literal);
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            // Preserve existing API behavior: transient DNS failures do not block a host.
            return false;
        }

        return addresses.Any(IsBlockedAddress);
    }

    public static bool IsBlockedAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 0
                   || bytes[0] == 10
                   || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                   || (bytes[0] == 192 && bytes[1] == 168)
                   || (bytes[0] == 169 && bytes[1] == 254)
                   || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127);
        }

        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal;
    }
}
