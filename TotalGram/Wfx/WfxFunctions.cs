using System.Runtime.InteropServices;
using TotalGram.Core;
using TotalGram.Models;

namespace TotalGram.Wfx
{
    public sealed class FsItem
    {
        public string Name = "";
        public bool IsDir;
        public long Size;
        public DateTime Date = DateTime.Now;
    }

    public static class WfxFunctions
    {
        public const int ERROR_NO_MORE_FILES = 18;

        [DllImport("kernel32.dll")]
        private static extern void SetLastError(uint dwErrCode);

        public static string GetDefRootName() => "Totalgram";

        // ---- find state ----

        private sealed class FindState
        {
            public List<FsItem> Items = new();
            public int Index;
        }

        private static readonly Dictionary<IntPtr, FindState> _finds = new();
        private static int _nextHandle = 1;

        internal static unsafe IntPtr FindFirst(string path, WIN32_FIND_DATAW* findData)
        {
            // set log_paths=1 in config.ini to see exactly what Total Commander asks for
            if (ApiCredentials.LogPaths) WfxEntryPoint.LogLine($"TotalGram: list '{path}'");

            List<FsItem> items;
            try { items = List(path); }
            catch (Exception ex) { WfxEntryPoint.LogError($"TotalGram: list failed: {ex.Message}"); items = new(); }

            if (items.Count == 0)
            {
                SetLastError(ERROR_NO_MORE_FILES);
                return new IntPtr(-1);
            }

            var state = new FindState { Items = items, Index = 1 };
            var handle = new IntPtr(_nextHandle++);
            _finds[handle] = state;
            Fill(findData, items[0]);
            return handle;
        }

        internal static unsafe int FindNext(IntPtr handle, WIN32_FIND_DATAW* findData)
        {
            if (!_finds.TryGetValue(handle, out var state)) return 0;
            if (state.Index >= state.Items.Count) return 0;
            Fill(findData, state.Items[state.Index++]);
            return 1;
        }

        internal static int FindClose(IntPtr handle)
        {
            _finds.Remove(handle);
            return 0;
        }

        private static unsafe void Fill(WIN32_FIND_DATAW* fd, FsItem item)
        {
            if (fd == null) return;
            var name = item.Name;
            char* dst = fd->cFileName;
            int n = Math.Min(name.Length, 259);
            for (int i = 0; i < n; i++) dst[i] = name[i];
            dst[n] = '\0';
            fd->cAlternateFileName[0] = '\0';
            fd->dwFileAttributes = item.IsDir ? WfxEntryPoint.FILE_ATTRIBUTE_DIRECTORY : WfxEntryPoint.FILE_ATTRIBUTE_NORMAL;
            fd->ftLastWriteTime = WfxEntryPoint.DateTimeToFileTime(item.Date);
            fd->ftCreationTime = fd->ftLastWriteTime;
            fd->ftLastAccessTime = fd->ftLastWriteTime;
            fd->nFileSizeHigh = (uint)(item.Size >> 32);
            fd->nFileSizeLow = (uint)(item.Size & 0xFFFFFFFF);
            fd->dwReserved0 = 0;
            fd->dwReserved1 = 0;
        }

        // ---- account and path resolution ----

        private static readonly HashSet<string> _reportedDeadSessions = new(StringComparer.OrdinalIgnoreCase);

        /// Resolves an account and lets Total Commander show its FTP log panel
        /// (and its Disconnect button) once a real connection exists.
        private static TelegramAccount? Account(string? displayName)
        {
            if (displayName == null) return null;

            var acc = AccountManager.Resolve(displayName);
            if (acc != null) { WfxEntryPoint.AnnounceConnect(); return acc; }

            var reason = AccountManager.LastError ?? "unknown error";
            WfxEntryPoint.LogError($"TotalGram: cannot open account '{displayName}': {reason}");
            if (_reportedDeadSessions.Add(displayName))
                WfxEntryPoint.MessageBox(
                    $"Could not open the Telegram session for:\r\n{displayName}\r\n\r\n" +
                    $"Reason:\r\n{reason}\r\n\r\n" +
                    $"Session files live in:\r\n{Paths.AccountsDir}");
            return null;
        }

