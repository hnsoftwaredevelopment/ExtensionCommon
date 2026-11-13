namespace ExtensionCommon.VisualStudio
{
    /// <summary>A folder with an extension.vsixmanifest: an installed extension or a part of VS itself.</summary>
    internal sealed class ExtensionSource
    {
        /// <summary>Owner name used for every folder of VS itself.</summary>
        public const string VisualStudioName = "Visual Studio";

        public ExtensionSource(string folder, string owner, bool isVisualStudio)
        {
            Folder = folder;
            Owner = owner;
            IsVisualStudio = isVisualStudio;
        }

        public string Folder { get; }

        /// <summary>Display name; null means: take it from the manifest.</summary>
        public string Owner { get; }
        public bool IsVisualStudio { get; }
    }
}
