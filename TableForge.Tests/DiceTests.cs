using TableForge.Dice;
using TableForge.Domain;
using TableForge.Import;

namespace TableForge.Tests;

public class DiceTests
{
    [Theory]
    [InlineData("d6", 1, 6)]
    [InlineData("D10", 1, 10)]
    [InlineData("d100", 1, 100)]
    [InlineData("2d6", 2, 6)]
    [InlineData("2D8", 2, 8)]
    [InlineData("3d6", 3, 6)]
    [InlineData("  d20 ", 1, 20)]
    public void Parses_supported_expressions(string text, int count, int sides)
    {
        Assert.True(DiceExpression.TryParse(text, out var dice));
        Assert.Equal(new DiceExpression(count, sides), dice);
    }

    [Theory]
    [InlineData("")]
    [InlineData("d")]
    [InlineData("6")]
    [InlineData("d1")]
    [InlineData("0d6")]
    [InlineData("2d6+1d4")]
    [InlineData("4d6kh3")]
    [InlineData("3d6!")]
    [InlineData("d6d6")]
    [InlineData("d%")]
    [InlineData("1000d6")]
    [InlineData("d10000")]
    [InlineData("d٦")] // non-ASCII digit
    [InlineData("hello")]
    public void Rejects_malformed_or_unsupported_expressions(string text)
    {
        Assert.False(DiceExpression.TryParse(text, out _));
        Assert.Throws<FormatException>(() => DiceExpression.Parse(text));
    }

    [Fact]
    public void Rejects_null()
    {
        Assert.False(DiceExpression.TryParse(null, out _));
    }

    [Theory]
    [InlineData("d10", 1, 10)]
    [InlineData("d100", 1, 100)]
    [InlineData("2d6", 2, 12)]
    [InlineData("3d6", 3, 18)]
    public void Reports_legal_range(string text, int min, int max)
    {
        var dice = DiceExpression.Parse(text);
        Assert.Equal(min, dice.Min);
        Assert.Equal(max, dice.Max);
        Assert.True(dice.IsLegal(min));
        Assert.True(dice.IsLegal(max));
        Assert.False(dice.IsLegal(min - 1));
        Assert.False(dice.IsLegal(max + 1));
    }

    [Theory]
    [InlineData("D10", "d10")]
    [InlineData("1d6", "d6")]
    [InlineData("2D6", "2d6")]
    public void Formats_canonically(string text, string expected) =>
        Assert.Equal(expected, DiceExpression.Parse(text).ToString());

    [Fact]
    public void Percentile_shows_100_as_00_but_no_other_dice_do()
    {
        Assert.Equal("00", DiceExpression.Parse("d100").FormatValue(100));
        Assert.Equal("99", DiceExpression.Parse("d100").FormatValue(99));
        Assert.Equal("100", DiceExpression.Parse("2d100").FormatValue(100));
        Assert.Equal("10", DiceExpression.Parse("d10").FormatValue(10));
    }

    [Theory]
    [InlineData("d6")]
    [InlineData("2d6")]
    [InlineData("d100")]
    public void Built_in_provider_stays_in_legal_range(string text)
    {
        var dice = DiceExpression.Parse(text);
        var provider = new BuiltInDiceProvider(new Random(1234));
        var seen = new HashSet<int>();
        for (var i = 0; i < 2000; i++)
        {
            var roll = provider.Roll(dice);
            Assert.True(dice.IsLegal(roll), $"{roll} is outside {dice.Min}-{dice.Max}");
            seen.Add(roll);
        }
        Assert.Contains(dice.Min, seen);
        Assert.Contains(dice.Max, seen);
    }

    [Theory]
    [InlineData("1-2", 1, 2)]
    [InlineData("3", 3, 3)]
    [InlineData(" 9 - 10 ", 9, 10)] // whitespace alone is not a distinct display form
    [InlineData("9—10", 9, 10)]
    public void Range_text_plain_forms_have_no_display_range(string text, int min, int max)
    {
        Assert.True(RangeText.TryParse(text, DiceExpression.Parse("d10"), out var range, out _));
        Assert.Equal(min, range.Min);
        Assert.Equal(max, range.Max);
        Assert.Null(range.DisplayRange);
    }

    [Fact]
    public void D100_range_text_keeps_numeric_and_display_forms_apart()
    {
        var d100 = DiceExpression.Parse("d100");

        Assert.True(RangeText.TryParse("96-00", d100, out var top, out _));
        Assert.Equal((96, 100), (top.Min, top.Max));
        Assert.Equal("96–00", top.DisplayRange);

        Assert.True(RangeText.TryParse("00", d100, out var zero, out _));
        Assert.Equal((100, 100), (zero.Min, zero.Max));
        Assert.Equal("00", zero.DisplayRange);

        Assert.True(RangeText.TryParse("08", d100, out var eight, out _));
        Assert.Equal((8, 8), (eight.Min, eight.Max));
        Assert.Equal("08", eight.DisplayRange);

        Assert.True(RangeText.TryParse("01–04", d100, out var low, out _));
        Assert.Equal((1, 4), (low.Min, low.Max));
    }

    [Fact]
    public void Double_zero_is_only_100_on_a_d100()
    {
        Assert.True(RangeText.TryParse("00", DiceExpression.Parse("d20"), out var range, out _));
        Assert.Equal(0, range.Min);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("5-")]
    [InlineData("1-2-3")]
    [InlineData("10-5")]
    [InlineData("99999999999")]
    public void Range_text_rejects_bad_input(string text)
    {
        Assert.False(RangeText.TryParse(text, DiceExpression.Parse("d10"), out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData("7", "d10", true, 7)]
    [InlineData("00", "d100", true, 100)]
    [InlineData("08", "d100", true, 8)]
    [InlineData(" 10 ", "d10", true, 10)]
    [InlineData("", "d10", false, 0)]
    [InlineData("-3", "d10", false, 0)]
    [InlineData("4.5", "d10", false, 0)]
    [InlineData("1-2", "d10", false, 0)]
    public void Manual_roll_value_parsing(string text, string dice, bool ok, int expected)
    {
        Assert.Equal(ok, RangeText.TryParseValue(text, DiceExpression.Parse(dice), out var value));
        if (ok) Assert.Equal(expected, value);
    }
}
