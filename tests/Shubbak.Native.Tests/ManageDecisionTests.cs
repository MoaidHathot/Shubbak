namespace Shubbak.Native.Tests;

/// <summary>
/// The two ways a verdict is worded.
/// </summary>
/// <remarks>
/// <para>
/// <c>Explain</c> is written for a terminal, where a paragraph costs nothing and the
/// advice at the end of it is the most useful part. <c>Summarise</c> is written for a
/// palette row, which is one clipped line - so the long form arrives there as a
/// sentence with its ending cut off, and the ending was where the answer was.
/// </para>
/// <para>
/// Two switches over one enum, which is why they are tested together: the compiler
/// will not notice a reason that has a sentence and no summary, and a user will only
/// notice it as a row reading "unknown".
/// </para>
/// </remarks>
public class ManageDecisionTests
{
    private static IEnumerable<ExclusionReason> Reasons =>
        Enum.GetValues<ExclusionReason>().Where(r => r is not ExclusionReason.None);

    [Fact]
    public void EveryReasonHasAnExplanationOfItsOwn()
    {
        foreach (ExclusionReason reason in Reasons)
        {
            string explained = ManageDecision.No(reason).Explain();

            Assert.False(string.IsNullOrWhiteSpace(explained));

            // The fallback. A reason added to the enum and forgotten here reaches the
            // user as the least useful word available.
            Assert.NotEqual("unknown", explained);
        }
    }

    [Fact]
    public void EveryReasonHasAShortFormAsWell()
    {
        foreach (ExclusionReason reason in Reasons)
        {
            string summary = ManageDecision.No(reason).Summarise();

            Assert.False(string.IsNullOrWhiteSpace(summary));
            Assert.NotEqual("unknown", summary);
        }
    }

    [Fact]
    public void TheShortFormIsActuallyShort()
    {
        // The point of having one. A palette row shows it beside the process name in
        // the dim half of a single clipped line, so a sentence there is a sentence
        // with its ending cut off - and several of the long forms run past 150
        // characters, ending with the part that says what to do about it.
        foreach (ExclusionReason reason in Reasons)
        {
            string summary = ManageDecision.No(reason).Summarise();

            Assert.True(
                summary.Length <= 40,
                $"{reason} summarises to {summary.Length} characters: \"{summary}\"");
        }
    }

    [Fact]
    public void TheShortFormIsNotJustTheLongOne()
    {
        // Guards against the lazy implementation. If a summary is ever written as a
        // copy of the sentence it stops being a summary, and the row it exists for
        // goes back to being clipped.
        foreach (ExclusionReason reason in Reasons)
        {
            ManageDecision decision = ManageDecision.No(reason);

            Assert.NotEqual(decision.Explain(), decision.Summarise());
        }
    }

    [Fact]
    public void AManageableWindowSaysSoBothWays()
    {
        Assert.Equal("manageable", ManageDecision.Yes.Explain());
        Assert.Equal("manageable", ManageDecision.Yes.Summarise());
    }

    [Fact]
    public void NeitherWordingEndsWithPunctuationThatARowWouldRepeat()
    {
        // Both are dropped into a line that already has structure around it -
        // "manageable   no - <this>" on the command line, and a dim column in the
        // palette. A trailing full stop reads as a typo in both.
        foreach (ExclusionReason reason in Reasons)
        {
            ManageDecision decision = ManageDecision.No(reason);

            Assert.False(decision.Explain().EndsWith('.'));
            Assert.False(decision.Summarise().EndsWith('.'));
        }
    }
}
