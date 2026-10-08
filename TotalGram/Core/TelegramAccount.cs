using TL;
using TotalGram.Models;

namespace TotalGram.Core
{
    /// One logged-in Telegram account. Owns its WTelegram client, its session
    /// file and the TL object caches needed to turn a path back into an API call.
    public sealed class TelegramAccount : IDisposable
    {
        private WTelegram.Client? _client;
        private readonly Dictionary<long, InputPeer> _peers = new();
        private readonly Dictionary<long, ChatEntry> _chats = new();
        private readonly Dictionary<(long chat, int msg), TL.Message> _messages = new();
        private readonly Dictionary<long, bool> _isForum = new();

        /// Base name of the session file. Internal only — it keeps the numeric id so two
        /// accounts with the same display name cannot overwrite each other on disk.
        public string Key { get; private set; }
        public string SessionPath { get; }

        /// What the user sees: their Telegram name, without any id.
        public string Title { get; private set; }
        public long UserId { get; private set; }
        public string? Slug => Me?.MainUsername;

        public User? Me { get; private set; }
        public bool IsLoggedIn => _client != null && Me != null;

        public event Action<string>? Log;

        public TelegramAccount(string key, string sessionPath)
        {
            Key = key;
            SessionPath = sessionPath;
            (Title, UserId) = SplitKey(key);
        }

        /// Session files are named "Title (userId)".
        private static (string Title, long Id) SplitKey(string key)
        {
            if (key.EndsWith(')'))
            {
                var open = key.LastIndexOf('(');
                if (open > 0 && long.TryParse(key.AsSpan(open + 1, key.Length - open - 2), out var id))
                    return (key[..open].TrimEnd(), id);
            }
            return (key, 0);
        }

        private void AdoptIdentity(User me)
        {
            Me = me;
            Title = DisplayName(me);
            UserId = me.id;
            Key = $"{Naming.Sanitize(Title)} ({me.id})";
        }

        /// Set only while an interactive login is running. When it is null the client
        /// must never ask the user anything — a directory listing may not open dialogs.
        private Func<string, string?>? _ask;
        private string? _phone;

        private WTelegram.Client CreateClient()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
            return new WTelegram.Client(Config!);
        }

        private string? Config(string what) => what switch
        {
            "session_pathname" => SessionPath,
            "api_id" => ApiCredentials.ApiId,
            "api_hash" => ApiCredentials.ApiHash,
            "device_model" => "Total Commander",
            "system_version" => "Windows",
            "app_version" => "TotalGram 1.0",
            "lang_code" => "en",
            // Without this WTelegramClient cannot validate a resumed session and falls back
            // to a full login, even though the stored auth key is perfectly valid.
            "user_id" => UserId != 0 ? UserId.ToString() : null,
            "phone_number" => _phone ?? _ask?.Invoke(what),
            "verification_code" or "password" or "first_name" or "last_name"
                or "email" or "email_verification_code" => _ask?.Invoke(what),
            _ => null
        };

        /// Resumes the stored session. Returns false when it can no longer be resumed —
        /// with _ask left null the client cannot prompt, so it throws and we report it.
        public async Task<bool> ConnectAsync()
        {
            if (IsLoggedIn) return true;
            try
            {
                _ask = null;
                _phone = null;
                _client ??= CreateClient();
                var me = await _client.LoginUserIfNeeded();
                if (me == null)
                {
                    LastError = "LoginUserIfNeeded returned no user";
                    Log?.Invoke($"TotalGram: session for {Title} is not usable");
                    Dispose();
                    return false;
                }
                AdoptIdentity(me);
                Log?.Invoke($"TotalGram: connected as {Title}");
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log?.Invoke($"TotalGram: cannot resume session '{Title}': {ex.Message}");
                // the client holds the .session file open — release it so the file is not
                // locked for the rest of the Total Commander session, and so a retry starts clean
                Dispose();
                return false;
            }
        }

        public string? LastError { get; private set; }

        /// Full interactive login. `ask` is called for each value Telegram wants
        /// ("verification_code", "password", ...) and returns what the user typed.
        public async Task<bool> LoginAsync(string phone, Func<string, string?> ask)
        {
            try
            {
                _phone = phone;
                _ask = ask;
                _client ??= CreateClient();
                var me = await _client.LoginUserIfNeeded();
                if (me == null) return false;
                AdoptIdentity(me);
                Log?.Invoke($"TotalGram: logged in as {Title}");
                return true;
            }
            catch (Exception ex) { Log?.Invoke($"TotalGram: login failed: {ex.Message}"); return false; }
            finally { _ask = null; _phone = null; }
        }