        private static long ResolveFolder(TelegramAccount acc, Node node)
            => node.FolderName == null
                ? TelegramIds.AllChatsFolder
                : acc.ResolveFolderAsync(node.FolderName).GetAwaiter().GetResult() ?? -1;

        /// Everything the path below a chat means, once the chat's type is known.
        private sealed record InChat(
            TelegramAccount Acc,
            ChatEntry Chat,
            long TopicId,
            ContentCategory? Category,
            int PostId,
            string? FileName,
            bool AllFiles = false,
            /// true inside Outgoing, false inside Incoming, null in the folder itself
            bool? Outgoing = null);

        /// Consumes an Incoming/Outgoing segment if the path has one at this depth.
        private static bool? Direction(string[] tail, ref int i)
        {
            if (i >= tail.Length) return null;
            var dir = VirtualFolders.ParseDirection(tail[i]);
            if (dir != null) i++;
            return dir;
        }

        private static InChat? Resolve(Node node)
        {
            if (node.Type != NodeType.InChat) return null;

            var acc = Account(node.AccountName);
            if (acc == null) return null;

            var folderId = ResolveFolder(acc, node);
            if (folderId < 0 || node.ChatName == null) return null;

            var chat = acc.ResolveChatAsync(folderId, node.InArchive, node.ChatName).GetAwaiter().GetResult();
            if (chat == null) return null;

            var tail = node.Tail;
            var i = 0;
            long topicId = 0;

            // only a forum has a topic level, and a topic segment never looks like
            // a category, the flat list or a post, so the remaining shapes stay unambiguous
            if (chat.Kind == ChatKind.Forum && i < tail.Length
                && !VirtualFolders.IsVirtualFolder(tail[i]) && tail[i] != VirtualFolders.AllFiles
                && !PathParser.IsPost(tail[i], out _))
            {
                topicId = acc.ResolveTopicAsync(chat.Id, tail[i]).GetAwaiter().GetResult() ?? 0;
                i++;
            }

            if (i >= tail.Length)
                return new InChat(acc, chat, topicId, null, 0, null);

            if (tail[i] == VirtualFolders.AllFiles)
            {
                i++;
                var dir = Direction(tail, ref i);
                return new InChat(acc, chat, topicId, null, 0, i < tail.Length ? tail[i] : null,
                    AllFiles: true, Outgoing: dir);
            }

            if (VirtualFolders.IsVirtualFolder(tail[i]))
            {
                var category = VirtualFolders.ParseCategory(tail[i]);
                i++;
                var dir = Direction(tail, ref i);
                return new InChat(acc, chat, topicId, category, 0, i < tail.Length ? tail[i] : null,
                    Outgoing: dir);
            }

            if (PathParser.IsPost(tail[i], out var postId))
            {
                i++;
                return new InChat(acc, chat, topicId, null, postId, i < tail.Length ? tail[i] : null);
            }

            // a plain file sitting directly in a chat folder (private chats and groups)
            return new InChat(acc, chat, topicId, null, 0, tail[i]);
        }

        // ---- listing ----

        private static List<FsItem> List(string path)
        {
            var node = PathParser.Parse(path);
            return node.Type switch
            {
                NodeType.Root => ListRoot(),
                NodeType.Account => ListAccount(node),
                NodeType.FolderList => ListChats(node),
                NodeType.Archived => ListChats(node),
                NodeType.InChat => ListInChat(node),
                _ => new()
            };
        }

