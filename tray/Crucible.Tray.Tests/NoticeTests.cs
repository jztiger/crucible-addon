using Xunit;

namespace Crucible.Tray.Tests
{
    /// <summary>
    /// A notice after version 6 is shown once and never forced. Spec 2026-10-15 §8, owner's ruling 2026-10-16: v7 (Canvas) was shown
    /// once and a v6 acceptance counted; it shipped with tray 0.100.0 (2026-10-18). Spec 2026-10-17 §11, owner's ruling 2026-10-18:
    /// v8 (the simulator) comes with tray 0.101.0 and is not forced either - an acceptance of version 6 or later counts for it.
    /// </summary>
    public class NoticeTests
    {
        [Theory]
        [InlineData(6, 6, true)] [InlineData(7, 7, true)] [InlineData(6, 7, true)]   // a later scope notice is never re-accepted: v6 counts
        [InlineData(5, 7, false)] [InlineData(5, 6, false)] [InlineData(0, 7, false)] [InlineData(6, 0, false)]
        public void IsAccepted(int accepted, int current, bool expected) => Assert.Equal(expected, Notice.IsAccepted(accepted, current));

        [Theory]
        [InlineData(8, 8, true)] [InlineData(7, 8, true)] [InlineData(6, 8, true)] [InlineData(5, 8, false)] [InlineData(0, 8, false)]
        public void Version8IsNeverForcedOnAScopeNoticeAcceptance(int accepted, int current, bool expected) =>
            Assert.Equal(expected, Notice.IsAccepted(accepted, current));

        [Theory]
        [InlineData(0, 7, true)] [InlineData(6, 7, true)] [InlineData(7, 7, false)] [InlineData(8, 7, false)] [InlineData(0, 0, false)]
        [InlineData(7, 8, true)] [InlineData(8, 8, false)]   // v8 is shown once, with Close, even to a tray that already showed v7 (tray 0.100.0)
        public void NeedsShowing(int shown, int current, bool expected) => Assert.Equal(expected, Notice.NeedsShowing(shown, current));

        [Fact]
        public void TheCanvasSentenceIsTheSpecs() =>
            Assert.Equal("If you turn on Crucible Canvas, Crucible will also move and resize your WoW window to span the two screens you choose; it never reads or changes anything inside the game.", SetupWords.CanvasSwitch);

        /// <summary>
        /// The line under the notice the first time a tray shows a carried-over version (controller's ruling, task 11 review). It said
        /// "Nothing changes unless you turn Crucible Canvas on" - which version 8 contradicts: the simulator is on by default, and the
        /// notice says where it is switched off (the Your PCs page). Pinned word for word, like the Canvas sentence.
        /// </summary>
        [Fact]
        public void TheFirstShowingHintSaysWhatChangedAndThatNothingIsToBeAccepted() =>
            Assert.Equal("Nothing to accept. New: the simulator is on unless you switch it off on the website's Your PCs page. Read this any time under Help > About Crucible.", SetupWords.NoticeFirstShowingHint);
    }
}
