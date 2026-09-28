using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace NetLights.Core;

public static class GeoCountryParsers
{
    public static bool TryParseIpWhoCurrent(ReadOnlySpan<byte> utf8, out string country)
    {
        country = "";
        if (!TryReadObject(utf8, out JsonDocument doc))
        {
            return false;
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("success", out JsonElement success)
                || success.ValueKind != JsonValueKind.True
                || !TryReadString(root, "country_code", out string? code)
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
}