        private static List<FsItem> ListRoot()
        {
            var items = new List<FsItem>();
            foreach (var (_, name) in AccountManager.Listing())
                items.Add(new FsItem { Name = name, IsDir = true });
            items.Add(new FsItem { Name = Naming.AddAccount, IsDir = false, Size = 0 });
            return items;
        }

        private static List<FsItem> ListAccount(Node node)
        {
            var items = new List<FsItem>();
            var acc = Account(node.AccountName);
            if (acc == null) return items;

            foreach (var (_, name) in acc.FolderListingAsync().GetAwaiter().GetResult())
                items.Add(new FsItem { Name = name, IsDir = true });

            items.Add(new FsItem { Name = Naming.AllChats, IsDir = true });

            // shortcut to the chat with yourself, which belongs to no folder
            var saved = acc.ResolveChatAsync(TelegramIds.AllChatsFolder, false, Naming.SavedMessages)
                           .GetAwaiter().GetResult();
            if (saved != null)
                items.Add(new FsItem
                {
                    Name = Naming.SavedMessages,
                    IsDir = true,
                    Date = saved.Date == default ? DateTime.Now : saved.Date
                });

            return items;
        }

        private static List<FsItem> ListChats(Node node)
        {
            var items = new List<FsItem>();
            var acc = Account(node.AccountName);
            if (acc == null) return items;

            var folderId = ResolveFolder(acc, node);
            if (folderId < 0) return items;

            if (node.Type == NodeType.FolderList && folderId == TelegramIds.AllChatsFolder)
                items.Add(new FsItem { Name = Naming.Archived, IsDir = true });

            foreach (var (chat, name) in acc.ChatListingAsync(folderId, node.InArchive).GetAwaiter().GetResult())
                items.Add(new FsItem
                {
                    Name = name,
                    IsDir = true,
                    Date = chat.Date == default ? DateTime.Now : chat.Date
                });

            return items;
        }

        private static List<FsItem> ListInChat(Node node)
        {
            var at = Resolve(node);
            // a file name in the path means this is not a directory listing
            if (at == null || at.FileName != null) return new();

            if (at.AllFiles)
            {
                var flat = Directions(at);
                flat.AddRange(AllFilesOf(at)
                    .Select(f => new FsItem { Name = f.Name, IsDir = false, Size = f.Size, Date = f.Msg.date }));
                return flat;
            }

            if (at.Category != null)
            {
                var items = Directions(at);
                items.AddRange(FilesOf(at.Acc, at.Chat.Id, at.TopicId, at.Category, at.Outgoing));
                return items;
            }

            if (at.PostId != 0)
                return ListPost(at);

            return ListChatRoot(at);
        }

        /// The Incoming/Outgoing pair, shown in a content folder but not inside one.
        private static List<FsItem> Directions(InChat at)
        {
            var items = new List<FsItem>();
            if (at.Outgoing != null) return items;
            items.Add(new FsItem { Name = VirtualFolders.Incoming, IsDir = true, Date = at.Chat.Date });
            items.Add(new FsItem { Name = VirtualFolders.Outgoing, IsDir = true, Date = at.Chat.Date });
            return items;
        }

        private static List<FsItem> ListChatRoot(InChat at)
        {
            // a forum shows its topics; everything else works off the message list
            if (at.Chat.Kind == ChatKind.Forum && at.TopicId == 0)
            {
                var forumItems = Categories(new(), at.Chat.Date);
                foreach (var (_, name) in at.Acc.TopicListingAsync(at.Chat.Id).GetAwaiter().GetResult())
                    forumItems.Add(new FsItem { Name = name, IsDir = true, Date = at.Chat.Date });
                return forumItems;
            }

            var items = Categories(CategoryDates(at), at.Chat.Date);

            var postsPerMessage = at.Chat.Kind == ChatKind.Channel || at.TopicId != 0;
            if (postsPerMessage)
            {
                foreach (var m in Messages(at.Acc, at.Chat.Id, at.TopicId))
                    items.Add(new FsItem { Name = PathParser.PostName(m.id), IsDir = true, Date = m.date });
            }
            else
            {
                // private chats and plain groups are one flat folder holding everything
                items.AddRange(FilesOf(at.Acc, at.Chat.Id, at.TopicId, null, null));
            }
            return items;
        }

