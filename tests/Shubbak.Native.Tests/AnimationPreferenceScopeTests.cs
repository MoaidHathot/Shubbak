using Shubbak.TestWindow;

namespace Shubbak.Native.Tests;

public sealed class AnimationPreferenceScopeTests
{
    [Fact]
    public void TheOriginalPreferenceIsRestoredWhenTheTestBodyThrows()
    {
        DesktopLease.Acquire();
        bool original = AnimationPreferenceScope.Read();
        var failure = new InvalidOperationException("deliberate test-body failure");

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var preference = new AnimationPreferenceScope(!original);
            Assert.Equal(original, preference.OriginalEnabled);
            Assert.Equal(!original, AnimationPreferenceScope.Read());
            throw failure;
        }));

        Assert.Same(failure, thrown);
        Assert.Equal(original, AnimationPreferenceScope.Read());
    }
}