        public static string DisplayName(User u) =>
            string.Join(' ', new[] { u.first_name, u.last_name }.Where(s => !string.IsNullOrEmpty(s)));

        // ---- folders (dialog filters) ----

        public async Task<List<(long Id, string Title)>> GetFoldersAsync()
        {
            var result = new List<(long, string)>();
            if (!IsLoggedIn) return result;
            try
            {
                var filters = await _client!.Messages_GetDialogFilters();
                foreach (var f in filters.filters)
                    switch (f)
                    {
                        case DialogFilter df: result.Add((df.id, df.title?.text ?? "Folder")); break;
                        // shared chat-list folders are ordinary folders in the UI
                        case DialogFilterChatlist cl: result.Add((cl.id, cl.title?.text ?? "Folder")); break;
                    }
            }
            catch (Exception ex) { Log?.Invoke($"TotalGram: folders failed: {ex.Message}"); }
            return result;
        }

        // ---- display names and reverse lookup ----
        // Panel paths carry names, not ids, so every lookup rebuilds the same names
        // the listing produced and matches against them.

        public async Task<(long Id, string Name)[]> FolderListingAsync()
        {
            var folders = await GetFoldersAsync();
            var names = Naming.MakeUnique(
                folders.Select(f => new NameCandidate(f.Title, null, f.Id)).ToList());
            return folders.Select((f, i) => (f.Id, names[i])).ToArray();
        }

        public async Task<long?> ResolveFolderAsync(string name)
        {
            foreach (var (id, n) in await FolderListingAsync())
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return id;
            return null;
        }

        public async Task<(ChatEntry Chat, string Name)[]> ChatListingAsync(long folderId, bool archived)
        {
            var chats = await GetChatsAsync(folderId, archived);
            var names = Naming.MakeUnique(
                chats.Select(c => new NameCandidate(c.Title, c.Slug, c.Id)).ToList());
            return chats.Select((c, i) => (c, names[i])).ToArray();
        }

        public async Task<ChatEntry?> ResolveChatAsync(long folderId, bool archived, string name)
        {
            foreach (var (chat, n) in await ChatListingAsync(folderId, archived))
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return chat;
            return null;
        }

        public async Task<(long Id, string Name)[]> TopicListingAsync(long chatId)
        {
            var topics = await GetTopicsAsync(chatId);
            var names = Naming.MakeUnique(
                topics.Select(t => new NameCandidate(t.Title, null, t.Id)).ToList());
            return topics.Select((t, i) => (t.Id, names[i])).ToArray();
        }

        public async Task<long?> ResolveTopicAsync(long chatId, string name)
        {
            foreach (var (id, n) in await TopicListingAsync(chatId))
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return id;
            return null;
        }

        // ---- chats ----

        /// Dialog order as Telegram returns it (most recent first), used to order listings.
        private readonly List<long> _dialogOrder = new();
        private readonly HashSet<long> _dialogSeen = new();
        private bool _dialogsLoaded;

        private async Task EnsureDialogsAsync()
        {
            if (_dialogsLoaded) return;
            // both folders are needed: a user folder may include archived chats
            var main = await _client!.Messages_GetAllDialogs((int)TelegramIds.AllChatsFolder);
            Absorb(main, archived: false);
            var archive = await _client!.Messages_GetAllDialogs((int)TelegramIds.ArchiveFolder);
            Absorb(archive, archived: true);
            _dialogsLoaded = true;
        }

        public async Task<List<ChatEntry>> GetChatsAsync(long folderId, bool archived)
        {
            var list = new List<ChatEntry>();
            if (!IsLoggedIn) return list;
            try
            {
                await EnsureDialogsAsync();

                if (folderId != TelegramIds.AllChatsFolder)
                    return await GetChatsInFolderAsync(folderId);

                foreach (var id in _dialogOrder)
                    if (_chats.TryGetValue(id, out var e) && e.IsArchived == archived)
                        list.Add(e);
            }
            catch (Exception ex) { Log?.Invoke($"TotalGram: chats failed: {ex.Message}"); }
            return list;
        }

