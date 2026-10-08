using System.Text.RegularExpressions;
using TL;
using TotalGram.Models;

namespace TotalGram.Core
{
    public sealed record MediaInfo(ContentCategory Category, string FileName, long Size);

    public static class MediaClassifier
    {
        private static readonly Regex UrlRegex = new(@"https?://[^\s<>""]+", RegexOptions.Compiled);

        /// Describes the downloadable payload of a message, or null when there is none.
        public static MediaInfo? Describe(TL.Message msg)
        {
            switch (msg.media)
            {
                case MessageMediaPhoto { photo: Photo p }:
                    return new MediaInfo(ContentCategory.Media, $"photo_{msg.id}.jpg", LargestSize(p));

                case MessageMediaDocument { document: Document d }:
                    var name = FileNameOf(d, msg.id);
                    return new MediaInfo(CategoryOf(d), name, d.size);
            }
            return null;
        }

        public static string? FirstUrl(TL.Message msg)
        {
            if (string.IsNullOrEmpty(msg.message)) return null;
            var m = UrlRegex.Match(msg.message);
            return m.Success ? m.Value : null;
        }

        public static string UrlFileName(TL.Message msg) => $"link_{msg.id}.url";

        public static string UrlFileBody(string url) => $"[InternetShortcut]\r\nURL={url}\r\n";

        /// The post's own text, so a text-only post is not an empty folder.
        public const string TextFileName = "message.md";

        public static string? TextBody(TL.Message msg) =>
            string.IsNullOrWhiteSpace(msg.message) ? null : msg.message;

        public static long TextSize(TL.Message msg) =>
            TextBody(msg) is { } t ? System.Text.Encoding.UTF8.GetByteCount(t) : 0;

        private static ContentCategory CategoryOf(Document d)
        {
            var animated = false;
            var voice = false;
            var audio = false;
            var video = false;
            foreach (var a in d.attributes)
            {
                switch (a)
                {
                    case DocumentAttributeAnimated: animated = true; break;
                    case DocumentAttributeAudio au:
                        if (au.flags.HasFlag(DocumentAttributeAudio.Flags.voice)) voice = true; else audio = true;
                        break;
                    case DocumentAttributeVideo: video = true; break;
                }
            }
            var mime = d.mime_type ?? "";
            if (voice) return ContentCategory.Voice;
            if (audio) return ContentCategory.Music;
            if (animated || mime.Contains("gif", StringComparison.OrdinalIgnoreCase)) return ContentCategory.GIF;
            if (video || mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return ContentCategory.Media;
            if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return ContentCategory.Media;
            return ContentCategory.Files;
        }

        private static string FileNameOf(Document d, int msgId)
        {
            foreach (var a in d.attributes)
                if (a is DocumentAttributeFilename fn && !string.IsNullOrEmpty(fn.file_name))
                    return Naming.Sanitize(fn.file_name);

            foreach (var a in d.attributes)
                if (a is DocumentAttributeAudio au)
                    return Naming.Sanitize(
                        au.flags.HasFlag(DocumentAttributeAudio.Flags.voice)
                            ? $"voice_{msgId}.ogg"
                            : $"{au.performer} - {au.title}".Trim(' ', '-') is { Length: > 0 } t ? $"{t}.mp3" : $"audio_{msgId}.mp3");

            return $"file_{msgId}{ExtensionOf(d.mime_type)}";
        }

        private static string ExtensionOf(string? mime) => mime switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            "audio/mpeg" => ".mp3",
            "audio/ogg" => ".ogg",
            "application/pdf" => ".pdf",
            "application/zip" => ".zip",
            _ => ".bin"
        };

        /// A special folder only accepts what it stands for. Returns why a file is
        /// rejected, or null when it may be sent there.
        public static string? RejectUpload(ContentCategory? category, string localPath)
        {
            if (category == null) return null;   // chat root takes anything

            var ext = Path.GetExtension(localPath).ToLowerInvariant();
            var mime = MimeOfExtension(ext);

            return category switch
            {
                ContentCategory.Media when !mime.StartsWith("image/") && !mime.StartsWith("video/")
                    => "Media accepts images and videos only",
                ContentCategory.Music when !mime.StartsWith("audio/")
                    => "Music accepts audio files only",
                ContentCategory.Voice when !mime.StartsWith("audio/")
                    => "Voice Messages accepts audio files only",
                ContentCategory.GIF when ext is not (".gif" or ".mp4" or ".webm")
                    => "GIF accepts .gif, .mp4 and .webm only",
                ContentCategory.Links when ext != ".url"
                    => "Links accepts .url files only",
                _ => null
            };
        }

        /// The URL inside a Windows .url shortcut.
        public static string? ReadUrlShortcut(string localPath)
        {
            try
            {
                foreach (var line in File.ReadAllLines(localPath))
                    if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                        return line[4..].Trim();
            }
            catch { }
            return null;
        }

        public static string MimeOfExtension(string ext) => ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mkv" => "video/x-matroska",
            ".mov" => "video/quicktime",
            ".mp3" => "audio/mpeg",
            ".ogg" or ".oga" or ".opus" => "audio/ogg",
            ".m4a" => "audio/mp4",
            ".flac" => "audio/flac",
            ".wav" => "audio/wav",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            ".txt" or ".md" => "text/plain",
            _ => "application/octet-stream"
        };

        private static long LargestSize(Photo p)
        {
            long max = 0;
            foreach (var s in p.sizes)
            {
                // Telegram sends today's photos as progressive JPEGs, where the full
                // file size is the last of the partial sizes, not a PhotoSize.size
                long size = s switch
                {
                    PhotoSize ps => ps.size,
                    PhotoSizeProgressive { sizes.Length: > 0 } pr => pr.sizes[^1],
                    PhotoCachedSize pc => pc.bytes.Length,
                    _ => 0
                };
                if (size > max) max = size;
            }
            return max;
        }
    }
}
