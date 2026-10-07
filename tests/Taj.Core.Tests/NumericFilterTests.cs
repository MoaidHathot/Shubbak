using Taj.Core;
using Taj.Core.Widgets;

namespace Taj.Core.Tests;

/// <summary>
/// Arithmetic in a template, and the number-reading underneath it.
/// </summary>
/// <remarks>
/// A source carries a string, and a script that prints bytes should not have to be
/// rewritten to print gigabytes: the template can divide. The filters read the first
/// number in the value, so <c>87%</c> and <c>cpu 45</c> are numbers too, and leave a
/// value alone when there is nothing in it to compute with - a raw value on the bar
/// beats a blank.
/// </remarks>
public sealed class NumericFilterTests
{
    private static readonly Dictionary<string, string?> Values = new(StringComparer.Ordinal)
    {
        ["cpu"] = "45",
        ["battery"] = "87%",
        ["mem"] = "6442450944",
        ["ratio"] = "0.3333",
        ["load"] = "load: 1.75 avg",
        ["negative"] = "-12.5",
        ["word"] = "none",
        ["empty"] = "",
        ["layout"] = "splith",
        ["state"] = "on",
    };

    private static string Render(string template) => Template.Render(template, Values);

    // ---- reading numbers ---------------------------------------------------

    [Theory]
    [InlineData("45", 45)]
    [InlineData("87%", 87)]
    [InlineData("cpu 45", 45)]
    [InlineData("load: 1.75 avg", 1.75)]
    [InlineData("-12.5", -12.5)]
    [InlineData("+3", 3)]
    [InlineData("-.5", -0.5)]
    [InlineData(".25", 0.25)]
    [InlineData("87.", 87)]
    [InlineData("1.2.3", 1.2)]
    [InlineData("v2 of 3", 2)]
    public void TheFirstNumberInAValueIsTheNumber(string text, double expected)
    {
        Assert.True(Numbers.TryParseFirst(text, out double number));
        Assert.Equal(expected, number, precision: 9);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("none")]
    [InlineData("-")]
    [InlineData("a-b")]
    [InlineData(".")]
    public void AValueWithoutANumberSaysSo(string? text)
    {
        Assert.False(Numbers.TryParseFirst(text, out _));
    }

    [Fact]
    public void EveryNumberInAListIsRead()
    {
        // A history is a list, and a script may write one with spaces or commas; a
        // token with a unit on it is still a number, and one without any is skipped.
        Assert.Equal([12, 15.5, 50], Numbers.ParseAll("12 15.5 50"));
        Assert.Equal([1, 2, 3], Numbers.ParseAll("1,2,3"));
        Assert.Equal([10, 20], Numbers.ParseAll("10% x 20%"));
        Assert.Empty(Numbers.ParseAll(""));
        Assert.Empty(Numbers.ParseAll(null));
    }

    [Theory]
    [InlineData(87, "87")]
    [InlineData(0.5, "0.5")]
    [InlineData(1.0 / 3, "0.333333")]
    [InlineData(-2.25, "-2.25")]
    [InlineData(1e9, "1000000000")]
    public void ANumberIsWrittenAsShortAsItCanBe(double number, string expected)
    {
        Assert.Equal(expected, Numbers.Format(number));
    }

    [Fact]
    public void FixedDecimalsRoundHalfAwayFromZero()
    {
        // The way a person rounds, not the banker's way .NET defaults to: 2.5 is 3.
        Assert.Equal("3", Numbers.Format(2.5, 0));
        Assert.Equal("-3", Numbers.Format(-2.5, 0));
        Assert.Equal("87.0", Numbers.Format(87, 1));
        Assert.Equal("0.33", Numbers.Format(1.0 / 3, 2));
    }

    // ---- the filters -------------------------------------------------------

    [Theory]
    [InlineData("{{ ratio | round:2 }}", "0.33")]
    [InlineData("{{ load | round }}", "2")]
    [InlineData("{{ cpu | round:1 }}", "45.0")]
    [InlineData("{{ cpu | add:5 }}", "50")]
    [InlineData("{{ cpu | sub:50 }}", "-5")]
    [InlineData("{{ ratio | mul:3 }}", "0.9999")]
    [InlineData("{{ mem | div:1073741824 }}", "6")]
    [InlineData("{{ mem | div:1024 | div:1024 | round:1 }} MB", "6144.0 MB")]
    [InlineData("{{ battery | sub:100 | mul:-1 }}", "13")]
    [InlineData("{{ negative | mul:2 }}", "-25")]
    public void ArithmeticReadsTheNumberAndWritesOneBack(string template, string expected)
    {
        Assert.Equal(expected, Render(template));
    }