        private static List<FsItem> ListPost(InChat at)
        {
            var msg = at.Acc.GetCachedMessage(at.Chat.Id, at.PostId);
            if (msg == null)
            {
                Messages(at.Acc, at.Chat.Id, at.TopicId);
                msg = at.Acc.GetCachedMessage(at.Chat.Id, at.PostId);
            }
            if (msg == null) return new();

            var items = new List<FsItem>();
            var media = MediaClassifier.Describe(msg);
            if (media != null)
                items.Add(new FsItem { Name = media.FileName, IsDir = false, Size = media.Size, Date = msg.date });

            var url = MediaClassifier.FirstUrl(msg);
            if (url != null)
                items.Add(new FsItem
                {
                    Name = MediaClassifier.UrlFileName(msg),
                    IsDir = false,
                    Size = MediaClassifier.UrlFileBody(url).Length,
                    Date = msg.date
                });

            if (MediaClassifier.TextBody(msg) != null)
                items.Add(new FsItem
                {
                    Name = MediaClassifier.TextFileName,
                    IsDir = false,
                    Size = MediaClassifier.TextSize(msg),
                    Date = msg.date
                });

            return items;
        }

        private sealed record FlatFile(TL.Message Msg, string Name, long Size, bool IsUrl);

        private static bool IsOutgoing(TL.Message msg) => msg.flags.HasFlag(TL.Message.Flags.out_);

        /// Every downloadable file of the chat in one list, across topics and posts.
        /// Names repeat across messages, so colliding ones carry their message id.
        private static List<FlatFile> AllFilesOf(InChat at)
        {
            var messages = new List<TL.Message>();

            // at the root of a forum the flat list spans every topic
            if (at.Chat.Kind == ChatKind.Forum && at.TopicId == 0)
                foreach (var (topicId, _) in at.Acc.TopicListingAsync(at.Chat.Id).GetAwaiter().GetResult())
                    messages.AddRange(Files(at.Acc, at.Chat.Id, topicId, null));
            else
                messages.AddRange(Files(at.Acc, at.Chat.Id, at.TopicId, null));

            var raw = new List<FlatFile>();
            foreach (var msg in messages)
            {
                if (at.Outgoing != null && IsOutgoing(msg) != at.Outgoing) continue;

                var media = MediaClassifier.Describe(msg);
                if (media != null) raw.Add(new FlatFile(msg, media.FileName, media.Size, false));

                var url = MediaClassifier.FirstUrl(msg);
                if (url != null)
                    raw.Add(new FlatFile(msg, MediaClassifier.UrlFileName(msg),
                        MediaClassifier.UrlFileBody(url).Length, true));
            }

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in raw) counts[f.Name] = counts.GetValueOrDefault(f.Name) + 1;

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<FlatFile>(raw.Count);
            foreach (var f in raw)
            {
                var name = f.Name;
                if (counts[name] > 1)
                    name = $"{Path.GetFileNameWithoutExtension(name)} ({f.Msg.id}){Path.GetExtension(name)}";
                while (!used.Add(name))
                    name = $"{Path.GetFileNameWithoutExtension(name)}_{Path.GetExtension(name)}";
                result.Add(f with { Name = name });
            }
            return result;
        }

        private static readonly Dictionary<(string, long, long), (DateTime At, Dictionary<ContentCategory, DateTime> Dates)> _dateCache = new();

