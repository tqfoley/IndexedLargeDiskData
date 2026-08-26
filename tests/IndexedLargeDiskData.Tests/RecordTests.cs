using IndexedLargeDiskData.Records;

namespace IndexedLargeDiskData.Tests;

public class RecordTests
{
    [Fact]
    public void TripleRecord_IsTwentyFourBytes()
    {
        Assert.Equal(24, TripleRecord.Size);
    }

    [Fact]
    public void TripleRecord_RoundTrips()
    {
        TripleRecord original = new(long.MinValue, 0, long.MaxValue);
        Span<byte> buffer = stackalloc byte[TripleRecord.Size];
        TripleRecord.Write(buffer, original);

        Assert.Equal(original, TripleRecord.Read(buffer));
    }

    [Theory]
    [InlineData(0, 0L)]
    [InlineData(1, 7L)]
    [InlineData(2, -3L)]
    public void TripleRecord_GetField_SelectsTheRightValue(int field, long expected)
    {
        TripleRecord record = new(0, 7, -3);
        Assert.Equal(expected, record.GetField(field));
    }

    [Fact]
    public void AddressRecord_IsEightyThreeBytes()
    {
        Assert.Equal(8 + 75, AddressRecord.Size);
    }

    [Fact]
    public void AddressRecord_RoundTrips()
    {
        AddressRecord original = new(1234567890123L, TestData.Address(42));
        Span<byte> buffer = stackalloc byte[AddressRecord.Size];
        AddressRecord.Write(buffer, original);

        Assert.Equal(original, AddressRecord.Read(buffer));
    }

    [Fact]
    public void AddressRecord_Write_RejectsARecordWithNoAddress()
    {
        byte[] buffer = new byte[AddressRecord.Size];
        Assert.Throws<ArgumentNullException>(() => AddressRecord.Write(buffer, default));
    }
}

public class AddressTextTests
{
    [Fact]
    public void AnAddress_IsSeventyFiveAsciiCharacters()
    {
        string address = TestData.Address(1);

        Assert.Equal(75, AddressRecord.AddressLength);
        Assert.Equal(AddressRecord.AddressLength, address.Length);
        Assert.All(address, c => Assert.True(char.IsAscii(c)));
        Assert.True(AddressRecord.IsValid(address));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(74)]
    [InlineData(76)]
    public void WrongLength_IsRejected(int length)
    {
        string address = new('a', length);

        Assert.False(AddressRecord.IsValid(address));
        Assert.Throws<ArgumentException>(() => new AddressRecord(1, address));
    }

    [Fact]
    public void NonAsciiCharacters_AreRejected()
    {
        string address = new string('a', AddressRecord.AddressLength - 1) + 'é';

        Assert.False(AddressRecord.IsValid(address));
        Assert.Throws<ArgumentException>(() => new AddressRecord(1, address));
    }

    [Fact]
    public void ANullAddress_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new AddressRecord(1, null!));
    }

    [Fact]
    public void Prefix_PacksTheLeadingEightCharactersLittleEndian()
    {
        string address = TestData.AddressWithPrefix("abcdefgh", 'z');

        Assert.Equal(unchecked((long)0x6867666564636261UL), AddressRecord.PrefixOf(address));
        Assert.Equal(AddressRecord.PrefixOf(address), new AddressRecord(1, address).AddressPrefix);
    }

    [Fact]
    public void Prefix_IgnoresEverythingPastTheEighthCharacter()
    {
        Assert.Equal(
            AddressRecord.PrefixOf(TestData.AddressWithPrefix("abcdefgh", 'y')),
            AddressRecord.PrefixOf(TestData.AddressWithPrefix("abcdefgh", 'z')));

        Assert.NotEqual(
            AddressRecord.PrefixOf(TestData.AddressWithPrefix("abcdefgh", 'z')),
            AddressRecord.PrefixOf(TestData.AddressWithPrefix("abcdefgi", 'z')));
    }

    [Fact]
    public void AddressesSharingAPrefix_AreStillDistinct()
    {
        string a = TestData.AddressWithPrefix("abcdefgh", '1');
        string b = TestData.AddressWithPrefix("abcdefgh", '2');

        Assert.Equal(AddressRecord.PrefixOf(a), AddressRecord.PrefixOf(b));
        Assert.NotEqual(new AddressRecord(7, a), new AddressRecord(7, b));
    }

    [Fact]
    public void DistinctSeeds_ProduceDistinctAddressesAndKeys()
    {
        HashSet<string> addresses = [];
        HashSet<long> keys = [];
        for (long seed = 0; seed < 1000; seed++)
        {
            string address = TestData.Address(seed);
            Assert.True(addresses.Add(address));
            keys.Add(AddressRecord.PrefixOf(address));
        }

        Assert.Equal(addresses.Count, keys.Count);
    }
}
