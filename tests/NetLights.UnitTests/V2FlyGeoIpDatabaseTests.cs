using System.Net;
using System.Text;
using NetLights.Networking;
using Xunit;

namespace NetLights.UnitTests;

public sealed class V2FlyGeoIpDatabaseTests
{
    [Fact]
    public void Lookup_UsesMostSpecificIpv4Network()
    {
        byte[] bytes = List(
            Entry("US", Cidr([10, 0, 0, 0], 8)),
            Entry("DE", Cidr([10, 20, 0, 0], 16)));
        V2FlyGeoIpDatabase db = V2FlyGeoIpDatabase.Parse(bytes);

        Assert.Equal("DE", db.Lookup(IPAddress.Parse("10.20.3.4")));
        Assert.Equal("US", db.Lookup(IPAddress.Parse("10.30.3.4")));
        Assert.Null(db.Lookup(IPAddress.Parse("11.30.3.4")));
    }

    [Fact]
    public void Lookup_SupportsIpv6AndIpv4MappedIpv6()
    {
        byte[] bytes = List(
            Entry("GB", Cidr(IPAddress.Parse("2001:db8::").GetAddressBytes(), 32)),
            Entry("US", Cidr([192, 0, 2, 0], 24)));
        V2FlyGeoIpDatabase db = V2FlyGeoIpDatabase.Parse(bytes);

        Assert.Equal("GB", db.Lookup(IPAddress.Parse("2001:db8:1::1")));
        Assert.Equal("US", db.Lookup(IPAddress.Parse("::ffff:192.0.2.7")));
    }

    [Fact]
    public void Parse_IgnoresNamedRoutingSetsFromPublishedDatabase()
    {
        byte[] bytes = List(
            Entry("DE", Cidr([203, 0, 113, 0], 24)),
            Entry("PRIVATE", Cidr([10, 0, 0, 0], 8)),
            Entry("TEST", Cidr([192, 0, 2, 0], 24)));

        V2FlyGeoIpDatabase db = V2FlyGeoIpDatabase.Parse(bytes);

        Assert.Equal("DE", db.Lookup(IPAddress.Parse("203.0.113.7")));
        Assert.Null(db.Lookup(IPAddress.Parse("10.0.0.1")));
    }

    [Fact]
    public void Parse_RejectsTruncatedAndOversizedDatabase()
    {
        Assert.Throws<InvalidDataException>(() => V2FlyGeoIpDatabase.Parse([0x0a, 0x80]));
        Assert.Throws<InvalidDataException>(() => V2FlyGeoIpDatabase.Parse(new byte[V2FlyGeoIpDatabase.MaximumFileBytes + 1]));
    }

    [Fact]
    public void Parse_RejectsInvalidPrefixAndEqualPrefixConflict()
    {
        Assert.Throws<InvalidDataException>(() => V2FlyGeoIpDatabase.Parse(List(Entry("DE", Cidr([10, 0, 0, 0], 33)))));
        Assert.Throws<InvalidDataException>(() => V2FlyGeoIpDatabase.Parse(List(
            Entry("DE", Cidr([10, 0, 0, 0], 8)),
            Entry("US", Cidr([10, 0, 0, 0], 8)))));
    }

    private static byte[] List(params byte[][] entries)
        => entries.SelectMany(entry => Message(0x0a, entry)).ToArray();

    private static byte[] Entry(string country, params byte[][] networks)
        => Message(0x0a, Encoding.ASCII.GetBytes(country))
            .Concat(networks.SelectMany(network => Message(0x12, network))).ToArray();

    private static byte[] Cidr(byte[] ip, int prefix)
        => Message(0x0a, ip).Concat(Varint(0x10)).Concat(Varint((ulong)prefix)).ToArray();

    private static byte[] Message(byte tag, byte[] value)
        => [tag, .. Varint((ulong)value.Length), .. value];

    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        while (value >= 0x80)
        {
            bytes.Add((byte)((value & 0x7f) | 0x80));
            value >>= 7;
        }
        bytes.Add((byte)value);
        return bytes.ToArray();
    }
}
