namespace Ayn.Tests;

public sealed class ConfigurationCacheTests
{
    [Fact]
    public void IdenticalReadsReuseTheParsedResultButEditsAndInvalidTextAreReevaluated()
    {
        var cache = new ConfigurationCache();
        const string Original = "ayn { settle 500; }";
        var first = cache.Parse(Original);
        Assert.False(first.SyntaxErrors);
        Assert.Same(first, cache.Parse(new string(Original.AsSpan())));
        var edited = cache.Parse("ayn { settle 700; }");
        Assert.NotSame(first, edited);
        Assert.NotEqual(first.Config, edited.Config);
        Assert.True(cache.Parse("ayn {").SyntaxErrors);
        var restored = cache.Parse(Original);
        Assert.False(restored.SyntaxErrors);
        Assert.Equal(first.Config.EffectiveSettle, restored.Config.EffectiveSettle);
    }

    [Fact]
    public void UnchangedReloadParsingAllocatesNothingAfterTheFirstRead()
    {
        var cache = new ConfigurationCache();
        const string Source = "ayn { settle 500; }";
        for (int i = 0; i < 200; i++) cache.Parse(Source);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) cache.Parse(Source);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void OversizedFilesAreParsedWithoutBeingRetainedByTheCache()
    {
        var cache = new ConfigurationCache();
        string source = "//" + new string('x', ConfigurationCache.MaximumCachedCharacters) + "\nayn { settle 500; }";
        var first = cache.Parse(source);
        Assert.False(first.SyntaxErrors);
        Assert.NotSame(first, cache.Parse(source));
    }

    [Fact]
    public void FileComparisonDetectsSameStampEditsAndPreservesEncodingAndIoFailures()
    {
        string path = Path.Combine(Path.GetTempPath(), $"shubbak-ayn-cache-{Guid.NewGuid():N}.kdl");
        try
        {
            var cache = new ConfigurationCache();
            string prefix = "//" + new string('x', 5000) + "\n";
            File.WriteAllText(path, prefix + "ayn { settle 500; }");
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            var first = cache.Read(path);
            Assert.Same(first, cache.Read(path));
            File.WriteAllText(path, prefix + "ayn { settle 700; }");
            File.SetLastWriteTimeUtc(path, stamp);
            var edited = cache.Read(path);
            Assert.Equal(TimeSpan.FromMilliseconds(700), edited.Config.EffectiveSettle);
            File.WriteAllText(path, prefix + "ayn { settle 700; }", System.Text.Encoding.Unicode);
            Assert.Same(edited, cache.Read(path));
            File.Delete(path);
            Assert.Throws<FileNotFoundException>(() => cache.Read(path));
        }
        finally { File.Delete(path); }
    }
}
