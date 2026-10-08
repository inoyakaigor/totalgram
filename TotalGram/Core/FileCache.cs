using TL;
using TotalGram.Models;

namespace TotalGram.Core
{
    /// A chat's file listing kept on disk between sessions, so Telegram is asked for a
    /// whole folder once and afterwards only for what changed. Messages are stored in
    /// Telegram's own binary format: everything downstream needs the real TL.Message.
    public static class FileCache
    {
        private const int Magic = 0x4D475447;   // "GTGM"
        private const int Version = 1;

        /// What one folder's cache holds: the messages, the oldest id reached while
        /// walking back through the history, and whether that walk hit the beginning.
        public sealed record Entry(List<TL.Message> Messages, int OldestId, bool Complete);

        public static readonly Entry Empty = new(new List<TL.Message>(), 0, false);

        private static string PathFor(string accountKey, long chatId, long topicId, ContentCategory category) =>
            Path.Combine(Paths.ListingCacheDir, Naming.Sanitize(accountKey),
                $"{chatId}_{topicId}_{(int)category}.bin");

        public static Entry Load(string accountKey, long chatId, long topicId, ContentCategory category)
        {
            var path = PathFor(accountKey, chatId, topicId, category);
            try
            {
                if (!File.Exists(path)) return Empty;

                using var reader = new BinaryReader(File.OpenRead(path));
                if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version) return Empty;

                var oldest = reader.ReadInt32();
                var complete = reader.ReadBoolean();
                var count = reader.ReadInt32();

                var messages = new List<TL.Message>(count);
                for (var i = 0; i < count; i++)
                    if (reader.ReadTLObject() is TL.Message msg) messages.Add(msg);

                return new Entry(messages, oldest, complete);
            }
            // a half-written or outdated cache costs one refetch, never a failed listing
            catch { return Empty; }
        }

        public static void Save(string accountKey, long chatId, long topicId, ContentCategory category,
            IReadOnlyList<TL.Message> messages, int oldestId, bool complete)
        {
            var path = PathFor(accountKey, chatId, topicId, category);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                // written beside the real file so a crash cannot leave a torn listing
                var temp = path + ".tmp";
                using (var writer = new BinaryWriter(File.Create(temp)))
                {
                    writer.Write(Magic);
                    writer.Write(Version);
                    writer.Write(oldestId);
                    writer.Write(complete);
                    writer.Write(messages.Count);
                    foreach (var msg in messages) writer.WriteTLObject(msg);
                }
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) { AccountManager.Log?.Invoke($"TotalGram: cache write failed: {ex.Message}"); }
        }
    }
}