        /// Reproduces Telegram's folder rules: explicit peers plus whole classes of chats
        /// selected by the filter's flags, minus the explicitly excluded ones.
        private async Task<List<ChatEntry>> GetChatsInFolderAsync(long folderId)
        {
            var list = new List<ChatEntry>();
            var filters = await _client!.Messages_GetDialogFilters();

            InputPeer[]? pinned = null, include = null, exclude = null;
            DialogFilter.Flags flags = 0;
            var found = false;

            foreach (var f in filters.filters)
            {
                if (f is DialogFilter df && df.id == folderId)
                {
                    pinned = df.pinned_peers; include = df.include_peers; exclude = df.exclude_peers;
                    flags = df.flags; found = true; break;
                }
                // a chat-list folder has no class rules, only an explicit set of chats
                if (f is DialogFilterChatlist cl && cl.id == folderId)
                {
                    pinned = cl.pinned_peers; include = cl.include_peers;
                    found = true; break;
                }
            }
            if (!found) return list;

            var excluded = new HashSet<long>();
            foreach (var p in exclude ?? Array.Empty<InputPeer>())
            {
                var id = PeerId(p);
                if (id != 0) excluded.Add(id);
            }

            var added = new HashSet<long>();

            // pinned peers keep their position at the top, like in the Telegram client
            foreach (var p in pinned ?? Array.Empty<InputPeer>()) TryAdd(PeerId(p));
            foreach (var p in include ?? Array.Empty<InputPeer>()) TryAdd(PeerId(p));

            var wantsClasses = flags.HasFlag(DialogFilter.Flags.contacts)
                || flags.HasFlag(DialogFilter.Flags.non_contacts)
                || flags.HasFlag(DialogFilter.Flags.groups)
                || flags.HasFlag(DialogFilter.Flags.broadcasts)
                || flags.HasFlag(DialogFilter.Flags.bots);

            if (wantsClasses)
                foreach (var id in _dialogOrder)
                    if (_chats.TryGetValue(id, out var e) && Matches(e, flags))
                        TryAdd(id);

            return list;

            void TryAdd(long id)
            {
                if (id == 0 || excluded.Contains(id) || !added.Add(id)) return;
                if (!_chats.TryGetValue(id, out var e)) return;
                // the exclusion flags apply to explicitly listed chats as well
                if (e.IsArchived && flags.HasFlag(DialogFilter.Flags.exclude_archived)) return;
                if (!e.IsUnread && flags.HasFlag(DialogFilter.Flags.exclude_read)) return;
                if (e.IsMuted && flags.HasFlag(DialogFilter.Flags.exclude_muted)) return;
                list.Add(e);
            }
        }

        private static bool Matches(ChatEntry e, DialogFilter.Flags flags) => e.Kind switch
        {
            ChatKind.Private when e.IsBot => flags.HasFlag(DialogFilter.Flags.bots),
            ChatKind.Private => e.IsContact
                ? flags.HasFlag(DialogFilter.Flags.contacts)
                : flags.HasFlag(DialogFilter.Flags.non_contacts),
            ChatKind.Group or ChatKind.Forum => flags.HasFlag(DialogFilter.Flags.groups),
            ChatKind.Channel => flags.HasFlag(DialogFilter.Flags.broadcasts),
            _ => false
        };

