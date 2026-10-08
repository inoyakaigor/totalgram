using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TotalGram.Core;

namespace TotalGram.Wfx
{
    internal static unsafe class Exports
    {
        private const int FS_FILE_OK = 0;
        private const int FS_FILE_NOTFOUND = 2;
        private const int FS_FILE_READERROR = 3;
        private const int FS_FILE_WRITEERROR = 4;
        private const int FS_FILE_USERABORT = 5;
        private const int FS_EXEC_OK = 0;
        private const int FS_EXEC_ERROR = 1;
        private const int FS_EXEC_SYMLINK = -2;
        private const int BG_DOWNLOAD = 1;
        private const int BG_UPLOAD = 2;

        [UnmanagedCallersOnly(EntryPoint = "FsInit", CallConvs = new[] { typeof(CallConvStdcall) })]
        public static int FsInit(int pluginNr, IntPtr progressProc, IntPtr logProc, IntPtr requestProc)
        {
            try
            {
                WfxEntryPoint._pluginNr = pluginNr;
                WfxEntryPoint._progressProcPtr = progressProc;
                WfxEntryPoint._logProcPtr = logProc;
                WfxEntryPoint._requestProcPtr = requestProc;
                AccountManager.Log = WfxEntryPoint.LogLine;
                return 0;
            }
            catch { return -1; }
        }

        [UnmanagedCallersOnly(EntryPoint = "FsSetDefaultParams", CallConvs = new[] { typeof(CallConvStdcall) })]
        public static void FsSetDefaultParams(void* dps) { }

        [UnmanagedCallersOnly(EntryPoint = "FsGetDefRootName", CallConvs = new[] { typeof(CallConvStdcall) })]
        public static void FsGetDefRootName(byte* defRootName, int maxlen)
        {
            try
            {
                var bytes = System.Text.Encoding.ASCII.GetBytes(WfxFunctions.GetDefRootName());
                var n = Math.Min(bytes.Length, maxlen - 1);
                for (int i = 0; i < n; i++) defRootName[i] = bytes[i];
                defRootName[n] = 0;
            }
            catch { }
        }

        [UnmanagedCallersOnly(EntryPoint = "FsFindFirstW", CallConvs = new[] { typeof(CallConvStdcall) })]
        public static IntPtr FsFindFirstW(char* path, WIN32_FIND_DATAW* findData)
        {
            try { return WfxFunctions.FindFirst(new string(path), findData); }
            catch { return new IntPtr(-1); }
        }

        [UnmanagedCallersOnly(EntryPoint = "FsFindNextW", CallConvs = new[] { typeof(CallConvStdcall) })]
        public static int FsFindNextW(IntPtr handle, WIN32_FIND_DATAW* findData)
        {
            try { return WfxFunctions.FindNext(handle, findData); }
            catch { return 0; }
        }

        // ANSI fallbacks: Total Commander looks these names up before it will accept
        // the plugin at all, even though it calls the W versions afterwards.
        [UnmanagedCallersOnly(EntryPoint = "FsFindFirst", CallConvs = new[] { typeof(CallConvStdcall) })]
        public static IntPtr FsFindFirst(byte* path, WIN32_FIND_DATA* findData)
        {
            try
            {
                var pathStr = Marshal.PtrToStringAnsi((IntPtr)path) ?? "";
                WIN32_FIND_DATAW wide = default;
                var handle = WfxFunctions.FindFirst(pathStr, &wide);
                if (handle != new IntPtr(-1)) CopyToAnsi(&wide, findData);
                return handle;
            }
            catch { return new IntPtr(-1); }
        }

        [UnmanagedCallersOnly(EntryPoint = "FsFindNext", CallConvs = new[] { typeof(CallConvStdcall) })]
        public static int FsFindNext(IntPtr handle, WIN32_FIND_DATA* findData)
        {
            try
            {
                WIN32_FIND_DATAW wide = default;
                var result = WfxFunctions.FindNext(handle, &wide);
                if (result != 0) CopyToAnsi(&wide, findData);
                return result;
            }
            catch { return 0; }
        }

        private static void CopyToAnsi(WIN32_FIND_DATAW* src, WIN32_FIND_DATA* dst)
        {
            dst->dwFileAttributes = src->dwFileAttributes;
            dst->ftCreationTime = src->ftCreationTime;
            dst->ftLastAccessTime = src->ftLastAccessTime;
            dst->ftLastWriteTime = src->ftLastWriteTime;
            dst->nFileSizeHigh = src->nFileSizeHigh;
            dst->nFileSizeLow = src->nFileSizeLow;
            dst->dwReserved0 = src->dwReserved0;
            dst->dwReserved1 = src->dwReserved1;

            var bytes = System.Text.Encoding.Default.GetBytes(new string(src->cFileName));
            int n = Math.Min(bytes.Length, 259);
            for (int i = 0; i < n; i++) dst->cFileName[i] = bytes[i];
            dst->cFileName[n] = 0;
            dst->cAlternateFileName[0] = 0;
        }

        [UnmanagedCallersOnly(EntryPoint = "FsFindClose", CallConvs = new[] { typeof(CallConvStdcall) })]
        public static int FsFindClose(IntPtr handle)
        {
            try { return WfxFunctions.FindClose(handle); }
            catch { return 0; }
        }

        [UnmanagedCallersOnly(EntryPoint = "FsGetBackgroundFlags", CallConvs = new[] { typeof(CallConvStdcall) })]
        public static int FsGetBackgroundFlags() => BG_DOWNLOAD | BG_UPLOAD;

