namespace Crucible.Tray
{
    /// <summary>
    /// The setup window's words about the folder to pick (M113). They describe the game's folder by its shape and never
    /// by the beta's name: at launch it gets a name nobody knows yet (<see cref="ForeverProducts"/>), and this build has
    /// to read right then too. The folder to pick is the one that HOLDS it - <see cref="GameFolders.LooksLikeWow"/>
    /// looks at the picked folder's children.
    /// </summary>
    public static class SetupWords
    {
        public const string FolderLabel =
            "Your World of Warcraft folder - the one that holds your World of Warcraft: Forever game folder (it looks like _something_):";

        public const string FolderEmpty = "Pick the folder first.";

        public const string FolderWrong =
            "That is not it. Pick the folder that holds your World of Warcraft: Forever game folder (it looks like _something_) - "
            + "if you picked that folder itself, pick the one above it.";

        public const string BrowseDescription =
            "Pick the folder that holds your World of Warcraft: Forever game folder (it looks like _something_)";

        /// <summary>Beside the Crucible Canvas switch (spec 2026-10-15 §8).</summary>
        public const string CanvasSwitch = "If you turn on Crucible Canvas, Crucible will also move and resize your WoW window to span the two screens you choose; it never reads or changes anything inside the game.";

        /// <summary>The Displays window (spec 2026-10-15 §6.5): its title and its three switches.</summary>
        public const string DisplaysTitle = "Crucible - Displays";

        public const string CanvasEnable = "Use Crucible Canvas (two screens)";

        public const string CanvasAuto = "Span automatically when WoW starts, after";

        public const string CanvasSplit = "Single wide display, split in half";
    }
}