        /// A response also describes chats that are not dialogs of this folder — group
        /// members, authors of last messages. Identity is taken from all of them, but
        /// folder membership and per-dialog flags come only from the Dialogs array,
        /// otherwise the second pass would mark everything as archived.
        private void Absorb(Messages_Dialogs d, bool archived)
        {
            var lastDate = new Dictionary<long, DateTime>();
            foreach (var m in d.Messages)
                if (m.Peer != null && (!lastDate.TryGetValue(m.Peer.ID, out var prev) || m.Date > prev))
                    lastDate[m.Peer.ID] = m.Date;

            foreach (var kv in d.users)
            {
                var u = kv.Value;
                if (u.id == 0) continue;
                _peers[u.id] = new InputPeerUser(u.id, u.access_hash);
                Remember(new ChatEntry(u.id, DisplayNameOrUser(u), ChatKind.Private, u.MainUsername,
                    default, u.flags.HasFlag(User.Flags.contact), u.IsBot), lastDate);
            }

            foreach (var kv in d.chats)
            {
                var c = kv.Value;
                if (c is Channel ch)
                {
                    _peers[ch.id] = new InputPeerChannel(ch.id, ch.access_hash);
                    var forum = ch.flags.HasFlag(Channel.Flags.forum);
                    _isForum[ch.id] = forum;
                    var kind = ch.IsChannel ? ChatKind.Channel : (forum ? ChatKind.Forum : ChatKind.Group);
                    Remember(new ChatEntry(ch.id, ch.Title, kind, ch.MainUsername), lastDate);
                }
                else if (c is Chat basic)
                {
                    _peers[basic.id] = new InputPeerChat(basic.id);
                    Remember(new ChatEntry(basic.id, basic.Title, ChatKind.Group), lastDate);
                }
            }

            var now = DateTime.UtcNow;
            foreach (var dialog in d.Dialogs)
            {
                var id = dialog.Peer?.ID ?? 0;
                if (id == 0) continue;

                // A handful of chats come back from both folder queries. The dialog's own
                // folder_id is the authoritative answer, and the main folder is queried
                // first, so the first answer for a chat is the one that counts.
                if (!_dialogSeen.Add(id)) continue;
                _dialogOrder.Add(id);

                if (dialog is Dialog dlg && _chats.TryGetValue(id, out var entry))
                    _chats[id] = entry with
                    {
                        IsArchived = dlg.folder_id == TelegramIds.ArchiveFolder,
                        IsUnread = dlg.unread_count > 0 || dlg.flags.HasFlag(Dialog.Flags.unread_mark),
                        IsMuted = dlg.notify_settings?.mute_until > now
                    };
            }
        }

        /// Keeps identity fresh without losing what an earlier pass already established.
        private void Remember(ChatEntry fresh, Dictionary<long, DateTime> lastDate)
        {
            var date = lastDate.GetValueOrDefault(fresh.Id);
            if (_chats.TryGetValue(fresh.Id, out var known))
                _chats[fresh.Id] = known with
                {
                    Title = fresh.Title,
                    Kind = fresh.Kind,
                    Slug = fresh.Slug,
                    IsContact = fresh.IsContact,
                    IsBot = fresh.IsBot,
                    Date = date > known.Date ? date : known.Date
                };
            else
                _chats[fresh.Id] = fresh with { Date = date };
        }

        private static string DisplayNameOrUser(User u)
        {
            if (u.flags.HasFlag(User.Flags.self)) return Naming.SavedMessages;
            var n = DisplayName(u);
            return n.Length > 0 ? n : (u.MainUsername ?? $"User {u.id}");
        }

        private static long PeerId(InputPeer p) => p switch
        {
            InputPeerUser u => u.user_id,
            InputPeerChat c => c.chat_id,
            InputPeerChannel ch => ch.channel_id,
            _ => 0
        };

        public bool IsForum(long chatId) => _isForum.TryGetValue(chatId, out var f) && f;

        public ChatKind KindOf(long chatId) =>
            _chats.TryGetValue(chatId, out var e) ? e.Kind : ChatKind.Private;

        // ---- topics ----

        public async Task<List<(long Id, string Title)>> GetTopicsAsync(long chatId)
        {
            var list = new List<(long, string)>();
            if (!IsLoggedIn || !_peers.TryGetValue(chatId, out var peer)) return list;
            try
            {
                var topics = await _client!.Channels_GetAllForumTopics(peer, null);
                foreach (var t in topics.topics)
                    if (t is ForumTopic ft) list.Add((ft.id, ft.title));
            }
            catch (Exception ex) { Log?.Invoke($"TotalGram: topics failed: {ex.Message}"); }
            return list;
        }

        // ---- messages ----

        private const int PageSize = 100;

        private async Task<InputPeer?> PeerOfAsync(long chatId)
        {
            if (_peers.TryGetValue(chatId, out var peer)) return peer;
            await GetChatsAsync(TelegramIds.AllChatsFolder, false);
            return _peers.TryGetValue(chatId, out peer) ? peer : null;
        }

