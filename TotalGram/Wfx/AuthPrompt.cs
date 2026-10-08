using System.Runtime.InteropServices;

namespace TotalGram.Wfx
{
    /// Login prompts rendered by Total Commander itself through RequestProc.
    public static class AuthPrompt
    {
        private const int RT_Other = 0;
        private const int RT_Password = 2;

        /// `what` is the value WTelegramClient asks for next.
        public static string? Ask(string what)
        {
            var (title, text, type) = what switch
            {
                "phone_number" => ("TotalGram - add account", "Phone number (e.g. +79991234567):", RT_Other),
                "verification_code" => ("TotalGram - verification", "Code sent by Telegram:", RT_Other),
                "password" => ("TotalGram - two-factor", "Cloud password (2FA):", RT_Password),
                "first_name" => ("TotalGram - sign up", "First name:", RT_Other),
                "last_name" => ("TotalGram - sign up", "Last name:", RT_Other),
                "email" => ("TotalGram - email", "E-mail address:", RT_Other),
                "email_verification_code" => ("TotalGram - email", "Code sent by e-mail:", RT_Other),
                _ => ("TotalGram", what + ":", RT_Other)
            };
            return Request(type, title, text);
        }

        private static unsafe string? Request(int requestType, string title, string text)
        {
            const int maxlen = 512;

            var titlePtr = Marshal.StringToHGlobalAnsi(title);
            var textPtr = Marshal.StringToHGlobalAnsi(text);
            var bufPtr = Marshal.AllocHGlobal(maxlen);
            try
            {
                new Span<byte>((void*)bufPtr, maxlen).Clear();
                var ok = WfxEntryPoint.Request(WfxEntryPoint.PluginNr, requestType, titlePtr, textPtr, bufPtr, maxlen);
                if (!ok) return null;
                return Marshal.PtrToStringAnsi(bufPtr);
            }
            finally
            {
                Marshal.FreeHGlobal(titlePtr);
                Marshal.FreeHGlobal(textPtr);
                Marshal.FreeHGlobal(bufPtr);
            }
        }
    }
}
