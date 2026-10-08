using System.Runtime.InteropServices;

namespace TotalGram.Wfx
{
    public static class WfxEntryPoint
    {
        internal static int _pluginNr;
        internal static IntPtr _progressProcPtr;
        internal static IntPtr _logProcPtr;
        internal static IntPtr _requestProcPtr;

        public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        public const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        private const int MSGTYPE_CONNECT = 1;
        private const int MSGTYPE_DISCONNECT = 2;
        private const int MSGTYPE_DETAILS = 3;
        private const int MSGTYPE_IMPORTANTERROR = 6;
        private const int MSGTYPE_OPERATIONCOMPLETE = 7;
        private const int RT_MsgOK = 8;

        private static bool _connectAnnounced;

        public static int PluginNr => _pluginNr;

        internal static int Progress(int pluginNr, IntPtr sourceName, IntPtr targetName, int percentDone)
        {
            if (_progressProcPtr == IntPtr.Zero) return 0;
            unsafe
            {
                var fn = (delegate* unmanaged[Stdcall]<int, IntPtr, IntPtr, int, int>)_progressProcPtr;
                return fn(pluginNr, sourceName, targetName, percentDone);
            }
        }

        /// Drives the copy dialog for one transfer. Total Commander wants this called at
        /// least at 0% and 100%, and it reports the Cancel button through the return value.
        public sealed class Transfer : IDisposable
        {
            private readonly IntPtr _source;
            private readonly IntPtr _target;
            private readonly long _expected;
            private long _done;
            private long _total;

            public Transfer(string source, string target, long expectedSize = 0)
            {
                _source = Marshal.StringToHGlobalAnsi(source);
                _target = Marshal.StringToHGlobalAnsi(target);
                _expected = expectedSize;
            }

            public bool Aborted { get; private set; }

            /// Called from the Telegram client's own thread, so it only records the position.
            public void Note(long done, long total)
            {
                Volatile.Write(ref _done, done);
                Volatile.Write(ref _total, total);
            }

            /// Total Commander runs the transfer on a thread of its own and ignores progress
            /// arriving from any other one, so the dialog is driven from here - the thread
            /// that entered FsGetFile/FsPutFile - rather than from the client's callback.
            public bool Pump(Task<bool> work)
            {
                Report(0, 1);
                while (!work.Wait(200))
                    Report(Volatile.Read(ref _done), Volatile.Read(ref _total));
                Report(1, 1);
                return work.Result;
            }

            public void Report(long done, long total)
            {
                if (total <= 0) total = _expected;   // the client reports no size for photos
                var percent = total > 0 ? (int)(done * 100 / total) : 0;
                if (Progress(_pluginNr, _source, _target, Math.Clamp(percent, 0, 100)) != 0)
                    Aborted = true;
            }

            public void Dispose()
            {
                Marshal.FreeHGlobal(_source);
                Marshal.FreeHGlobal(_target);
            }
        }

        internal static void Log(int pluginNr, int msgType, IntPtr logString)
        {
            if (_logProcPtr == IntPtr.Zero) return;
            unsafe
            {
                var fn = (delegate* unmanaged[Stdcall]<int, int, IntPtr, void>)_logProcPtr;
                fn(pluginNr, msgType, logString);
            }
        }

        /// Writes one line into Total Commander's FTP log.
        public static void LogLine(string message) => Send(MSGTYPE_DETAILS, message);

        public static void LogError(string message) => Send(MSGTYPE_IMPORTANTERROR, message);

        public static void LogOperation(string message) => Send(MSGTYPE_OPERATIONCOMPLETE, message);

        /// Makes the FTP toolbar/log panel appear. The string format is mandated by the SDK.
        /// Also tells Total Commander that the Disconnect button applies to us.
        public static void AnnounceConnect()
        {
            if (_connectAnnounced) return;
            _connectAnnounced = true;
            Send(MSGTYPE_CONNECT, "CONNECT \\" + WfxFunctions.GetDefRootName());
        }

        public static void AnnounceDisconnect()
        {
            if (!_connectAnnounced) return;
            _connectAnnounced = false;
            Send(MSGTYPE_DISCONNECT, "Disconnected");
        }

        private static void Send(int msgType, string message)
        {
            if (_logProcPtr == IntPtr.Zero) return;
            var ptr = Marshal.StringToHGlobalAnsi(message);
            try { Log(_pluginNr, msgType, ptr); }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        internal static bool Request(int pluginNr, int requestType, IntPtr customTitle, IntPtr customText, IntPtr returnedText, int maxlen)
        {
            if (_requestProcPtr == IntPtr.Zero) return false;
            unsafe
            {
                var fn = (delegate* unmanaged[Stdcall]<int, int, IntPtr, IntPtr, IntPtr, int, int>)_requestProcPtr;
                return fn(pluginNr, requestType, customTitle, customText, returnedText, maxlen) != 0;
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        /// Total Commander's own RequestProc is ANSI-only, which mangles names containing
        /// characters outside the local codepage, so messages go through Win32 directly.
        public static void MessageBox(string text)
        {
            const uint MB_OK = 0x0;
            const uint MB_ICONINFORMATION = 0x40;
            const uint MB_TOPMOST = 0x40000;
            MessageBoxW(IntPtr.Zero, text, "TotalGram", MB_OK | MB_ICONINFORMATION | MB_TOPMOST);
        }

        internal static FILETIME DateTimeToFileTime(DateTime dt)
        {
            long v;
            try { v = dt.ToUniversalTime().ToFileTimeUtc(); }
            catch { v = DateTime.UtcNow.ToFileTimeUtc(); }
            return new FILETIME { dwLowDateTime = (uint)(v & 0xFFFFFFFF), dwHighDateTime = (uint)((v >> 32) & 0xFFFFFFFF) };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public unsafe struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        public fixed char cFileName[260];
        public fixed char cAlternateFileName[14];
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct WIN32_FIND_DATA
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        public fixed byte cFileName[260];
        public fixed byte cAlternateFileName[14];
    }
}
