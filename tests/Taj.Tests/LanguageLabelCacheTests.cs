namespace Taj.Tests;

public sealed class LanguageLabelCacheTests
{
    [Theory]
    [InlineData(0x0409, "EN")]
    [InlineData(0x0401, "AR")]
    [InlineData(0x0407, "DE")]
    [InlineData(0xFFFF, "0xFFFF")]
    public void LanguageCodesPreserveKnownAndUnknownLabels(int language, string expected) =>
        Assert.Equal(expected, KeyboardLanguage.Code(language));

    [Fact]
    public void RepeatedLayoutPollsDoNotAllocateCultureObjectsOrLabels()
    {
        for (int i = 0; i < 200; i++) KeyboardLanguage.Code(0x0409);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) KeyboardLanguage.Code(0x0409);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
