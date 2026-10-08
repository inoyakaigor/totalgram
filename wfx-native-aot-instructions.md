# Building a Total Commander WFX plugin with .NET Native AOT

Checklist and gotchas for a coding agent writing (or fixing) a Total Commander
filesystem plugin (`.wfx`/`.wfx64`) in C# via `PublishAot`. Every item here
was hit for real in this repo — most of them do not show up until you try to
load the finished DLL in Total Commander itself, and TC's error reporting for
plugin rejection is a single generic message with no detail, so getting these
right up front saves hours of blind debugging.

Reference: the official SDK is vendored at [`WFX-SDK/`](WFX-SDK) (mirror of
github.com/ghisler/WFX-SDK). Read `docs/overview.htm` and
`docs/unicode_support.htm` before debugging any "plugin not recognized"
symptom — both gotchas below that cost the most debugging time are spelled
out there in plain text.

## 1. Why Native AOT, not a C++ shim

`PublishAot=true` + `NativeLib=Shared` makes the ILC compiler emit an
ordinary native PE DLL with a real export table, no CLR header, no managed
runtime dependency at the PE level. Total Commander cannot tell it apart from
an MSVC-built DLL. This claim is false for classic (JIT) .NET — it only holds
from .NET 7 onward. Don't build a C++ shim around a managed DLL; it's not
needed.

## 2. csproj

```xml
<PropertyGroup>
  <TargetFramework>net10.0-windows</TargetFramework>
  <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  <UseWindows>true</UseWindows>

  <PublishAot>true</PublishAot>
  <NativeLib>Shared</NativeLib>                 <!-- mandatory: without it the output is an .exe -->
  <InvariantGlobalization>true</InvariantGlobalization>
  <TrimmerSingleWarn>false</TrimmerSingleWarn>   <!-- see AOT warnings below -->

  <!-- Avoid NativeAOT thread-creation-from-DllMain hangs in a large host process (dotnet/runtime#109543) -->
  <ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>
</PropertyGroup>
```

