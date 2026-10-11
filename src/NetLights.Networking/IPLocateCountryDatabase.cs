using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using NetLights.Core;

namespace NetLights.Networking;

/// <summary>Bounded offline reader for IPLocate's public country CSV ZIP mirror.</summary>
internal sealed class IPLocateCountryDatabase
{
    internal const int MaximumArchiveBytes = 24 * 1024 * 1024;
    private const int MaximumCsvBytes = 128 * 1024 * 1024;
    private const int MaximumNetworks = 2_500_000;
    private readonly Bucket<uint>[] _v4;
    private readonly Bucket<UInt128>[] _v6;
    private readonly string[] _countries;

    private IPLocateCountryDatabase(Bucket<uint>[] v4, Bucket<UInt128>[] v6, string[] countries, int networkCount)
    {
        _v4 = v4;
        _v6 = v6;
        _countries = countries;
        NetworkCount = networkCount;
    }

    internal int NetworkCount { get; }

    internal static IPLocateCountryDatabase ParseArchive(byte[] archive, int minimumNetworks = 1_000_000,
        int minimumNetworksPerFamily = 10_000)
    {
        if (archive is null || archive.Length == 0 || archive.Length > MaximumArchiveBytes)
            throw new InvalidDataException("IPLocate ZIP size is outside the permitted range.");
        // The caller already owns the bounded byte array. Let the read-only stream
        // reference it directly instead of making a second full ZIP-sized copy.
        using var zip = new ZipArchive(new MemoryStream(archive, writable: false), ZipArchiveMode.Read);
        ZipArchiveEntry[] candidates = zip.Entries.Where(item => item.FullName.StartsWith("ip-to-country-", StringComparison.OrdinalIgnoreCase)
            && item.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (candidates.Length != 1)
            throw new InvalidDataException("IPLocate archive must contain exactly one country CSV.");
        ZipArchiveEntry entry = candidates[0];
        if (entry.Length <= 0 || entry.Length > MaximumCsvBytes)
            throw new InvalidDataException("IPLocate CSV size is outside the permitted range.");

        var counts4 = new int[33];
        var counts6 = new int[129];
        var countryIndexes = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var countries = new List<string>();
        int networkCount = 0;
        Scan(entry, (address, prefix, country) =>
        {
            if (++networkCount > MaximumNetworks) throw new InvalidDataException("IPLocate CSV has too many networks.");
            if (!countryIndexes.ContainsKey(country))
            {
                if (countries.Count >= byte.MaxValue) throw new InvalidDataException("IPLocate CSV has too many country codes.");
                countryIndexes.Add(country, (byte)countries.Count);
                countries.Add(country);
            }
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) counts4[prefix]++;
            else counts6[prefix]++;
        });
        if (minimumNetworks < 1 || networkCount < minimumNetworks) throw new InvalidDataException("IPLocate CSV is unexpectedly incomplete.");
        if (minimumNetworksPerFamily < 1 || counts4.Sum() < minimumNetworksPerFamily || counts6.Sum() < minimumNetworksPerFamily)
            throw new InvalidDataException("IPLocate CSV must contain substantial IPv4 and IPv6 data.");

        Bucket<uint>[] v4 = Allocate<uint>(counts4);
        Bucket<UInt128>[] v6 = Allocate<UInt128>(counts6);
        var offsets4 = new int[33];
        var offsets6 = new int[129];
        Scan(entry, (address, prefix, country) =>
        {
            byte index = countryIndexes[country];
            byte[] bytes = address.GetAddressBytes();
            if (bytes.Length == 4)
            {
                uint value = BinaryPrimitives.ReadUInt32BigEndian(bytes) & Mask32(prefix);
                int offset = offsets4[prefix]++;
                v4[prefix].Keys[offset] = value;
                v4[prefix].Countries[offset] = index;
            }
            else
            {
                UInt128 value = Read128(bytes) & Mask128(prefix);
                int offset = offsets6[prefix]++;
                v6[prefix].Keys[offset] = value;
                v6[prefix].Countries[offset] = index;
            }
        });
        Sort(v4);
        Sort(v6);
        return new IPLocateCountryDatabase(v4, v6, countries.ToArray(), networkCount);
    }

    internal string? Lookup(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        if (address.IsIPv4MappedToIPv6) bytes = address.MapToIPv4().GetAddressBytes();
        if (bytes.Length == 4)
        {
            uint value = BinaryPrimitives.ReadUInt32BigEndian(bytes);
            for (int prefix = 32; prefix >= 0; prefix--)
            {
                Bucket<uint> bucket = _v4[prefix];
                int found = Array.BinarySearch(bucket.Keys, value & Mask32(prefix));
                if (found >= 0) return _countries[bucket.Countries[found]];
            }
            return null;
        }
        if (bytes.Length != 16) return null;
        UInt128 addressValue = Read128(bytes);
        for (int prefix = 128; prefix >= 0; prefix--)
        {
            Bucket<UInt128> bucket = _v6[prefix];
            int found = Array.BinarySearch(bucket.Keys, addressValue & Mask128(prefix));
            if (found >= 0) return _countries[bucket.Countries[found]];
        }
        return null;
    }

    private static void Scan(ZipArchiveEntry entry, Action<IPAddress, int, string> row)
    {
        using Stream input = entry.Open();
        using var reader = new StreamReader(input, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        string? line = reader.ReadLine();
        if (line is null || !line.StartsWith("network,", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("IPLocate CSV header is invalid.");
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length is 0 or > 256) continue;
            string[] fields = line.Split(',');
            if (fields.Length < 3 || !GeoCountryParsers.TryNormalizeIso3166Alpha2(fields[2], out string? country)) continue;
            string[] cidr = fields[0].Split('/');
            if (cidr.Length != 2 || !IPAddress.TryParse(cidr[0], out IPAddress? networkAddress)) continue;
            int bits = networkAddress.GetAddressBytes().Length * 8;
            if (!int.TryParse(cidr[1], out int prefix) || prefix < 0 || prefix > bits) continue;
            row(networkAddress, prefix, country!);
        }
    }

    private static Bucket<T>[] Allocate<T>(int[] counts) where T : unmanaged
    {
        var result = new Bucket<T>[counts.Length];
        for (int i = 0; i < counts.Length; i++) result[i] = new Bucket<T>(counts[i]);
        return result;
    }

    private static void Sort<T>(Bucket<T>[] buckets) where T : unmanaged, IComparable<T>
    {
        foreach (Bucket<T> bucket in buckets) Array.Sort(bucket.Keys, bucket.Countries);
    }

    private static uint Mask32(int prefix) => prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
    private static UInt128 Mask128(int prefix) => prefix == 0 ? 0 : UInt128.MaxValue << (128 - prefix);
    private static UInt128 Read128(byte[] bytes) => ((UInt128)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(0, 8)) << 64)
        | BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(8, 8));

    private sealed class Bucket<T>(int count) where T : unmanaged
    {
        public T[] Keys { get; } = new T[count];
        public byte[] Countries { get; } = new byte[count];
    }
}
