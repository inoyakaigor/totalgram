namespace TotalGram.Core
{
    public enum NodeType
    {
        Root,           // \                                  -> accounts + [+ Add account]
        AddAccount,     // \[+ Add account]                   -> pseudo-file, Enter starts login
        Account,        // \acct\                             -> telegram folders + All chats
        FolderList,     // \acct\All chats\ or \acct\Folder\   -> chats (+ Archived under All chats)
        Archived,       // \acct\All chats\Archived\          -> archived chats
        InChat,         // \...\Chat\...                      -> needs the chat's type to go further
        Unknown
    }

    /// Splits a panel path into the parts that can be understood without talking to
    /// Telegram. Everything below the chat stays in Tail: whether a segment is a forum
    /// topic, a category, a post or a plain file depends on what kind of chat it is,
    /// so that decision belongs to whoever can resolve the chat (see WfxFunctions).
    public sealed class Node
    {
        public NodeType Type = NodeType.Unknown;
        public string? AccountName;
        public string? FolderName;      // null means "All chats"
        public bool InArchive;
        public string? ChatName;
        public string[] Tail = Array.Empty<string>();
    }

    public static class TelegramIds
    {
        public const long AllChatsFolder = 0;
        public const long ArchiveFolder = 1;
    }

    public static class PathParser
    {
        public const string PostPrefix = "Post ";

        public static string PostName(int id) => PostPrefix + id;

        public static bool IsPost(string name, out int id)
        {
            id = 0;
            return name.StartsWith(PostPrefix, StringComparison.Ordinal)
                && int.TryParse(name.AsSpan(PostPrefix.Length), out id);
        }

        public static Node Parse(string path)
        {
            var p = (path ?? "").Split('\\', StringSplitOptions.RemoveEmptyEntries);
            // some Total Commander commands append a search mask to the directory
            if (p.Length > 0 && (p[^1] == "*.*" || p[^1] == "*")) p = p[..^1];
            var n = new Node();

            if (p.Length == 0) { n.Type = NodeType.Root; return n; }
            if (p.Length == 1 && p[0] == Naming.AddAccount) { n.Type = NodeType.AddAccount; return n; }

            n.AccountName = p[0];
            if (p.Length == 1) { n.Type = NodeType.Account; return n; }

            int i = 1;

            // Saved Messages sits beside the folders as a shortcut to that one chat,
            // so at this depth it names a chat rather than a folder
            if (p[i] == Naming.SavedMessages)
            {
                n.ChatName = p[i];
                n.Tail = p[(i + 1)..];
                n.Type = NodeType.InChat;
                return n;
            }

            n.FolderName = p[i] == Naming.AllChats ? null : p[i];
            i++;
            if (i == p.Length) { n.Type = NodeType.FolderList; return n; }

            if (p[i] == Naming.Archived && n.FolderName == null)
            {
                n.InArchive = true;
                i++;
                if (i == p.Length) { n.Type = NodeType.Archived; return n; }
            }

            n.ChatName = p[i];
            i++;
            n.Tail = p[i..];
            n.Type = NodeType.InChat;
            return n;
        }
    }
}
