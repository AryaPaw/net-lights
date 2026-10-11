using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace NetLights.Networking;

/// <summary>Small bounded reader for the GeoIPList protobuf used by v2fly/geoip.</summary>
internal sealed class V2FlyGeoIpDatabase
{
    internal const int MaximumFileBytes = 40 * 1024 * 1024;
    private const int MaximumEntries = 4096;
    private const int MaximumNetworks = 2_000_000;
    private readonly PrefixBucket<uint>[] _ipv4;
    private readonly PrefixBucket<UInt128>[] _ipv6;
    private readonly string[] _countries;

    private V2FlyGeoIpDatabase(string[] countries, PrefixBucket<uint>[] ipv4,
        PrefixBucket<UInt128>[] ipv6, int networkCount)
    {
        _countries = countries;
        _ipv4 = ipv4;
        _ipv6 = ipv6;
        NetworkCount = networkCount;
    }

    public int NetworkCount { get; }

    public static V2FlyGeoIpDatabase Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaximumFileBytes)
            throw new InvalidDataException("GeoIP database size is invalid.");

        var countryIndexes = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var countries = new List<string>();
        var ipv4Counts = new int[33];
        var ipv6Counts = new int[129];
        int offset = 0;
        int entries = 0;
        int parsedNetworks = 0;
        int networkCount = 0;
        while (offset < bytes.Length)
        {
            (int field, int wire, ulong value) = ReadTag(bytes, ref offset);
            if (wire != 2)
            {
                Skip(bytes, ref offset, wire, value);
                continue;
            }

            ReadOnlySpan<byte> message = ReadLengthDelimited(bytes, ref offset);
            if (field != 1) continue;
            if (++entries > MaximumEntries) throw new InvalidDataException("GeoIP database has too many country entries.");
            ParseEntry(message, countryIndexes, countries, ipv4Counts, ipv6Counts,
                null, null, null, null, ref parsedNetworks, ref networkCount);
        }

        if (networkCount == 0 || countries.Count == 0)
            throw new InvalidDataException("GeoIP database contains no networks.");

        PrefixBucket<uint>[] ipv4 = AllocateBuckets<uint>(ipv4Counts);
        PrefixBucket<UInt128>[] ipv6 = AllocateBuckets<UInt128>(ipv6Counts);
        var ipv4Offsets = new int[ipv4.Length];
        var ipv6Offsets = new int[ipv6.Length];
        offset = 0;
        while (offset < bytes.Length)
        {
            (int field, int wire, ulong value) = ReadTag(bytes, ref offset);
            if (wire != 2)
            {
                Skip(bytes, ref offset, wire, value);
                continue;
            }
            ReadOnlySpan<byte> message = ReadLengthDelimited(bytes, ref offset);
            if (field != 1) continue;
            ParseEntry(message, countryIndexes, countries, ipv4Counts, ipv6Counts,
                ipv4, ipv6, ipv4Offsets, ipv6Offsets, ref parsedNetworks, ref networkCount);
        }

        SortAndValidate(ipv4);
        SortAndValidate(ipv6);
        return new V2FlyGeoIpDatabase(countries.ToArray(), ipv4, ipv6, networkCount);
    }

    public string? Lookup(IPAddress address)
    {
        byte[] raw = address.GetAddressBytes();
        bool ipv4 = raw.Length == 4 || (raw.Length == 16 && address.IsIPv4MappedToIPv6);
        if (raw.Length == 16 && address.IsIPv4MappedToIPv6) raw = address.MapToIPv4().GetAddressBytes();
        if (!ipv4 && raw.Length != 16) return null;
        if (ipv4)
        {
            uint value = BinaryPrimitives.ReadUInt32BigEndian(raw);
            for (int prefix = 32; prefix >= 0; prefix--)
            {
                PrefixBucket<uint> bucket = _ipv4[prefix];
                if (bucket.Keys.Length == 0) continue;
                int index = Array.BinarySearch(bucket.Keys, Mask32(value, prefix));
                if (index >= 0) return _countries[bucket.Countries[index]];
            }
        }
        else
        {
            UInt128 value = ReadUInt128(raw);
            for (int prefix = 128; prefix >= 0; prefix--)
            {
                PrefixBucket<UInt128> bucket = _ipv6[prefix];
                if (bucket.Keys.Length == 0) continue;
                int index = Array.BinarySearch(bucket.Keys, Mask(value, 128, prefix));
                if (index >= 0) return _countries[bucket.Countries[index]];
            }
        }

        return null;
    }

    private static void ParseEntry(
        ReadOnlySpan<byte> bytes,
        Dictionary<string, byte> countryIndexes,
        List<string> countries,
        int[] ipv4Counts,
        int[] ipv6Counts,
        PrefixBucket<uint>[]? ipv4,
        PrefixBucket<UInt128>[]? ipv6,
        int[]? ipv4Offsets,
        int[]? ipv6Offsets,
        ref int parsedNetworks,
        ref int networkCount)
    {
        int offset = 0;
        string? countryCode = null;
        while (offset < bytes.Length)
        {
            (int field, int wire, ulong value) = ReadTag(bytes, ref offset);
            if (field == 1 && wire == 2)
            {
                countryCode = Encoding.UTF8.GetString(ReadLengthDelimited(bytes, ref offset));
            }
            else
            {
                Skip(bytes, ref offset, wire, value);
            }
        }

        // The upstream database also includes named routing sets (for example PRIVATE and TEST).
        // They are valid entries, but cannot be displayed as countries.
        bool isCountry = countryCode is { Length: 2 }
            && char.IsAsciiLetter(countryCode[0]) && char.IsAsciiLetter(countryCode[1]);
        byte countryIndex = 0;
        if (isCountry && !countryIndexes.TryGetValue(countryCode!, out countryIndex))
        {
            if (ipv4 is not null) throw new InvalidDataException("GeoIP country entries changed during parsing.");
            if (countries.Count >= byte.MaxValue) throw new InvalidDataException("GeoIP database contains too many country codes.");
            countryIndex = (byte)countries.Count;
            countryCode = countryCode!.ToUpperInvariant();
            countries.Add(countryCode);
            countryIndexes.Add(countryCode, countryIndex);
        }

        offset = 0;
        while (offset < bytes.Length)
        {
            (int field, int wire, ulong value) = ReadTag(bytes, ref offset);
            if (field != 2 || wire != 2)
            {
                Skip(bytes, ref offset, wire, value);
                continue;
            }

            (UInt128 key, int prefix, bool isIpv4) = ParseCidr(ReadLengthDelimited(bytes, ref offset));
            if (ipv4 is null && ++parsedNetworks > MaximumNetworks)
                throw new InvalidDataException("GeoIP database has too many networks.");
            if (!isCountry) continue;

            if (isIpv4)
            {
                uint masked = Mask32((uint)key, prefix);
                if (ipv4 is null) ipv4Counts[prefix]++;
                else
                {
                    int index = ipv4Offsets![prefix]++;
                    ipv4[prefix].Keys[index] = masked;
                    ipv4[prefix].Countries[index] = countryIndex;
                }
            }
            else
            {
                UInt128 masked = Mask(key, 128, prefix);
                if (ipv6 is null) ipv6Counts[prefix]++;
                else
                {
                    int index = ipv6Offsets![prefix]++;
                    ipv6[prefix].Keys[index] = masked;
                    ipv6[prefix].Countries[index] = countryIndex;
                }
            }
            if (ipv4 is null) networkCount++;
        }
    }

    private static (UInt128 Key, int Prefix, bool Ipv4) ParseCidr(ReadOnlySpan<byte> bytes)
    {
        int offset = 0;
        UInt128 ip = 0;
        int ipLength = 0;
        int? prefix = null;
        while (offset < bytes.Length)
        {
            (int field, int wire, ulong value) = ReadTag(bytes, ref offset);
            if (field == 1 && wire == 2)
            {
                ReadOnlySpan<byte> address = ReadLengthDelimited(bytes, ref offset);
                ipLength = address.Length;
                if (ipLength == 4) ip = BinaryPrimitives.ReadUInt32BigEndian(address);
                else if (ipLength == 16) ip = ReadUInt128(address);
            }
            else if (field == 2 && wire == 0)
            {
                if (value > 128) throw new InvalidDataException("GeoIP CIDR prefix is out of range.");
                prefix = (int)value;
            }
            else
            {
                Skip(bytes, ref offset, wire, value);
            }
        }

        if (prefix is null || (ipLength != 4 && ipLength != 16) || prefix > ipLength * 8)
            throw new InvalidDataException("GeoIP database contains an invalid CIDR.");
        return (ip, prefix.Value, ipLength == 4);
    }

    private static (int Field, int Wire, ulong Value) ReadTag(ReadOnlySpan<byte> bytes, ref int offset)
    {
        ulong tag = ReadVarint(bytes, ref offset);
        int field = checked((int)(tag >> 3));
        int wire = (int)(tag & 7);
        if (field == 0 || wire == 3 || wire == 4 || wire > 5)
            throw new InvalidDataException("GeoIP database contains an invalid protobuf tag.");
        ulong value = wire == 0 ? ReadVarint(bytes, ref offset) : 0;
        return (field, wire, value);
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> bytes, ref int offset)
    {
        ulong result = 0;
        for (int shift = 0; shift < 70; shift += 7)
        {
            if (offset >= bytes.Length) throw new InvalidDataException("GeoIP database protobuf is truncated.");
            byte current = bytes[offset++];
            if (shift == 63 && current > 1) throw new InvalidDataException("GeoIP database varint overflows.");
            result |= (ulong)(current & 0x7f) << shift;
            if ((current & 0x80) == 0) return result;
        }

        throw new InvalidDataException("GeoIP database varint is too long.");
    }

    private static ReadOnlySpan<byte> ReadLengthDelimited(ReadOnlySpan<byte> bytes, ref int offset)
    {
        ulong length = ReadVarint(bytes, ref offset);
        if (length > (ulong)(bytes.Length - offset)) throw new InvalidDataException("GeoIP database protobuf is truncated.");
        ReadOnlySpan<byte> result = bytes.Slice(offset, (int)length);
        offset += (int)length;
        return result;
    }

    private static void Skip(ReadOnlySpan<byte> bytes, ref int offset, int wire, ulong value)
    {
        switch (wire)
        {
            case 0: return;
            case 1: Advance(bytes, ref offset, 8); return;
            case 2: _ = ReadLengthDelimited(bytes, ref offset); return;
            case 5: Advance(bytes, ref offset, 4); return;
            default: throw new InvalidDataException("GeoIP database protobuf uses an unsupported wire type.");
        }
    }

    private static void Advance(ReadOnlySpan<byte> bytes, ref int offset, int count)
    {
        if (bytes.Length - offset < count) throw new InvalidDataException("GeoIP database protobuf is truncated.");
        offset += count;
    }

    private static PrefixBucket<TKey>[] AllocateBuckets<TKey>(int[] counts)
    {
        var result = new PrefixBucket<TKey>[counts.Length];
        for (int i = 0; i < result.Length; i++)
            result[i] = new PrefixBucket<TKey>(new TKey[counts[i]], new byte[counts[i]]);
        return result;
    }

    private static void SortAndValidate<TKey>(PrefixBucket<TKey>[] buckets)
        where TKey : IComparable<TKey>
    {
        foreach (PrefixBucket<TKey> bucket in buckets)
        {
            Array.Sort(bucket.Keys, bucket.Countries);
            for (int i = 1; i < bucket.Keys.Length; i++)
                if (bucket.Keys[i - 1].CompareTo(bucket.Keys[i]) == 0
                    && bucket.Countries[i - 1] != bucket.Countries[i])
                    throw new InvalidDataException("GeoIP database contains conflicting networks with equal prefixes.");
        }
    }

    private static UInt128 ReadUInt128(ReadOnlySpan<byte> bytes)
        => ((UInt128)BinaryPrimitives.ReadUInt64BigEndian(bytes) << 64)
            | BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);

    private static UInt128 Mask(UInt128 value, int bits, int prefix)
    {
        if (prefix == 0) return 0;
        int shift = bits - prefix;
        return shift == 0 ? value : (value >> shift) << shift;
    }

    private static uint Mask32(uint value, int prefix)
        => prefix == 0 ? 0 : prefix == 32 ? value : value & (uint.MaxValue << (32 - prefix));

    private readonly record struct PrefixBucket<TKey>(TKey[] Keys, byte[] Countries);
}