        private static Dictionary<ContentCategory, DateTime> CategoryDates(InChat at)
        {
            var key = (at.Acc.Key, at.Chat.Id, at.TopicId);
            if (_dateCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheTtl)
                return hit.Dates;

            var dates = at.Acc.GetCategoryDatesAsync(at.Chat.Id, at.TopicId).GetAwaiter().GetResult();
            _dateCache[key] = (DateTime.UtcNow, dates);
            return dates;
        }

        /// The fixed folders, dated by the newest message each one holds so that
        /// sorting by date is meaningful.
        private static List<FsItem> Categories(Dictionary<ContentCategory, DateTime> newest, DateTime fallback)
        {
            var items = new List<FsItem>();
            foreach (var name in VirtualFolders.Order)
            {
                var category = VirtualFolders.ParseCategory(name);
                items.Add(new FsItem
                {
                    Name = name,
                    IsDir = true,
                    Date = newest.TryGetValue(category, out var d) ? d
                         : fallback == default ? DateTime.Now : fallback
                });
            }

            items.Add(new FsItem
            {
                Name = VirtualFolders.AllFiles,
                IsDir = true,
                Date = newest.Count > 0 ? newest.Values.Max()
                     : fallback == default ? DateTime.Now : fallback
            });
            return items;
        }

        /// Files of one kind (or of every kind when `category` is null), optionally
        /// narrowed to the ones sent by this account or the ones received.
        private static List<FsItem> FilesOf(TelegramAccount acc, long chatId, long topicId,
            ContentCategory? category, bool? outgoing)
        {
            var items = new List<FsItem>();
            foreach (var msg in Files(acc, chatId, topicId, category))
            {
                if (outgoing != null && IsOutgoing(msg) != outgoing) continue;

                if (category == ContentCategory.Links)
                {
                    var url = MediaClassifier.FirstUrl(msg);
                    if (url == null) continue;
                    items.Add(new FsItem
                    {
                        Name = MediaClassifier.UrlFileName(msg),
                        IsDir = false,
                        Size = MediaClassifier.UrlFileBody(url).Length,
                        Date = msg.date
                    });
                    continue;
                }

                var media = MediaClassifier.Describe(msg);
                if (media == null) continue;

                // No second guess at the kind: Telegram already answered that by picking
                // the messages, and its answer is what its own "Shared media" tabs show.
                // A .MOV sent uncompressed keeps a video mime yet belongs under Files,
                // and judging it here again dropped it from every folder at once.

                items.Add(new FsItem { Name = media.FileName, IsDir = false, Size = media.Size, Date = msg.date });
            }
            return items;
        }

        // ---- message cache ----

        private static readonly Dictionary<(string, long, long), (DateTime At, List<TL.Message> Items)> _msgCache = new();
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

        private static List<TL.Message> Messages(TelegramAccount acc, long chatId, long topicId)
        {
            var key = (acc.Key, chatId, topicId);
            if (_msgCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheTtl)
                return hit.Items;

            var list = acc.GetMessagesAsync(chatId, topicId).GetAwaiter().GetResult();
            _msgCache[key] = (DateTime.UtcNow, list);
            return list;
        }

        private static readonly Dictionary<(string, long, long, ContentCategory?), (DateTime At, List<TL.Message> Items)> _fileCache = new();

        /// Messages carrying files, fetched by kind straight from Telegram.
        private static List<TL.Message> Files(TelegramAccount acc, long chatId, long topicId, ContentCategory? category)
        {
            var key = (acc.Key, chatId, topicId, category);
            if (_fileCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheTtl)
                return hit.Items;

            var list = acc.GetFilesAsync(chatId, topicId, category).GetAwaiter().GetResult();
            _fileCache[key] = (DateTime.UtcNow, list);
            return list;
        }

        // ---- transfers ----

        internal static TransferResult GetFile(string remotePath, string localPath, long expectedSize)
        {
            var at = Resolve(PathParser.Parse(remotePath));
            if (at?.FileName == null) return TransferResult.Failed;

            using var transfer = new WfxEntryPoint.Transfer(remotePath, localPath, expectedSize);
            transfer.Report(0, 1);

            var outcome = Download(at, localPath, transfer);
            transfer.Report(1, 1);
            return transfer.Aborted ? TransferResult.Aborted : outcome;
        }

