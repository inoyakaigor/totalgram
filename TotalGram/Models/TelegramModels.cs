namespace TotalGram.Models
{
    public enum ContentCategory { Media, Files, Links, Music, Voice, GIF }

    /// The fixed set of folders shown first inside every chat.
    public static class VirtualFolders
    {
        public const string Media = "Media";
        public const string Files = "Files";
        public const string Links = "Links";
        public const string Music = "Music";
        public const string VoiceMessages = "Voice Messages";
        public const string GIF = "GIF";

        public static readonly string[] Order = { Media, Files, Links, Music, VoiceMessages, GIF };

        /// Every file of the chat as one flat list — Total Commander's branch view
        /// (Ctrl+B) is not available inside plugin filesystems, so this stands in for it.
        /// Deliberately not part of Order: it is not a content category.
        public const string AllFiles = "All files";

        /// Inside every content folder: the same files split by who sent them.
        public const string Incoming = "Incoming";
        public const string Outgoing = "Outgoing";

        /// True for outgoing, false for incoming, null when the name is neither.
        public static bool? ParseDirection(string name) =>
            name.Equals(Outgoing, StringComparison.OrdinalIgnoreCase) ? true
            : name.Equals(Incoming, StringComparison.OrdinalIgnoreCase) ? false
            : null;

        public static bool IsVirtualFolder(string name)
        {
            foreach (var f in Order)
                if (f.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static ContentCategory ParseCategory(string name) => name.ToLowerInvariant() switch
        {
            "files" => ContentCategory.Files,
            "links" => ContentCategory.Links,
            "music" => ContentCategory.Music,
            "voice messages" => ContentCategory.Voice,
            "gif" => ContentCategory.GIF,
            _ => ContentCategory.Media
        };
    }
}
