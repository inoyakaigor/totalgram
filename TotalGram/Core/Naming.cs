using System.Text;

namespace TotalGram.Core
{
    /// A listing entry before its folder name is decided.
    public readonly record struct NameCandidate(string? Title, string? Slug, long Id);

    /// Names are plain titles. A title is only qualified — with the @username when there
    /// is one, otherwise with the numeric id — when it collides inside the same listing.
    public static class Naming
    {
        public const string AddAccount = "[+ Add account]";
        public const string AllChats = "All chats";
        public const string Archived = "Archived";

        /// Telegram hands the chat with yourself over as an ordinary user; every client
        /// substitutes this title, and so does the plugin.
        public const string SavedMessages = "Saved Messages";

        private const string Illegal = "\\/:*?\"<>|";

        public static string[] MakeUnique(IReadOnlyList<NameCandidate> items)
        {
            var titles = new string[items.Count];
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < items.Count; i++)
            {
                var t = Sanitize(items[i].Title);
                titles[i] = t.Length > 0 ? t : "Untitled";
                counts[titles[i]] = counts.GetValueOrDefault(titles[i]) + 1;
            }

            var names = new string[items.Count];
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < items.Count; i++)
            {
                var name = titles[i];
                if (counts[name] > 1)
                {
                    var slug = Sanitize(items[i].Slug);
                    name = slug.Length > 0
                        ? $"{name} (@{slug.TrimStart('@')})"
                        : $"{name} ({items[i].Id})";
                }
                // two chats can share both title and @username only in pathological cases
                if (!used.Add(name))
                {
                    name = $"{titles[i]} ({items[i].Id})";
                    for (int n = 2; !used.Add(name); n++) name = $"{titles[i]} ({items[i].Id}) #{n}";
                }
                names[i] = name;
            }
            return names;
        }

        public static string Sanitize(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
                sb.Append(c < ' ' || Illegal.IndexOf(c) >= 0 ? '_' : c);
            return sb.ToString().Trim().TrimEnd('.');
        }
    }
}
