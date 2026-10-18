using Xunit;

namespace Crucible.Tray.Tests
{
    /// <summary>Spec 2026-10-15 §8, owner's ruling 2026-10-16: v7 is shown once and never re-accepted; a v6 acceptance counts.</summary>
    public class NoticeTests
    {
        [Theory]
        [InlineData(6, 6, true)] [InlineData(7, 7, true)] [InlineData(6, 7, true)]   // the one carry-over
        [InlineData(5, 7, false)] [InlineData(5, 6, false)] [InlineData(0, 7, false)] [InlineData(6, 0, false)]
        public void IsAccepted(int accepted, int current, bool expected) => Assert.Equal(expected, Notice.IsAccepted(accepted, current));

        [Theory]
        [InlineData(0, 7, true)] [InlineData(6, 7, true)] [InlineData(7, 7, false)] [InlineData(8, 7, false)] [InlineData(0, 0, false)]
        public void NeedsShowing(int shown, int current, bool expected) => Assert.Equal(expected, Notice.NeedsShowing(shown, current));

        [Fact]
        public void TheCanvasSentenceIsTheSpecs() =>
            Assert.Equal("If you turn on Crucible Canvas, Crucible will also move and resize your WoW window to span the two screens you choose; it never reads or changes anything inside the game.", SetupWords.CanvasSwitch);
    }
}