    [Theory]
    [InlineData("{{ ratio | percent }}", "33")]
    [InlineData("{{ cpu | percent:200 }}", "23")]
    [InlineData("{{ battery | percent:100 }}%", "87%")]
    public void PercentIsOfTheArgumentOrOfOne(string template, string expected)
    {
        Assert.Equal(expected, Render(template));
    }

    [Theory]
    [InlineData("{{ word | add:5 }}", "none")]
    [InlineData("{{ empty | round:1 }}", "")]
    [InlineData("{{ cpu | add:five }}", "45")]
    [InlineData("{{ cpu | div:0 }}", "45")]
    [InlineData("{{ cpu | percent:0 }}", "45")]
    [InlineData("{{ cpu | round:x }}", "45")]
    public void AValueThatCannotBeComputedWithPassesThrough(string template, string expected)
    {
        // The same bargain an unknown filter makes: a raw value says what it is, a
        // blank says nothing.
        Assert.Equal(expected, Render(template));
    }

    [Fact]
    public void ArithmeticDropsTheLabelAroundTheNumber()
    {
        // Once the value is a number it is only a number; the template puts the unit
        // back where it wants it.
        Assert.Equal("87", Render("{{ battery | add:0 }}"));
        Assert.Equal("1.75", Render("{{ load | add:0 }}"));
    }

    [Theory]
    [InlineData("{{ layout | map:splith=H,splitv=V }}", "H")]
    [InlineData("{{ layout | map:SPLITH=H }}", "H")]
    [InlineData("{{ layout | map:splitv=V }}", "splith")]
    [InlineData("{{ layout | map:splitv=V,*=? }}", "?")]
    [InlineData("{{ state | map:on=\uE720,off=\uE74F }}", "\uE720")]
    [InlineData("{{ empty | map:on=yes,*=no }}", "no")]
    [InlineData("{{ layout | map: splith = H , splitv = V }}", "H")]
    [InlineData("{{ layout | map:nonsense }}", "splith")]
    public void MapReplacesAValueFromATable(string template, string expected)
    {
        Assert.Equal(expected, Render(template));
    }

    [Fact]
    public void MapAndArithmeticChainWithTheOlderFilters()
    {
        Assert.Equal("LOW", Render("{{ cpu | sub:50 | map:-5=low | upper }}"));
        Assert.Equal("45 of 100", Render("{{ cpu | round }} of {{ cpu | percent:45 }}"));
    }

    // ---- cost ----------------------------------------------------------------

    [Fact]
    public void ReadingANumberAllocatesNothing()
    {
        // A numeric `when` reads its subject on every rebuild of the tree, and a
        // history reads every reading its source takes. Both parse from the span.
        string[] subjects = ["87%", "load: 1.75 avg", "cpu 45", "none", "-12.5"];
        int found = 0;

        for (int i = 0; i < 500; i++) if (Numbers.TryParseFirst(subjects[i % subjects.Length], out _)) found++;

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 1000; i++) if (Numbers.TryParseFirst(subjects[i % subjects.Length], out _)) found++;

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(1200, found);   // four of the five, over fifteen hundred calls
    }

    [Fact]
    public void ReadingAListAllocatesOnlyTheList()
    {
        // Sixty readings used to be sixty substrings and a list that grew five times;
        // now it is the one array the caller is handed. Measured here as a bound so a
        // return to the substrings is caught, not as a byte count the runtime owns.
        string sixty = string.Join(' ', Enumerable.Range(0, 60).Select(i => (i * 7 % 100).ToString(System.Globalization.CultureInfo.InvariantCulture)));

        for (int i = 0; i < 200; i++) Numbers.ParseAll(sixty);

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 100; i++) Numbers.ParseAll(sixty);

        long perCall = (GC.GetAllocatedBytesForCurrentThread() - before) / 100;

        // Sixty doubles and an array header; the substrings alone were five times this.
        Assert.InRange(perCall, 60 * sizeof(double), 60 * sizeof(double) + 64);
    }
}
