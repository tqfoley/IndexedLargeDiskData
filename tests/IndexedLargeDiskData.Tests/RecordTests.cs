using IndexedLargeDiskData.Records;

namespace IndexedLargeDiskData.Tests;

public class RecordTests
{
    [Fact]
    public void QuadrupleRecord_IsFortyBytes()
    {
        Assert.Equal(40, QuadrupleRecord.Size);
    }

    [Fact]
    public void QuadrupleRecord_CarriesOneMoreValueThanItIndexes()
    {
        Assert.Equal(5, QuadrupleRecord.FieldCount);
        Assert.Equal(4, QuadrupleRecord.IndexedFieldCount);
    }

    [Fact]
    public void QuadrupleRecord_RoundTrips()
    {
        QuadrupleRecord original = new(ulong.MinValue, 0, ulong.MaxValue, ulong.MaxValue - 1, 4242);
        Span<byte> buffer = stackalloc byte[QuadrupleRecord.Size];
        QuadrupleRecord.Write(buffer, original);

        Assert.Equal(original, QuadrupleRecord.Read(buffer));
    }

    [Theory]
    [InlineData(0, 0UL)]
    [InlineData(1, 7UL)]
    [InlineData(2, ulong.MaxValue)]
    [InlineData(3, 9UL)]
    [InlineData(4, 11UL)]
    public void QuadrupleRecord_GetField_SelectsTheRightValue(int field, ulong expected)
    {
        // The third value is every bit set: under `long` that was -1, and the point of the field
        // being unsigned is that it now round-trips as the largest value instead.
        QuadrupleRecord record = new(0, 7, ulong.MaxValue, 9, 11);
        Assert.Equal(expected, record.GetField(field));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(QuadrupleRecord.FieldCount)]
    public void QuadrupleRecord_GetField_RejectsAFieldOutsideTheRecord(int field)
    {
        QuadrupleRecord record = new(0, 7, ulong.MaxValue, 9, 11);
        Assert.Throws<ArgumentOutOfRangeException>(() => record.GetField(field));
    }

    [Fact]
    public void AddressRecord_IsEightyThreeBytes()
    {
        Assert.Equal(8 + 55, AddressRecord.Size);
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

        Assert.Equal(unchecked((ulong)0x6867666564636261UL), AddressRecord.PrefixOf(address));
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
        HashSet<ulong> keys = [];
        for (ulong seed = 0; seed < 1000; seed++)
        {
            string address = TestData.Address(seed);
            Assert.True(addresses.Add(address));
            keys.Add(AddressRecord.PrefixOf(address));
        }

        Assert.Equal(addresses.Count, keys.Count);
    }
}
