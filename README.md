# claude-trap

Runs the current native Claude Code (2.1.113+, built with Bun) on Windows PCs whose CPU has no SSE4.2/POPCNT,
e.g. Core 2 / Penryn, Harpertown Xeons. Works for the terminal and for the Code -> Local tab in the Claude desktop app.

Unofficial, not affiliated with Anthropic. Use at your own risk.

Without it you get:

    error: this CPU is missing SSE4.2 support, which Bun requires.

Until now the options were staying on 2.1.112 (last JS-only version, too old for newer models) or running everything
under Intel SDE, which works but is painfully slow. This runs the binary natively and only emulates the few
instructions the CPU doesn't have.

Numbers from my machine (2x Xeon E5450, Claude Code 2.1.328):

| | Intel SDE | claude-trap |
|---|---|---|
| `claude --version` | ~35 s | 0.3 s |
| one `claude -p` request | > 60 s | ~7 s |
| RAM | ~4.6 GB | ~250 MB |

## How it works

claude-trap.exe starts the real claude.exe as a debugger and:

- finds Bun's SSE4.2 startup check and NOPs the jump to the error, in memory only (the exe on disk is not touched).
  The location is found automatically, so it should survive updates. Result is cached in `%LOCALAPPDATA%\claude-trap\`.
- sets `SIMDUTF_FORCE_IMPLEMENTATION=westmere`. On these CPUs simdutf otherwise picks no implementation at all and
  returns empty strings, and Bun dies with `error: EISDIR reading ""`.
- catches illegal instruction faults and emulates POPCNT, PCMPESTRI/PCMPESTRM/PCMPISTRI/PCMPISTRM, PCMPGTQ and CRC32.
- patches hot POPCNT reg,reg sites to jump to a small native popcount routine, so they stop faulting.

The JIT and things like BoringSSL check CPUID themselves, so only ahead-of-time compiled code needs this.

## Requirements

- Windows 10/11 x64. .NET Framework 4.x is part of Windows, build.cmd uses its csc.exe.
- Git for Windows (Claude Code on Windows needs git-bash).
- For the desktop app: open the Code tab once before patching, even if it shows the SSE4.2 error.
  That is when the app downloads its own copy of Claude Code, which is what gets patched.

Tested only on Penryn (Xeon E5450), Windows 10 19044, Claude Code 2.1.326 and 2.1.328.

## Install

Build:

    build.cmd

There are no prebuilt binaries here on purpose, build it yourself.

### Desktop app

    powershell -ExecutionPolicy Bypass -File patch-claude-app.ps1

Renames the app's `claude-code\<version>\<hash>\claude.exe` to `claude-real.exe` and puts claude-trap.exe in its place.
No need to close the app, just start a new Code session.

The app downloads new Claude Code versions now and then. To re-patch them automatically (per-user logon task, no admin):

    powershell -ExecutionPolicy Bypass -File install-autopatch.ps1

Undo:

    powershell -ExecutionPolicy Bypass -File install-autopatch.ps1 -Remove
    powershell -ExecutionPolicy Bypass -File patch-claude-app.ps1 -Restore

### Terminal

Get the native binary:

    npm pack @anthropic-ai/claude-code-win32-x64
    tar -xf anthropic-ai-claude-code-win32-x64-*.tgz

and make a `claude-native.cmd` somewhere on PATH:

    @echo off
    set DISABLE_AUTOUPDATER=1
    set CLAUDE_TRAP_REAL=C:\path\to\package\claude.exe
    "C:\path\to\claude-trap.exe" %*

## Settings

Environment variables, or `key=value` lines in `claude-trap.ini` next to the exe or in `%LOCALAPPDATA%\claude-trap\`:

- `CLAUDE_TRAP_REAL` / `real` - path to the real binary, default `claude-real.exe` next to the launcher
- `CLAUDE_TRAP_REWRITE` / `rewrite` - `0` turns off the POPCNT patching (slower but simpler)
- `CLAUDE_TRAP_VERBOSE` / `verbose` - `1` logs every new emulated address
- `SIMDUTF_FORCE_IMPLEMENTATION` / `simdutf` - default `westmere`

Log is in `%TEMP%\claude-trap.log`. If you see `UNHANDLED #UD` there, some instruction isn't emulated yet,
open an issue with that line.

## Known issues

- Not official. A Claude Code or Bun update can break it.
- Only the instructions listed above are emulated. Anything else crashes Claude Code (nothing gets damaged).
- The PCMPxSTRx emulation is the most complicated part. Works for me, but a bug there would show up as weird text handling.
- Runs Claude Code under a debugger, so you can't attach another one. Killing the launcher kills Claude Code too.
- Antivirus might not like a program that writes to another process's memory.
- Startup is still a few seconds slower than on a modern CPU.
- Right after an app update, the first session can still show the SSE4.2 error if it starts before autopatch catches
  the new version. Open a new session.

Related: anthropics/claude-code#85571, anthropics/claude-code#105074

## Po polsku

Uruchamia najnowszy Claude Code (terminal i zakladka Code -> Local w aplikacji Claude) na procesorach bez SSE4.2,
np. Core 2 / Penryn. Wymaga Windows 10/11 x64 i Git for Windows. W aplikacji trzeba raz otworzyc zakladke Code
(nawet z bledem), zeby pobrala Claude Code.

    build.cmd
    powershell -ExecutionPolicy Bypass -File patch-claude-app.ps1
    powershell -ExecutionPolicy Bypass -File install-autopatch.ps1

Wycofanie: `install-autopatch.ps1 -Remove`, potem `patch-claude-app.ps1 -Restore`. Nieoficjalne, na wlasne ryzyko.

## License

MIT
