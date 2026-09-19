using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace NetLights.Core;

public static class GeoCountryParsers
{
    public static bool TryParseCountryIs(ReadOnlySpan<byte> utf8, out IPAddress ip, out string country)
    {
        ip = IPAddress.None;
        country = "";
        if (!TryReadObject(utf8, out JsonDocument doc))
        {
            return false;
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (!TryReadString(root, "ip", out string? ipText)
                || !TryReadString(root, "country", out string? code)
                || !TryParsePublicUnicast(ipText, out IPAddress parsed)
                || !TryNormalizeIso3166Alpha2(code, out string? iso))
            {
                return false;
            }

            ip = parsed;
            country = iso;
            return true;
        }
    }

    public static bool TryParseIpWho(ReadOnlySpan<byte> utf8, out string country)
    {
        country = "";
        if (!TryReadObject(utf8, out JsonDocument doc))
        {
            return false;
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("success", out JsonElement success)
                && success.ValueKind == JsonValueKind.False)
            {
                return false;
            }

            if (!TryReadString(root, "country_code", out string? code)
                || !TryNormalizeIso3166Alpha2(code, out string? iso))
            {
                return false;
            }

            country = iso;
            return true;
        }
    }

    public static bool TryNormalizeIso3166Alpha2(string? value, [NotNullWhen(true)] out string? iso)
    {
        iso = null;
        if (value is not { Length: 2 })
        {
            return false;
        }

        char a = char.ToUpperInvariant(value[0]);
        char b = char.ToUpperInvariant(value[1]);
        if (!char.IsAsciiLetter(a) || !char.IsAsciiLetter(b))
        {
            return false;
        }

        iso = string.Create(2, (a, b), static (span, parts) =>
        {
            span[0] = parts.a;
            span[1] = parts.b;
        });
        try
        {
            _ = new RegionInfo(iso);
        }
        catch (ArgumentException)
        {
            iso = null;
            return false;
        }

        return true;
    }

    public static bool IsIso3166Alpha2([NotNullWhen(true)] string? value)
        => TryNormalizeIso3166Alpha2(value, out _);

    public static bool TryParsePublicUnicast(string? text, out IPAddress ip)
    {
        ip = IPAddress.None;
        if (!IPAddress.TryParse(text, out IPAddress? parsed) || !IsPublicUnicast(parsed))
        {
            return false;
        }

        ip = parsed;
        return true;
    }

    public static bool IsPublicUnicast(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip)
            || EqualsUnspecified(ip)
            || ip.IsIPv6Multicast
            || ip.IsIPv6LinkLocal
            || ip.IsIPv6SiteLocal
            || ip.IsIPv6UniqueLocal)
        {
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return true;
        }

        if (ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[4];
        if (!ip.TryWriteBytes(bytes, out int written) || written != 4)
        {
            return false;
        }

        byte a = bytes[0];
        byte b = bytes[1];
        if (a == 0 || a == 10 || a == 127 || a >= 224)
        {
            return false;
        }

        if (a == 169 && b == 254)
        {
            return false;
        }

        if (a == 172 && b is >= 16 and <= 31)
        {
            return false;
        }

        if (a == 192 && b == 168)
        {
            return false;
        }

        if (a == 100 && b is >= 64 and <= 127)
        {
            return false;
        }

        return true;
    }

    public static string IpWhoLookupUrl(Uri ipWhoBase, IPAddress ip)
    {
        string root = ipWhoBase.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return root + "/" + Uri.EscapeDataString(ip.ToString()) + "?fields=" + GeoCountryPolicy.IpWhoFields;
    }

    public static Uri IpWhoLookupUri(Uri ipWhoBase, IPAddress ip)
        => new(IpWhoLookupUrl(ipWhoBase, ip), UriKind.Absolute);

    private static bool TryReadObject(ReadOnlySpan<byte> utf8, out JsonDocument doc)
    {
        doc = null!;
        try
        {
            doc = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
        }
        catch (JsonException)
        {
            return false;
        }

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            doc.Dispose();
            doc = null!;
            return false;
        }

        return true;
    }

    private static bool TryReadString(JsonElement root, string name, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out JsonElement el) || el.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = el.GetString();
        return !string.IsNullOrEmpty(value);
    }

    private static bool EqualsUnspecified(IPAddress ip)
        => ip.Equals(IPAddress.Any)
           || ip.Equals(IPAddress.IPv6Any)
           || ip.Equals(IPAddress.None)
           || ip.Equals(IPAddress.IPv6None);
}