        /// Walks a paged Telegram listing until it runs out or the cap is reached.
        /// `fetch` receives the id to continue from (0 for the newest message).
        /// `startOffset` resumes an earlier walk; `stopBelowId` ends it as soon as the
        /// listing reaches messages that are already known. ReachedEnd tells the two
        /// apart: the history really ran out, rather than the cap or the stop id.
        private async Task<(List<TL.Message> Items, bool ReachedEnd)> PageAsync(
            long chatId, Func<int, Task<Messages_MessagesBase>> fetch,
            int startOffset = 0, int stopBelowId = 0)
        {
            var list = new List<TL.Message>();
            var cap = ApiCredentials.MaxItems;
            var offsetId = startOffset;

            while (true)
            {
                var page = await fetch(offsetId);
                var batch = page.Messages;
                if (batch.Length == 0) return (list, true);

                var reachedKnown = false;
                foreach (var m in batch)
                {
                    if (stopBelowId != 0 && m.ID <= stopBelowId) { reachedKnown = true; break; }
                    if (m is TL.Message msg)
                    {
                        _messages[(chatId, msg.id)] = msg;
                        list.Add(msg);
                    }
                }
                if (reachedKnown) return (list, false);

                offsetId = batch[^1].ID;
                if (offsetId == 0) return (list, true);
                if (cap > 0 && list.Count >= cap) return (list, false);
                // a short page means the end of the history, not an empty one
                if (batch.Length < PageSize) return (list, true);
            }
        }

        public async Task<List<TL.Message>> GetMessagesAsync(long chatId, long topicId)
        {
            if (!IsLoggedIn) return new();
            var peer = await PeerOfAsync(chatId);
            if (peer == null) return new();
            try
            {
                return (await PageAsync(chatId, offsetId => topicId != 0
                    ? _client!.Messages_GetReplies(peer, (int)topicId, offset_id: offsetId, limit: PageSize)
                    : _client!.Messages_GetHistory(peer, offset_id: offsetId, limit: PageSize))).Items;
            }
            catch (Exception ex) { Log?.Invoke($"TotalGram: history failed: {ex.Message}"); return new(); }
        }

        /// Asks Telegram for one kind of content directly, the way the "Shared media"
        /// tabs do, instead of reading the whole history and filtering it here.
        public async Task<List<TL.Message>> GetFilesAsync(long chatId, long topicId, ContentCategory? category)
        {
            if (!IsLoggedIn) return new();
            var peer = await PeerOfAsync(chatId);
            if (peer == null) return new();

            var categories = category != null
                ? new[] { category.Value }
                : new[] { ContentCategory.Media, ContentCategory.Files, ContentCategory.Music,
                          ContentCategory.Voice, ContentCategory.GIF, ContentCategory.Links };

            var byId = new Dictionary<int, TL.Message>();
            foreach (var c in categories)
            {
                try
                {
                    foreach (var m in await CategoryAsync(peer, chatId, topicId, c)) byId[m.id] = m;
                }
                catch (Exception ex) { Log?.Invoke($"TotalGram: search {c} failed: {ex.Message}"); }
            }

            return byId.Values.OrderByDescending(m => m.date).ToList();
        }

        /// One kind of content, served from the folder's cache on disk. Telegram is only
        /// asked for what the cache cannot answer: the messages posted since the last
        /// visit, and - until the folder is complete - one more stretch of older history.
        /// A chat too big to read in one go therefore fills in over several visits
        /// instead of staying cut off at the cap forever.
        private async Task<List<TL.Message>> CategoryAsync(
            InputPeer peer, long chatId, long topicId, ContentCategory category)
        {
            var cached = FileCache.Load(Key, chatId, topicId, category);
            foreach (var m in cached.Messages) _messages[(chatId, m.id)] = m;

            var known = new HashSet<int>();
            var newestId = 0;
            foreach (var m in cached.Messages)
            {
                known.Add(m.id);
                if (m.id > newestId) newestId = m.id;
            }

            Task<Messages_MessagesBase> Search(int offsetId) => _client!.Messages_Search(
                peer, null, FilterFor(category),
                offset_id: offsetId, limit: PageSize,
                top_msg_id: topicId != 0 ? (int)topicId : null);

            var fresh = new List<TL.Message>();
            var complete = cached.Complete;
            var oldestId = cached.OldestId;

            // what arrived since the last visit: usually one request that finds nothing
            if (newestId != 0)
                foreach (var m in (await PageAsync(chatId, Search, stopBelowId: newestId)).Items)
                    if (known.Add(m.id)) fresh.Add(m);

            // the first visit, or more of the history still to walk back through
            if (newestId == 0 || !complete)
            {
                var older = await PageAsync(chatId, Search, startOffset: oldestId);
                foreach (var m in older.Items)
                    if (known.Add(m.id)) fresh.Add(m);
                complete = older.ReachedEnd;
            }

            var all = cached.Messages;
            all.AddRange(fresh);
            all.Sort((a, b) => b.id.CompareTo(a.id));
            if (all.Count > 0) oldestId = all[^1].id;

            if (fresh.Count > 0 || complete != cached.Complete)
                FileCache.Save(Key, chatId, topicId, category, all, oldestId, complete);

            return all;
        }