        [UnmanagedCallersOnly(EntryPoint = "FsDisconnect", CallConvs = new[] { typeof(CallConvStdcall) })]
        public static int FsDisconnect()
        {
            try { AccountManager.DisconnectAll(); WfxEntryPoint.AnnounceDisconnect(); return 0; }
            catch { return -1; }
        }

        /// What FsGetFile receives about the remote file; only the size is of use here.
        [StructLayout(LayoutKind.Sequential)]
        private struct RemoteInfoStruct
        {
            public uint SizeLow;
            public uint SizeHigh;
            public FILETIME LastWriteTime;
            public int Attr;
        }

        [UnmanagedCallersOnly(EntryPoint = "FsGetFileW", CallConvs = new[] { typeof(CallConvStdcall) })]
        public static int FsGetFileW(char* remoteFile, char* localFile, int copyMode, IntPtr ri)
        {
            try
            {
                // Telegram does not report a size for photos, so the one the listing
                // already gave Total Commander is what drives the progress bar
                long expected = 0;
                if (ri != IntPtr.Zero)
                {
                    var info = *(RemoteInfoStruct*)ri;
                    expected = ((long)info.SizeHigh << 32) | info.SizeLow;
                }

                return WfxFunctions.GetFile(new string(remoteFile), new string(localFile), expected) switch
                {
                    WfxFunctions.TransferResult.Ok => FS_FILE_OK,
                    WfxFunctions.TransferResult.Aborted => FS_FILE_USERABORT,
                    _ => FS_FILE_NOTFOUND
                };
            }
            catch { return FS_FILE_READERROR; }
        }

        [UnmanagedCallersOnly(EntryPoint = "FsPutFileW", CallConvs = new[] { typeof(CallConvStdcall) })]
        public static int FsPutFileW(char* localFile, char* remoteFile, int copyFlags)
        {
            try
            {
                return WfxFunctions.PutFile(new string(localFile), new string(remoteFile)) switch
                {
                    WfxFunctions.TransferResult.Ok => FS_FILE_OK,
                    WfxFunctions.TransferResult.Aborted => FS_FILE_USERABORT,
                    _ => FS_FILE_WRITEERROR
                };
            }
            catch { return FS_FILE_WRITEERROR; }
        }

        [UnmanagedCallersOnly(EntryPoint = "FsExecuteFileW", CallConvs = new[] { typeof(CallConvStdcall) })]
        public static int FsExecuteFileW(IntPtr mainWin, char* remoteName, char* verb)
        {
            try
            {
                var name = new string(remoteName);
                if (!name.TrimEnd('\\').EndsWith(Naming.AddAccount, StringComparison.Ordinal))
                    return FS_EXEC_ERROR;

                var accountId = WfxFunctions.AddAccount();
                if (accountId == null) return FS_EXEC_OK;

                // FS_EXEC_OK would leave the panel stale; sending Total Commander into the
                // new account's folder is the documented way to make it re-read the listing.
                WriteRemoteName(remoteName, "\\" + accountId);
                return FS_EXEC_SYMLINK;
            }
            catch { return FS_EXEC_ERROR; }
        }

        /// RemoteName is a caller-owned MAX_PATH buffer; the SDK caps the path at 259 chars.
        private static void WriteRemoteName(char* buffer, string path)
        {
            var n = Math.Min(path.Length, 259);
            for (int i = 0; i < n; i++) buffer[i] = path[i];
            buffer[n] = '\0';
        }

        [UnmanagedCallersOnly(EntryPoint = "FsGetLocalNameW", CallConvs = new[] { typeof(CallConvStdcall) })]
        public static bool FsGetLocalNameW(char* remoteName, int maxlen) => false;

        private const int FS_BITMAP_NONE = 0;
        private const int FS_BITMAP_EXTRACT_YOURSELF_ANDDELETE = 3;
        private const int FS_BITMAP_CACHE = 256;

        [UnmanagedCallersOnly(EntryPoint = "FsGetPreviewBitmapW", CallConvs = new[] { typeof(CallConvStdcall) })]
        public static int FsGetPreviewBitmapW(char* remoteName, int width, int height, IntPtr returnedBitmap)
        {
            try
            {
                var preview = WfxFunctions.PreviewFile(new string(remoteName), width, height);
                if (preview == null) return FS_BITMAP_NONE;

                // hand back the local path: Total Commander decodes the image itself,
                // which keeps bitmap handling out of the plugin entirely
                WriteRemoteName(remoteName, preview);
                return FS_BITMAP_EXTRACT_YOURSELF_ANDDELETE | FS_BITMAP_CACHE;
            }
            catch { return FS_BITMAP_NONE; }
        }

        // ANSI twin: Total Commander decides a feature exists by looking up its
        // non-suffixed name, exactly as it does for FsFindFirst.
        [UnmanagedCallersOnly(EntryPoint = "FsGetPreviewBitmap", CallConvs = new[] { typeof(CallConvStdcall) })]
        public static int FsGetPreviewBitmap(byte* remoteName, int width, int height, IntPtr returnedBitmap)
        {
            try
            {
                var path = Marshal.PtrToStringAnsi((IntPtr)remoteName);
                if (path == null) return FS_BITMAP_NONE;

                var preview = WfxFunctions.PreviewFile(path, width, height);
                if (preview == null) return FS_BITMAP_NONE;

                var bytes = System.Text.Encoding.Default.GetBytes(preview);
                var n = Math.Min(bytes.Length, 259);
                for (int i = 0; i < n; i++) remoteName[i] = bytes[i];
                remoteName[n] = 0;
                return FS_BITMAP_EXTRACT_YOURSELF_ANDDELETE | FS_BITMAP_CACHE;
            }
            catch { return FS_BITMAP_NONE; }
        }
    }
}