Prerequisite: VS Build Tools with the "Desktop development with C++"
workload, and `vswhere.exe`'s directory (`...\Microsoft Visual
Studio\Installer`) on `PATH` for the build to find the linker.

Windows x86 Native AOT is supported since .NET 9, so both `win-x64` and
`win-x86` RIDs are valid — build both if the target TC bitness is unknown.

## 3. Export layer

Every function TC resolves via `GetProcAddress` needs:

```csharp
[UnmanagedCallersOnly(EntryPoint = "FsInit", CallConvs = new[] { typeof(CallConvStdcall) })]
```

`CallConvStdcall` is not optional — the WFX ABI is `__stdcall`; omitting it
corrupts the stack on x86.

Constraints on every exported method:

- static, non-generic, in a non-generic type.
- params/return must be genuinely blittable: `int`, `IntPtr`, `void*`,
  `byte*`, `char*`, or a blittable struct (see §5). `bool` is not blittable —
  use `int`.
- no exception may escape — wrap the body in try/catch and return an error
  code. An unhandled exception across this boundary kills the TC process.
- managed code should call the underlying implementation, not the exported
  wrapper.

TC callback pointers received in `FsInit` (`progressProc`/`logProc`/
`requestProc`): keep as `IntPtr`, invoke via
`delegate* unmanaged[Stdcall]<...>`, not
`Marshal.GetDelegateForFunctionPointer`.

## 4. The mandatory export list is ANSI-named — even for a Unicode-only plugin

**This is the #1 cause of "This is not a valid plugin!" with an otherwise
perfectly working DLL.**

Per `WFX-SDK/docs/overview.htm`: the minimum functions are `FsInit`,
`FsFindFirst`, `FsFindNext`, `FsFindClose` — no `W` suffix. Quote: *"If any
of the minimum functions isn't implemented, loading of the plugin will
fail."* TC performs this check via `GetProcAddress` **before ever calling
`FsInit`**, so a missing mandatory export produces the generic invalid-plugin
error with zero further diagnostic output — nothing you log inside `FsInit`
or a `[ModuleInitializer]` will ever fire.

Per `WFX-SDK/docs/unicode_support.htm`: Unicode support adds `W`-suffixed
counterparts (`FsFindFirstW`, `FsFindNextW`, ...), and TC prefers them when
present on NT-based systems. But a **single combined file** (`.wfx`/
`.wfx64`, no separate `.uwfx`) still needs the ANSI names to exist as real
exports. Quote: *"You cannot have just the .uwfx plugin if you want to
create a Unicode-only plugin! Instead, create a combined Unicode/ANSI
plugin, where the ANSI functions are just empty functions returning an
error."* — i.e. they don't need to be fully functional, just present.

Fix: implement `FsFindFirst`/`FsFindNext` as thin wrappers around the `W`
logic (convert the ANSI path with `Marshal.PtrToStringAnsi`, call the shared
implementation, convert the result struct back — see §5 for why the struct
conversion needs care). `FsFindClose`, `FsGetDefRootName` and
`FsSetDefaultParams` have no `W` form at all and are already ANSI by
definition — no extra work needed there.

Checklist before you ever open Total Commander:

- [ ] `FsInit` exported (ANSI, no `W` variant exists)
- [ ] `FsFindFirst` exported (ANSI — separate from `FsFindFirstW`)
- [ ] `FsFindNext` exported (ANSI — separate from `FsFindNextW`)
- [ ] `FsFindClose` exported (ANSI, no `W` variant exists)
- [ ] Dump the export table and confirm all four names literally appear
      (see §7) — don't assume, verify.

## 5. Structs passed by pointer must be genuinely blittable

`WIN32_FIND_DATAW`/`WIN32_FIND_DATA` are filled through a raw pointer TC
allocates on its own stack — not through CLR marshalling. A `string` field
with `[MarshalAs(UnmanagedType.ByValTStr, SizeConst = N)]` has **no effect**
on raw unsafe pointer access; that attribute only matters when the CLR
itself performs P/Invoke marshalling. With a `string` field, the struct
still contains a managed GC reference at that offset, and reading/writing it
through a pointer into TC's native buffer is memory corruption waiting to
happen (the GC will eventually try to trace a "reference" that's actually
raw native bytes).

The Native AOT compiler will actually warn you about this
(`CS8500: This takes the address of, gets the size of, or declares a
pointer to managed type '...'`) — treat that warning as a build-breaking bug,
not noise, for any struct that crosses this boundary.

Correct pattern:

```csharp
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
    public fixed char cFileName[260];       // not `string` + MarshalAs
    public fixed char cAlternateFileName[14];
}
```

An ANSI counterpart (`WIN32_FIND_DATA`) needs the same shape with
`fixed byte` instead of `fixed char`.

## 6. x86 stdcall name decoration

ILC's default generated `.def` file on `win-x86` appends `@N` byte-count
suffixes to every export (stdcall convention) — `GetProcAddress("FsInit")`
then fails to find `_FsInit@16`. Patch the generated `.def` with an MSBuild
target that rewrites it with clean names for **every** exported function:

```xml
<Target Name="PatchDefFileForX86"
        Condition="'$(RuntimeIdentifier)' == 'win-x86'"
        BeforeTargets="LinkNative"
        AfterTargets="IlcGenerateCompleteProgram">
  <PropertyGroup>
    <GeneratedDefFile>$(IntermediateOutputPath)native/$(AssemblyName).def</GeneratedDefFile>
    <DefFileContent>EXPORTS
  DotNetRuntimeDebugHeader DATA
  FsInit = TotalGram_TotalGram_Wfx_Exports__FsInit
  FsFindFirst = TotalGram_TotalGram_Wfx_Exports__FsFindFirst
  FsFindNext = TotalGram_TotalGram_Wfx_Exports__FsFindNext
  <!-- ...one line per [UnmanagedCallersOnly] method... -->
</DefFileContent>
  </PropertyGroup>
  <WriteLinesToFile File="$(GeneratedDefFile)" Lines="$(DefFileContent)" Overwrite="true" Condition="Exists('$(GeneratedDefFile)')" />