        /// Newest message per kind, one cheap request each — enough to date the folders
        /// without pulling their contents.
        public async Task<Dictionary<ContentCategory, DateTime>> GetCategoryDatesAsync(long chatId, long topicId)
        {
            var dates = new Dictionary<ContentCategory, DateTime>();
            if (!IsLoggedIn) return dates;
            var peer = await PeerOfAsync(chatId);
            if (peer == null) return dates;

            foreach (var c in new[] { ContentCategory.Media, ContentCategory.Files, ContentCategory.Links,
                                      ContentCategory.Music, ContentCategory.Voice, ContentCategory.GIF })
            {
                try
                {
                    var page = await _client!.Messages_Search(
                        peer, null, FilterFor(c), limit: 1,
                        top_msg_id: topicId != 0 ? (int)topicId : null);

                    foreach (var m in page.Messages)
                        if (m is TL.Message msg) { dates[c] = msg.date; break; }
                }
                catch (Exception ex) { Log?.Invoke($"TotalGram: date probe {c} failed: {ex.Message}"); }
            }
            return dates;
        }

        private static MessagesFilter FilterFor(ContentCategory category) => category switch
        {
            ContentCategory.Media => new InputMessagesFilterPhotoVideo(),
            ContentCategory.Music => new InputMessagesFilterMusic(),
            ContentCategory.Voice => new InputMessagesFilterVoice(),
            ContentCategory.GIF => new InputMessagesFilterGif(),
            ContentCategory.Links => new InputMessagesFilterUrl(),
            _ => new InputMessagesFilterDocument()
        };

        public TL.Message? GetCachedMessage(long chatId, int msgId) =>
            _messages.TryGetValue((chatId, msgId), out var m) ? m : null;

        // ---- transfers ----

