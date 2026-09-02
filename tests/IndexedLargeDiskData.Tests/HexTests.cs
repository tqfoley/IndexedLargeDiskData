namespace IndexedLargeDiskData.Tests;

public class HexTests
{
    [Theory]
    [InlineData(0UL, "0000000000000000")]
    [InlineData(255UL, "00000000000000ff")]
    [InlineData(ulong.MaxValue, "ffffffffffffffff")]
    public void ToHexString_IsSixteenLowercaseCharacters(ulong value, string expected)
    {
        Assert.Equal(expected, Hex.ToHexString(value));
        Assert.Equal(Hex.Width, Hex.ToHexString(value).Length);
    }

    [Fact]
    public void ToHexString_PadsSoThatTextSortsLikeTheNumber()
    {
        // The reason the default is padded: "9" sorts after "10" as text, "0009" does not.
        ulong[] values = [1, 9, 16, 255, 4096, ulong.MaxValue];
        string[] rendered = [.. values.Select(Hex.ToHexString)];

        Assert.Equal(rendered.Order(StringComparer.Ordinal), rendered);
    }

    [Fact]
    public void ToHexString_Unpadded_DropsTheLeadingZeros()
    {
        Assert.Equal("ff", Hex.ToHexString(255, padded: false));
        Assert.Equal("0", Hex.ToHexString(0, padded: false));
        Assert.Equal("00000000000000ff", Hex.ToHexString(255, padded: true));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(4242UL)]
    [InlineData(1UL << 63)]
    [InlineData(ulong.MaxValue)]
    public void ToHexString_AndParse_RoundTrip(ulong value)
    {
        Assert.Equal(value, Hex.Parse(Hex.ToHexString(value)));
        Assert.Equal(value, Hex.Parse(Hex.ToHexString(value, padded: false)));
    }

    [Theory]
    [InlineData("ff", 255UL)]
    [InlineData("FF", 255UL)]
    [InlineData("0xff", 255UL)]
    [InlineData("0XFF", 255UL)]
    [InlineData("00000000000000ff", 255UL)]
    [InlineData("ffffffffffffffff", ulong.MaxValue)]
    public void Parse_AcceptsEitherCaseAndAnOptionalPrefix(string text, ulong expected)
    {
        Assert.Equal(expected, Hex.Parse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0x")]
    [InlineData("g")]
    [InlineData("12g4")]
    [InlineData(" ff")]
    [InlineData("ff ")]
    [InlineData("-1")]
    [InlineData("10000000000000000")]
    public void Parse_RejectsAnythingThatIsNotSixtyFourBitsOfHex(string text)
    {
        // A seventeenth digit is refused rather than wrapped: an overflowed value that still parses
        // is worse than a value that does not parse at all.
        Assert.False(Hex.TryParse(text, out ulong value));
        Assert.Equal(0UL, value);
        Assert.Throws<FormatException>(() => Hex.Parse(text));
    }

    [Fact]
    public void TheTopBitIsAValueBitNotASign()
    {
        // The whole point of the values being unsigned: this used to be long.MinValue.
        Assert.Equal("8000000000000000", Hex.ToHexString(1UL << 63));
        Assert.Equal(1UL << 63, Hex.Parse("8000000000000000"));
    }
}
