using Xunit;

namespace Crucible.Tray.Tests
{
    /// <summary>
    /// M113: the setup window describes the folder by its shape, never by the beta's name - on 11-04 the game's
    /// folder gets a name nobody knows yet, and the signed build friends download now has to be right then too.
    /// </summary>
    public class SetupWordsTests
    {
        private const string Shape = "your World of Warcraft: Forever game folder (it looks like _something_)";

        [Fact]
        public void The_label_the_hint_and_the_folder_picker_describe_the_folder_by_its_shape()
        {
            foreach (string words in new[] { SetupWords.FolderLabel, SetupWords.FolderWrong, SetupWords.BrowseDescription })
            {
                Assert.Contains(Shape, words);
                Assert.DoesNotContain("_classic_beta_", words);
                Assert.DoesNotContain("e.g.", words);
            }
            // The folder to pick is the one that HOLDS it: GameFolders.LooksLikeWow looks at the picked folder's children.
            Assert.Contains("holds", SetupWords.FolderLabel);
            Assert.Contains("holds", SetupWords.FolderWrong);
            Assert.Equal("Pick the folder first.", SetupWords.FolderEmpty);
        }
    }
}
