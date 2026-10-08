# TotalGram — Telegram file system plugin for Total Commander

Browse your Telegram chats as folders in Total Commander: read posts, download
attachments, upload files back.

## Install

**Auto-install:** press Enter on `TotalGram-<version>.zip` in Total Commander —
it finds `pluginst.inf` inside and offers to install the package. Both builds are
included; Total Commander picks the one matching its own bitness.

**Manual:** copy `TotalGram.wfx` (32-bit) and `TotalGram.wfx64` (64-bit) into the
same folder, then go to **Configuration → Options → Plugins → File system
plugins → Add** and select `TotalGram.wfx`.

## First run

The plugin talks to Telegram with your own API credentials. Create them once at
[my.telegram.org](https://my.telegram.org) → *API development tools*, then write
them into `%APPDATA%\Igor Zviagintsev\TotalGram\config.ini`:

```ini
api_id=1234567
api_hash=0123456789abcdef0123456789abcdef
```

Open **Telegram** in the Network Neighborhood, press Enter on `[+ Add account]`
and answer the prompts: phone number, the code Telegram sends you, and the
two-factor password if you use one. Several accounts can be added; each gets its
own folder.

## Structure

```
\Telegram\
  <Account>\
    <Telegram folder>\        folders you created in Telegram
    All chats\
      Archived\               archived chats
      <Chat>\
        Media\                photos and videos
          Incoming\           the same files, only the ones you received
          Outgoing\           the same files, only the ones you sent
        Files\                documents
        Links\                .url shortcuts for links found in messages
        Music\                audio tracks
        Voice Messages\       voice messages
        GIF\                  animations
        All files\            everything above as one flat list
        Post 123\             one folder per post (channels, forum topics)
          message.md          the post's text
        <Topic>\              forum topics, in supergroups that use them
```

Private chats and ordinary groups are flat: their files sit directly in the chat
folder. Channels and forum topics get one folder per post.

Every content folder, and `All files` too, holds an `Incoming` and an `Outgoing`
subfolder next to its files — the same list split by who sent the message. In a
channel you did not post to, `Outgoing` is simply empty.

`All files` exists because Total Commander's branch view (Ctrl+B) does not work
inside plugin file systems — it shows every file of the chat in one list.

## Uploading

Copying a file into a chat sends it. The destination folder decides the message
type:

| Folder | Sent as | Accepts |
|---|---|---|
| chat root | detected from the file type | anything |
| `Media` | photo, or streamable video | images, video |
| `Music` | audio track | audio |
| `Voice Messages` | voice message | audio |
| `GIF` | animation | `.gif`, `.mp4`, `.webm` |
| `Files` | document | anything |
| `Links` | the link as a message | `.url` |

A file whose type does not match the folder is rejected.

## Settings

`%APPDATA%\Igor Zviagintsev\TotalGram\config.ini`

| Key | Meaning |
|---|---|
| `api_id`, `api_hash` | Telegram API credentials, required |
| `max_items` | how many older entries one visit fetches, default `2000`; `0` reads a folder whole on the first visit |
| `log_paths` | `1` logs every directory request into the FTP log, for diagnostics |

Content folders ask Telegram for one kind of file at a time, the same way the
"Shared media" tabs do, so they reach years back rather than only through the
most recent messages.

Each folder is remembered in `%APPDATA%\Igor Zviagintsev\TotalGram\cache\`, so
Telegram is only asked for what the cache cannot answer. A folder that is too big
to read in one go is not cut off at `max_items`: every visit walks another stretch
further back, until the whole history is there. Opening a folder that is already
complete costs one request.

Deleting the cache folder is safe — it is rebuilt on the next visit. A file deleted
in Telegram stays listed until then, and fails to download.

Sessions live in `%APPDATA%\Igor Zviagintsev\TotalGram\accounts\`. Deleting a
session file signs that account out.

## Requirements

- Windows 10 or newer
- Total Commander 10.50 or newer
- a Telegram account

No .NET runtime is needed: the plugin is compiled ahead of time into native code.

## Building

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download) and Visual Studio
Build Tools with the **Desktop development with C++** workload — Native AOT
compiles through the MSVC linker and the Windows SDK libraries.

One command builds both bitnesses and the installable package:

```powershell
.\build_package.ps1
```

It writes `dist\TotalGram-<version>.zip` (the auto-install archive, version taken
from the build time as `yy.MM.dd.HHmm`) and leaves loose binaries in
`dist\release\` for manual installation.

For the development loop, one bitness is enough — build the one your Total
Commander uses:

```powershell
dotnet publish TotalGram\TotalGram.csproj -c Release -r win-x86 --self-contained
```

The result is `TotalGram\bin\Release\net10.0-windows\win-x86\native\TotalGram.dll`;
rename it to `TotalGram.wfx` where the plugin is installed. Close Total Commander
first — it keeps the file open while loaded.

### Two things that bite

**`vswhere.exe` is not on PATH.** The link step then fails with
`error MSB3073 ... link.exe`. `build_package.ps1` prepends the usual location
itself; when publishing by hand, do the same:

```powershell
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"
```

**The x86 export list is maintained by hand.** On x86 the linker decorates
stdcall exports (`_FsInit@16`), which Total Commander does not recognise, so the
`PatchDefFileForX86` target in `TotalGram.csproj` overwrites the generated `.def`
with clean names. A new export must be added there as well, or it simply will not
appear in the 32-bit build. Note that Total Commander decides which features a
plugin has by looking up the **ANSI** name even when it calls the Unicode one —
`FsFindFirst` and `FsGetPreviewBitmap` exist only for that reason.

## Removing

**Configuration → Options → Plugins → File system plugins**, select *Telegram*,
click **Remove**, then delete the plugin folder and
`%APPDATA%\Igor Zviagintsev\TotalGram\`.