        private static TransferResult Download(InChat at, string localPath, WfxEntryPoint.Transfer transfer)
        {
            // the flat list renames colliding files, so it resolves against its own listing
            if (at.AllFiles)
            {
                var entry = AllFilesOf(at).FirstOrDefault(f =>
                    string.Equals(f.Name, at.FileName, StringComparison.OrdinalIgnoreCase));
                if (entry == null) return TransferResult.Failed;

                if (entry.IsUrl) return WriteText(localPath, MediaClassifier.FirstUrl(entry.Msg) is { } u
                    ? MediaClassifier.UrlFileBody(u) : null);

                return Fetch(at, entry.Msg, localPath, transfer);
            }

            // resolve against the same source the listing came from
            var messages = at.PostId != 0
                ? Messages(at.Acc, at.Chat.Id, at.TopicId)
                : Files(at.Acc, at.Chat.Id, at.TopicId, at.Category);

            // the post's text is written from what the listing already holds
            if (at.PostId != 0 && string.Equals(at.FileName, MediaClassifier.TextFileName, StringComparison.OrdinalIgnoreCase))
            {
                var post = at.Acc.GetCachedMessage(at.Chat.Id, at.PostId);
                return WriteText(localPath, post == null ? null : MediaClassifier.TextBody(post));
            }

            // .url files are generated locally, never downloaded
            foreach (var msg in messages)
            {
                if (!string.Equals(MediaClassifier.UrlFileName(msg), at.FileName, StringComparison.OrdinalIgnoreCase)) continue;
                var url = MediaClassifier.FirstUrl(msg);
                if (url == null) continue;
                return WriteText(localPath, MediaClassifier.UrlFileBody(url));
            }

            var target = at.PostId != 0
                ? at.Acc.GetCachedMessage(at.Chat.Id, at.PostId)
                : messages.FirstOrDefault(m =>
                    string.Equals(MediaClassifier.Describe(m)?.FileName, at.FileName, StringComparison.OrdinalIgnoreCase));

            if (target == null)
            {
                WfxEntryPoint.LogError($"TotalGram: no message holds '{at.FileName}'");
                return TransferResult.Failed;
            }
            return Fetch(at, target, localPath, transfer);
        }

        private static TransferResult Fetch(InChat at, TL.Message msg, string localPath, WfxEntryPoint.Transfer transfer)
            => transfer.Pump(at.Acc.DownloadAsync(msg, localPath, transfer.Note))
                ? TransferResult.Ok
                : TransferResult.Failed;

        /// Files the plugin makes up locally: the post's text and the .url shortcuts.
        private static TransferResult WriteText(string localPath, string? content)
        {
            if (content == null) return TransferResult.Failed;
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            File.WriteAllText(localPath, content, new System.Text.UTF8Encoding(false));
            return TransferResult.Ok;
        }

        /// Thumbnail for one file, downloaded to TEMP for Total Commander to decode.
        internal static string? PreviewFile(string remotePath, int width, int height)
        {
            var at = Resolve(PathParser.Parse(remotePath));
            if (at?.FileName == null) return null;

            // message.md and the .url shortcuts are generated by the plugin and share a
            // message with the real attachment, so they must not inherit its picture
            if (string.Equals(at.FileName, MediaClassifier.TextFileName, StringComparison.OrdinalIgnoreCase)) return null;
            if (Path.GetExtension(at.FileName).Equals(".url", StringComparison.OrdinalIgnoreCase)) return null;

            var msg = FindMessageFor(at);
            return msg == null ? null : at.Acc.DownloadPreviewAsync(msg, width, height).GetAwaiter().GetResult();
        }

