namespace TotalGram.Core
{
    /// Telegram api_id / api_hash. Obtained once per application from
    /// https://my.telegram.org -> API development tools.
    /// Read from %APPDATA%\TotalGram\config.ini so they can be changed without a rebuild:
    ///   api_id=1234567
    ///   api_hash=0123456789abcdef0123456789abcdef
    public static class ApiCredentials
    {
        private const string DefaultApiId = "";
        private const string DefaultApiHash = "";

        private static readonly Dictionary<string, string> _ini = LoadIni();

        public static string ApiId => Get("api_id", DefaultApiId);
        public static string ApiHash => Get("api_hash", DefaultApiHash);
        public static bool Configured => ApiId.Length > 0 && ApiHash.Length > 0;

        /// log_paths=1 makes every directory request visible in Total Commander's FTP log.
        public static bool LogPaths => Get("log_paths", "0") is "1" or "true";

        /// How many items one folder may hold. Telegram is paged, so an unbounded
        /// listing of a busy channel would keep Total Commander waiting for minutes.
        /// max_items=0 removes the cap.
        public static int MaxItems =>
            int.TryParse(Get("max_items", "2000"), out var v) && v >= 0 ? v : 2000;

        public static string ConfigPath => Path.Combine(Paths.DataDir, "config.ini");

        private static string Get(string key, string fallback) =>
            _ini.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;

        private static Dictionary<string, string> LoadIni()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var path = Path.Combine(Paths.DataDir, "config.ini");
                if (!File.Exists(path)) return map;
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] is '#' or ';' or '[') continue;
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    map[line[..eq].Trim()] = line[(eq + 1)..].Trim();
                }
            }
            catch { }
            return map;
        }
    }

    public static class Paths
    {
        public static string DataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Igor Zviagintsev", "TotalGram");

        public static string AccountsDir => Path.Combine(DataDir, "accounts");

        public static string CacheDir => Path.Combine(Path.GetTempPath(), "TotalGram");

        /// Folder listings, unlike thumbnails, are worth keeping across reboots.
        public static string ListingCacheDir => Path.Combine(DataDir, "cache");
    }
}