</Target>
```

**This list must be kept in sync by hand.** Adding a new
`[UnmanagedCallersOnly]` export in C# and forgetting to add it here means it
silently doesn't exist on the x86 build, with no build error — only a
runtime `GetProcAddress` failure inside TC.

## 7. Verify the export table before touching Total Commander

No external tool needed — a ~40-line PowerShell script that walks the PE
export directory is enough (`IMAGE_DOS_HEADER.e_lfanew` → COFF header →
optional header → data directory #0 → section RVA-to-offset → export name
table). Confirm:

- machine type matches the target TC bitness (`0x14c` = x86, `0x8664` = x64)
- `FsInit`, `FsFindFirst`, `FsFindNext`, `FsFindClose` present, undecorated
  (no `@N` suffix, no leading underscore)

This check takes seconds and rules out the most common failure mode (§4)
before any TC-specific debugging starts.

## 8. Manual install vs. auto-install package — two different deliverables

- **Manual install** (Configuration → Filesystem plugins → Add): point
  directly at the raw `win-x86`/`win-x64` native output, renamed to
  `<name>.wfx` / `<name>.wfx64`. No packaging needed.
- **Auto-install** (double-click a `.wfx` in TC's file panel): TC treats a
  double-clicked file with a registered plugin extension as a ZIP installer
  containing `pluginst.inf`. Two rules that are easy to get backwards:
  - The **archive's own extension must be `.wfx`, not `.zip`** — TC only
    offers auto-install for a recognized plugin extension. Building a
    correct ZIP and naming it `dist/TotalGram.zip` produces a file nobody
    can auto-install; it must be renamed (or built directly) as
    `TotalGram.wfx`.
  - `pluginst.inf` must sit at the **root** of the archive, not nested in a
    subfolder. `ZipFile.CreateFromDirectory(dir, out, level,
    includeBaseDirectory)` — the last argument must be `false`, or every
    entry ends up under a `<dir-name>/` prefix and TC won't find
    `pluginst.inf` at all.
  - `pluginst.inf` must be plain ASCII/UTF-8 **without a BOM**. C#'s
    `Encoding.UTF8` writes a BOM by default — use `Encoding.ASCII` or an
    explicit BOM-less UTF8 encoding.
  - Any generated `.bat` referenced via `preinstall=` in `pluginst.inf` must
    come from a **double-quoted** PowerShell string, so `` `r`n `` actually
    expands to CRLF. A single-quoted string leaves the backticks literal
    and produces a broken one-line batch file that silently does nothing.

## 9. Diagnosing "This is not a valid plugin!"

This exact message is Total Commander's single generic response for several
unrelated causes: architecture mismatch, a missing mandatory export, or (on
double-click) the file not being a valid ZIP installer. It carries no
distinguishing detail. Diagnose cheapest-first:

1. Confirm the TC build's bitness (Help → About) matches the file
   (`.wfx` → 32-bit, `.wfx64` → 64-bit).
2. Dump the export table (§7) and confirm the four mandatory ANSI names
   (§4) are present. **This is the most common cause and requires no
   interaction with Total Commander at all.**
3. Only if exports check out: `LoadLibraryEx` + `GetProcAddress` + an actual
   call from a throwaway test harness (PowerShell or a tiny native C
   program compiled with `cl.exe`), matching the target process's bitness,
   to confirm the DLL is loadable and functional outside TC.
4. Attaching a real debugger (cdb/WinDbg) to a running `TOTALCMD.EXE` is a
   last resort. It is fragile — killing the debugger process kills the
   debuggee under an invasive attach (Windows default kill-on-exit
   behavior), so a naive `taskkill` on the debugger closes the user's TC
   session. Prefer launching TC fresh under the debugger
   (`cdb -o -g -G <path to TOTALCMD.EXE>`) over attaching to an already
   running instance, and expect that without an interactive stdin channel
   you cannot reliably resume a stopped breakpoint — plan for a
   fully-scripted, self-continuing `-c` command list, not a session you
   intend to steer live. In this project, the debugger never surfaced
   anything the SDK docs didn't already explain directly — read
   `WFX-SDK/docs/overview.htm` and `docs/unicode_support.htm` first.

## 10. Expect AOT trim/reflection warnings

`<TrimmerSingleWarn>false</TrimmerSingleWarn>` and report the full warning
list instead of suppressing it. Reflection-heavy dependencies (a Telegram
client library, `System.Text.Json` without a source-generated context, etc.)
will surface `IL2067`/`IL3050`-class warnings here — these point at real
runtime risk under AOT trimming and are a separate task; don't silence them
blindly.
