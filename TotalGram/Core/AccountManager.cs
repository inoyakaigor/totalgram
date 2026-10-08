namespace TotalGram.Core
{
    /// Discovers accounts from their session files and keeps the live clients.
    public static class AccountManager
    {
        private static readonly Dictionary<string, TelegramAccount> _accounts = new(StringComparer.OrdinalIgnoreCase);
        private static bool _scanned;

        public static Action<string>? Log;

        /// Why the last Resolve() failed, for reporting to the user.
        public static string? LastError { get; private set; }

        public static IReadOnlyList<TelegramAccount> All
        {
            get { Scan(); return _accounts.Values.OrderBy(a => a.Title, StringComparer.CurrentCulture).ToList(); }
        }

        /// Display names for the account list, qualified only where two accounts collide.
        public static (TelegramAccount Account, string Name)[] Listing()
        {
            var accounts = All;
            var names = Naming.MakeUnique(
                accounts.Select(a => new NameCandidate(a.Title, a.Slug, a.UserId)).ToList());
            return accounts.Select((a, i) => (a, names[i])).ToArray();
        }

        private static void Scan()
        {
            if (_scanned) return;
            _scanned = true;
            try
            {
                Directory.CreateDirectory(Paths.AccountsDir);
                foreach (var file in Directory.GetFiles(Paths.AccountsDir, "*.session"))
                {
                    var key = Path.GetFileNameWithoutExtension(file);
                    if (key.Length == 0 || _accounts.ContainsKey(key)) continue;
                    var acc = new TelegramAccount(key, file);
                    acc.Log += m => Log?.Invoke(m);
                    _accounts[key] = acc;
                }
            }
            catch (Exception ex) { Log?.Invoke($"TotalGram: account scan failed: {ex.Message}"); }
        }

        /// Maps a folder name from the panel back to a connected account.
        public static TelegramAccount? Resolve(string displayName)
        {
            LastError = null;
            var match = Listing().FirstOrDefault(x =>
                string.Equals(x.Name, displayName, StringComparison.OrdinalIgnoreCase));

            if (match.Account == null)
            {
                LastError = "no session file for this account";
                return null;
            }
            if (!match.Account.IsLoggedIn && !match.Account.ConnectAsync().GetAwaiter().GetResult())
            {
                LastError = match.Account.LastError;
                return null;
            }
            return match.Account;
        }

        /// Runs a full login into a fresh session file and registers the result.
        /// Returns the new account's display name, or null when cancelled or failed.
        public static string? AddAccount(Func<string, string?> ask)
        {
            Scan();
            var temp = Path.Combine(Paths.AccountsDir, $"new-{DateTime.UtcNow:yyyyMMddHHmmss}.session");
            Directory.CreateDirectory(Paths.AccountsDir);

            var phone = ask("phone_number");
            if (string.IsNullOrWhiteSpace(phone)) return null;

            var acc = new TelegramAccount(Path.GetFileNameWithoutExtension(temp), temp);
            acc.Log += m => Log?.Invoke(m);

            if (!acc.LoginAsync(phone, ask).GetAwaiter().GetResult())
            {
                acc.Dispose();
                TryDelete(temp);
                return null;
            }

            // the session file is named after the account so a rescan can identify it
            var final = Path.Combine(Paths.AccountsDir, acc.Key + ".session");
            acc.Dispose();
            try
            {
                if (File.Exists(final)) File.Delete(final);
                File.Move(temp, final);
            }
            catch (Exception ex) { Log?.Invoke($"TotalGram: cannot rename session: {ex.Message}"); final = temp; }

            var live = new TelegramAccount(Path.GetFileNameWithoutExtension(final), final);
            live.Log += m => Log?.Invoke(m);
            _accounts[live.Key] = live;

            return Listing().FirstOrDefault(x => x.Account == live).Name;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        public static void DisconnectAll()
        {
            foreach (var a in _accounts.Values) a.Dispose();
            _accounts.Clear();
            _scanned = false;
        }
    }
}