        /// Downloads a small preview into TEMP and returns its path, or null when the
        /// message has nothing to show. Total Commander decodes and deletes the file.
        public async Task<string?> DownloadPreviewAsync(TL.Message msg, int width, int height)
        {
            if (!IsLoggedIn) return null;
            try
            {
                var dir = Path.Combine(Paths.CacheDir, "preview");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"{Key}_{msg.peer_id?.ID}_{msg.id}.jpg");

                switch (msg.media)
                {
                    case MessageMediaPhoto { photo: Photo photo }:
                    {
                        var size = PickSize(photo.sizes, Math.Max(width, height));
                        if (size == null) return null;
                        await using var fs = File.Create(path);
                        await _client!.DownloadFileAsync(photo, fs, size);
                        break;
                    }
                    // videos and other documents carry their own thumbnails
                    case MessageMediaDocument { document: Document doc }:
                    {
                        var size = PickSize(doc.thumbs, Math.Max(width, height));
                        if (size == null) return null;
                        await using var fs = File.Create(path);
                        await _client!.DownloadFileAsync(doc, fs, size);
                        break;
                    }
                    default:
                        return null;
                }

                return new FileInfo(path).Length > 0 ? path : null;
            }
            catch (Exception ex) { Log?.Invoke($"TotalGram: preview failed: {ex.Message}"); return null; }
        }

        /// Smallest stored size that still fills the requested box, else the largest one.
        /// Stripped and path sizes are inline placeholders and cannot be downloaded.
        private static PhotoSizeBase? PickSize(PhotoSizeBase[]? sizes, int wanted)
        {
            if (sizes == null) return null;

            var usable = sizes
                .Where(s => s is PhotoSize or PhotoSizeProgressive)
                .OrderBy(s => Math.Max(s.Width, s.Height))
                .ToList();
            if (usable.Count == 0) return null;

            return usable.FirstOrDefault(s => Math.Max(s.Width, s.Height) >= wanted) ?? usable[^1];
        }

        public async Task<bool> DownloadAsync(TL.Message msg, string localPath, Action<long, long>? progress = null)
        {
            if (!IsLoggedIn) return false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                WTelegram.Client.ProgressCallback? callback =
                    progress == null ? null : (done, total) => progress(done, total);

                await using var fs = File.Create(localPath);
                switch (msg.media)
                {
                    case MessageMediaDocument { document: Document doc }:
                        await _client!.DownloadFileAsync(doc, fs, (PhotoSizeBase?)null, callback);
                        progress?.Invoke(1, 1);
                        return true;
                    case MessageMediaPhoto { photo: Photo photo }:
                        await _client!.DownloadFileAsync(photo, fs, (PhotoSizeBase?)null, callback);
                        progress?.Invoke(1, 1);
                        return true;
                    default:
                        return false;
                }
            }
            catch (Exception ex) { Log?.Invoke($"TotalGram: download failed: {ex.Message}"); return false; }
        }

        /// The target folder decides what kind of message is sent; without one the file
        /// type decides, so an image dropped into a chat arrives as a photo, not a file.
        public async Task<bool> UploadAsync(long chatId, long topicId, string localPath, ContentCategory? category,
            Action<long, long>? progress = null)
        {
            if (!IsLoggedIn) return false;
            if (!_peers.TryGetValue(chatId, out var peer)) return false;
            try
            {
                if (category == ContentCategory.Links)
                {
                    var url = MediaClassifier.ReadUrlShortcut(localPath);
                    if (url == null) { Log?.Invoke("TotalGram: .url file has no URL= line"); return false; }
                    await _client!.SendMessageAsync(peer, url, null, (int)topicId);
                    return true;
                }

                var file = await _client!.UploadFileAsync(localPath,
                    progress == null ? null : (done, total) => progress(done, total));
                var media = BuildMedia(file, localPath, category);
                await _client.SendMessageAsync(peer, "", media, (int)topicId);
                progress?.Invoke(1, 1);
                return true;
            }
            catch (Exception ex) { Log?.Invoke($"TotalGram: upload failed: {ex.Message}"); return false; }
        }

        private static InputMedia BuildMedia(InputFileBase file, string localPath, ContentCategory? category)
        {
            var name = Path.GetFileName(localPath);
            var mime = MimeOf(localPath);
            var kind = category ?? GuessCategory(mime);

            switch (kind)
            {
                case ContentCategory.Media when mime.StartsWith("image/"):
                    return new InputMediaUploadedPhoto { file = file };

                case ContentCategory.Media when mime.StartsWith("video/"):
                    return Document(file, mime, name,
                        new DocumentAttributeVideo { flags = DocumentAttributeVideo.Flags.supports_streaming });

                // an image forced into Media stays a photo; anything else falls back to a file
                case ContentCategory.Media:
                    return Document(file, mime, name);

                case ContentCategory.Voice:
                    return new InputMediaUploadedDocument
                    {
                        file = file,
                        mime_type = mime.StartsWith("audio/") ? mime : "audio/ogg",
                        attributes = new DocumentAttribute[]
                        {
                            new DocumentAttributeAudio { flags = DocumentAttributeAudio.Flags.voice }
                        }
                    };

                case ContentCategory.Music:
                    return Document(file, mime.StartsWith("audio/") ? mime : "audio/mpeg", name,
                        new DocumentAttributeAudio { title = Path.GetFileNameWithoutExtension(name) });

                case ContentCategory.GIF:
                    return Document(file, mime, name, new DocumentAttributeAnimated());

                default:
                    return Document(file, mime, name);
            }
        }

        private static InputMedia Document(InputFileBase file, string mime, string name, params DocumentAttribute[] extra)
        {
            var attributes = new List<DocumentAttribute>(extra) { new DocumentAttributeFilename { file_name = name } };
            return new InputMediaUploadedDocument
            {
                file = file,
                mime_type = mime,
                attributes = attributes.ToArray()
            };
        }

        private static ContentCategory GuessCategory(string mime) =>
            mime == "image/gif" ? ContentCategory.GIF
            : mime.StartsWith("image/") || mime.StartsWith("video/") ? ContentCategory.Media
            : mime.StartsWith("audio/") ? ContentCategory.Music
            : ContentCategory.Files;

        private static string MimeOf(string path) =>
            MediaClassifier.MimeOfExtension(Path.GetExtension(path).ToLowerInvariant());

        public void Dispose() { _client?.Dispose(); _client = null; Me = null; }
    }

    public enum ChatKind { Private, Group, Forum, Channel }

    public sealed record ChatEntry(
        long Id,
        string Title,
        ChatKind Kind,
        string? Slug = null,
        DateTime Date = default,
        bool IsContact = false,
        bool IsBot = false,
        bool IsArchived = false,
        bool IsUnread = false,
        bool IsMuted = false);
}