        /// The message a file path points at, whichever folder it was listed in.
        private static TL.Message? FindMessageFor(InChat at)
        {
            if (at.AllFiles)
                return AllFilesOf(at).FirstOrDefault(f =>
                    string.Equals(f.Name, at.FileName, StringComparison.OrdinalIgnoreCase))?.Msg;

            if (at.PostId != 0)
                return at.Acc.GetCachedMessage(at.Chat.Id, at.PostId);

            return Files(at.Acc, at.Chat.Id, at.TopicId, at.Category).FirstOrDefault(m =>
                string.Equals(MediaClassifier.Describe(m)?.FileName, at.FileName, StringComparison.OrdinalIgnoreCase));
        }

        internal static TransferResult PutFile(string localPath, string remotePath)
        {
            var at = Resolve(PathParser.Parse(remotePath));
            if (at == null) return TransferResult.Failed;

            var rejection = MediaClassifier.RejectUpload(at.Category, localPath);
            if (rejection != null)
            {
                WfxEntryPoint.LogError($"TotalGram: {rejection}");
                WfxEntryPoint.MessageBox($"{rejection}\r\n\r\n{Path.GetFileName(localPath)}");
                return TransferResult.Failed;
            }

            if (at.Outgoing == false) WarnIncomingUpload(localPath);

            using var transfer = new WfxEntryPoint.Transfer(localPath, remotePath, new FileInfo(localPath).Length);

            var sent = transfer.Pump(
                at.Acc.UploadAsync(at.Chat.Id, at.TopicId, localPath, at.Category, transfer.Note));

            if (transfer.Aborted) return TransferResult.Aborted;
            if (!sent) return TransferResult.Failed;

            // the chat changed, so the caches must not serve the listing from before it
            _msgCache.Remove((at.Acc.Key, at.Chat.Id, at.TopicId));
            foreach (var key in _fileCache.Keys.Where(k => k.Item1 == at.Acc.Key && k.Item2 == at.Chat.Id).ToList())
                _fileCache.Remove(key);
            return TransferResult.Ok;
        }

        private static bool _incomingWarned;

        /// Incoming and Outgoing only split what the chat already holds, so a file copied
        /// into Incoming is still sent and still turns up under Outgoing. The dialog comes
        /// once per session - one per file would be unusable for a whole selection - while
        /// the log says it every time.
        private static void WarnIncomingUpload(string localPath)
        {
            WfxEntryPoint.LogLine(
                $"TotalGram: {Path.GetFileName(localPath)} is sent from this account, " +
                $"so it will appear under {VirtualFolders.Outgoing}");

            if (_incomingWarned) return;
            _incomingWarned = true;
            WfxEntryPoint.MessageBox(
                $"{VirtualFolders.Incoming} and {VirtualFolders.Outgoing} are only two views " +
                $"of what the chat already holds.\r\n\r\n" +
                $"The file is sent from your account, so it will appear under " +
                $"{VirtualFolders.Outgoing} rather than here.");
        }

        internal enum TransferResult { Ok, Failed, Aborted }

        /// Enter on "[+ Add account]" — runs the login prompts through Total Commander.
        /// Returns the new account's display name so the caller can send TC into it.
        internal static string? AddAccount()
        {
            if (!ApiCredentials.Configured)
            {
                WfxEntryPoint.MessageBox(
                    $"api_id / api_hash are not set.\r\n\r\nCreate them at https://my.telegram.org (API development tools) and put them in:\r\n{ApiCredentials.ConfigPath}\r\n\r\napi_id=1234567\r\napi_hash=0123456789abcdef...");
                return null;
            }

            var name = AccountManager.AddAccount(AuthPrompt.Ask);
            if (name == null) WfxEntryPoint.LogLine("TotalGram: account was not added");
            else WfxEntryPoint.LogOperation($"TotalGram: account added: {name}");
            return name;
        }
    }
}
