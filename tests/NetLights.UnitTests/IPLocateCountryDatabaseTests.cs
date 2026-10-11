using System.IO.Compression;
using System.Net;
using System.Text;
using NetLights.Networking;
using Xunit;

namespace NetLights.UnitTests;

public sealed class IPLocateCountryDatabaseTests
{
    [Fact]
    public void ParseArchive_LooksUpIpv4Ipv6AndLongestPrefix()
    {
        byte[] bytes = Archive("network,continent_code,country_code,country_name\n"
            + "203.0.113.0/24,EU,DE,Germany\n"
            + "203.0.113.64/26,AS,JP,Japan\n"
            + "2001:db8::/32,EU,FR,France\n"
            + "invalid,EU,GB,United Kingdom\n");
        byte[] original = bytes.ToArray();

        IPLocateCountryDatabase database = IPLocateCountryDatabase.ParseArchive(bytes, minimumNetworks: 3, minimumNetworksPerFamily: 1);

        Assert.Equal(original, bytes);
        Assert.Equal(3, database.NetworkCount);
        Assert.Equal("DE", database.Lookup(IPAddress.Parse("203.0.113.12")));
        Assert.Equal("JP", database.Lookup(IPAddress.Parse("203.0.113.70")));
        Assert.Equal("FR", database.Lookup(IPAddress.Parse("2001:db8::42")));
        Assert.Null(database.Lookup(IPAddress.Parse("198.51.100.1")));
    }

    [Fact]
    public void ParseArchive_RejectsSmallOrMalformedArchives()
    {
        Assert.Throws<InvalidDataException>(() => IPLocateCountryDatabase.ParseArchive(new byte[] { 1, 2, 3 }));
        Assert.Throws<InvalidDataException>(() => IPLocateCountryDatabase.ParseArchive(
            Archive("not,a,country,header\n203.0.113.0/24,EU,DE,Germany\n"), minimumNetworks: 1));
    }

    [Fact]
    public void ParseArchive_RejectsAmbiguousCountryCsvAsInvalidData()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("ip-to-country-one.csv");
            zip.CreateEntry("ip-to-country-two.csv");
        }
        Assert.Throws<InvalidDataException>(() => IPLocateCountryDatabase.ParseArchive(output.ToArray()));
    }

    private static byte[] Archive(string csv)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        using (Stream stream = zip.CreateEntry("ip-to-country-20260930.csv").Open())
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            writer.Write(csv);
        return output.ToArray();
    }
}
