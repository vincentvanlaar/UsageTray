<p align="center">
  <img src="docs/logo.png" alt="" width="96">
</p>

<h1 align="center">UsageTray</h1>

<p align="center">
  <b>Your Codex and Claude subscription limits at a glance — and whether you're on pace to make it to the reset.</b>
</p>

<p align="center">
  <a href="https://github.com/MosrednA/UsageTray/releases/latest"><img src="https://img.shields.io/github/v/release/MosrednA/UsageTray?style=flat-square&color=4cc38a" alt="Latest release"></a>
  <a href="https://github.com/MosrednA/UsageTray/actions/workflows/ci.yml"><img src="https://img.shields.io/github/actions/workflow/status/MosrednA/UsageTray/ci.yml?style=flat-square&label=build" alt="Build"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-1c1d22?style=flat-square" alt="Windows 10 | 11">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-1c1d22?style=flat-square" alt="MIT license"></a>
</p>

<p align="center">
  <img src="docs/flyout.png" alt="UsageTray flyout showing Codex and Claude limits and memory use" width="440">
</p>

A tiny native Windows tray app. One glance at the icon tells you if you're burning through a limit; one click shows every window with a pace marker, so you know whether to slow down — or that you've got room to spare.

- **Pace, not just percentage.** A white tick marks where you'd be with perfectly even usage. Past the tick means you're going too fast.
- **Codex + Claude in one place.** Weekly and 5-hour windows, model-specific limits, Codex credits and free resets.
- **Memory too.** RAM and commit charge under the limits. Click them for the apps that use the most memory, with Claude Code sessions by name. Open a session in the Claude desktop app, or end an app, from there.
- **Your sessions at a glance.** Click *Claude* or *Codex* for a side panel with every running session: what it waits for, how much memory (and optionally CPU and GPU) it uses, and a stop button once it has been idle for a while.
- **Only what you want.** The ⚙ in the footer turns sections on or off, and adds CPU and GPU load.
- **No extra logins.** Uses the sign-ins of the official Codex and Claude Code CLIs. No API keys.
- **Lightweight.** Native WinForms, no dependencies, no telemetry. Refreshes every 2 minutes (Claude at most every 5).

## Install

**Requires** Windows 10 or 11, plus the [Codex CLI](https://github.com/openai/codex) and/or [Claude Code](https://code.claude.com/docs/en/overview) signed in with a subscription. UsageTray shows whichever of the two it finds.

Download **[UsageTray-win-x64.exe](https://github.com/MosrednA/UsageTray/releases/latest/download/UsageTray-win-x64.exe)** and run it. Nothing to install.

> **"Windows protected your PC"?** The exe isn't code-signed, so SmartScreen warns on first run. Click *More info → Run anyway*, or [build it yourself](#build-from-source).
>
> Prefer a ~250 KB download instead of ~50 MB? Grab `UsageTray-win-x64-small.exe` from the [release](https://github.com/MosrednA/UsageTray/releases/latest) instead — it needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

Then:

1. **Keep the icon visible** — drag it from the `^` overflow onto the taskbar (or *Taskbar settings → Other system tray icons → UsageTray*).
2. **Start with Windows** — right-click the icon and tick it.
3. **Signed out?** Click the red message in the flyout (or right-click → *Sign in to Codex…* / *Sign in to Claude…*). This runs the official `codex login` or `claude auth login`.

## Reading it

```
7d   ███│███│███│██▏│░░░│░░░│░░░   64%   +11   3d 6h
│    │             │               │     │     └─ time until reset
│    │             │               │     └────── points ahead of (+) or behind (−) even pace
│    │             │               └──────────── used
│    │             └──────────────────────────── pace: where even usage would put you now
│    └────────────────────────────────────────── usage, split per day (7d) or per hour (5h)
└─────────────────────────────────────────────── window: 5h, 7d, opus, sonnet
```

| | |
|---|---|
| 🟢 **Green** | On or under pace |
| 🟠 **Orange** | Faster than pace |
| 🔴 **Red** | ≥ 90 % used, or > 15 points ahead of pace |

### Tray icon

Pick a style via right-click → **Icon**. All three follow the same colors and adapt to light and dark taskbars.

| Style | | Shows |
|---|---|---|
| **Ring** (default) | <img src="docs/icon-ring.png" alt="Ring icon" height="48"> | The window most at risk: % inside, progress ring, pace notch |
| **Bars** | <img src="docs/icon-bars.png" alt="Bars icon" height="48"> | Codex, Claude and RAM side by side; Codex and Claude have a pace tick |
| **Classic** | <img src="docs/icon-classic.png" alt="Classic icon" height="48"> | The window most at risk as a colored tile |

Hover the icon for a one-line summary of everything.

> **Tip:** a 5-hour window only starts when you send your first message, so early on you'll almost always be "ahead of pace". For 5h, watch the percentage; for 7d, the pace is what matters.

| Action | |
|---|---|
| Click the icon | Open / close the flyout |
| Right-click | Refresh · Sign in to Codex / Claude · Icon style · Start with Windows · Quit |
| `F5` / `R`, or click the footer | Refresh now |
| `Esc`, or click elsewhere | Close |
| Click *System* in the flyout | Memory per app: click a row for its processes, right-click to end the app or one process |
| Click a Claude Code session there | Open it in the Claude desktop app (its chevron shows the processes) |
| Click *Claude ›* or *Codex ›* | Sessions panel beside the flyout: status, memory, CPU/GPU. Click a Claude session to open it; *stop* appears after the idle time you set |
| Click ⚙ in the footer | Quick settings: show Codex, Claude, Memory, CPU, GPU; when to offer *stop* |

<p align="center">
  <img src="docs/sessions.png" alt="Sessions panel listing Claude Code sessions with status and memory per session" width="440">
</p>

Both flyouts leave room for the taskbar, including when **Automatically hide the taskbar** is enabled. The limits flyout keeps that clearance when a refresh changes its height.

The UI is in English, or Dutch when Windows is set to Dutch. Force a language with `USAGETRAY_LANG=en` or `nl`.

## Where the numbers come from

| | Source | Notes |
|---|---|---|
| **Codex** | The official `codex app-server` (`account/rateLimits/read`), run locally | Live, using Codex's own login. Also shows credits and available free resets. If the CLI isn't on your `PATH`, it falls back to the last snapshot in `~/.codex/sessions` (labeled *as of 14:50*). |
| **Claude** | `api.anthropic.com/api/oauth/usage`, using the Claude Code CLI login in `~/.claude/.credentials.json` | Same data as `/usage` in Claude Code. Expired access tokens are refreshed and written back atomically, so the CLI keeps working. The endpoint is rate limited, so it's polled at most every 5 minutes; on a 429 UsageTray backs off (up to 30 min) and keeps showing the last numbers (labeled *as of 14:50*). |

| **Sessions** | `~/.claude/sessions/<pid>.json` for Claude Code (status: working, waiting, idle); for Codex, the `~/.codex/sessions` rollout that a running `codex` process holds open (found with the Restart Manager) | Codex writes no "waiting for you" state to disk, so a Codex session shows working or idle. |
| **System** | `GlobalMemoryStatusEx`, and the process list for the memory view | Apps are sorted by commit (private bytes): idle apps are paged out, so their working set looks small. Claude Code session names come from `~/.claude/sessions/<pid>.json`, the file Claude Code keeps per running session. |

A provider that isn't installed is simply hidden.

### Privacy & security

- **Runs** `codex app-server` locally for a second on each refresh; Codex handles its own auth and network.
- **Reads** the Claude Code credentials file, and your Codex session logs as a fallback. When you open the memory view, it also reads the process list with command lines and `~/.claude/sessions`.
- **Ends** a process only when you pick *End* in the memory view and confirm, or *stop* in the sessions panel (a session in a terminal asks first; a Claude desktop session keeps its conversation and resumes on your next message). System processes, and processes it has no rights to end, are greyed out.
- **Writes** only refreshed Claude tokens back to that same credentials file, plus the optional autostart entry (`HKCU\…\Run`).
- **Talks to** `platform.claude.com` (token refresh) and `api.anthropic.com` (usage) itself. Nothing else. No telemetry, no analytics.

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/MosrednA/UsageTray.git
cd UsageTray
dotnet run -c Release
```

| | |
|---|---|
| `UsageTray --snapshot out.png` | Render the flyout with your live data to a PNG |
| `UsageTray --render-assets` | Regenerate `docs/*.png` and `assets/app.ico` from demo data |
| `UsageTray --preview-icons out.png` | All icon styles at 16–32 px on dark and light taskbars, magnified |
| `UsageTray --procs out.png` | Render the memory view with your live data to a PNG |
| `UsageTray --procs` | Open only the memory view, at the cursor |
| `UsageTray --sessions Claude out.png` | Render the sessions panel (`Claude` or `Codex`) with your live data to a PNG |

Tag a commit `v*` to build and publish a release automatically.

<details>
<summary>Project layout</summary>

| File | Role |
|---|---|
| `TrayApp.cs` | Tray icon, menu, refresh timer, sign-in, autostart |
| `PopupForm.cs` | The flyout — custom-drawn, DPI-aware |
| `ProcessForm.cs` | The memory view — apps by memory, open a session, end a process |
| `SessionForm.cs` | The sessions panel — status, memory, CPU/GPU and stop per session |
| `SessionSource.cs` | Claude Code and Codex sessions, their status and CPU use |
| `Meters.cs` | CPU (`GetSystemTimes`) and GPU (PDH *GPU Engine* counters) load |
| `Flyout.cs` | Window base shared by both flyouts |
| `SystemSource.cs` | RAM and commit charge |
| `ProcessSource.cs` | Process snapshot, grouping per app, ending processes |
| `ClaudeSessions.cs` | Claude Code session names from `~/.claude/sessions` |
| `CodexSource.cs` | Codex via `codex app-server`, with session-log fallback |
| `ClaudeSource.cs` | OAuth token refresh + usage endpoint |
| `Models.cs` | Usage windows, pace math, formatting |
| `IconRenderer.cs` | Tray icon styles and logo |
| `DocAssets.cs` | README images and `.ico` generator |
| `L.cs` | English / Dutch strings |

</details>

## Caveats

- The Claude usage endpoint is **undocumented** and may change without notice. If Claude stops showing up after an update, that's the likely cause.
- `~/.claude/sessions` and the `claude://` link that opens a session are **undocumented** too. If they change, sessions show without a name or link; the rest of the memory view keeps working.
- `codex app-server` is marked *experimental* by the Codex CLI. If it changes, UsageTray falls back to the session logs until it's updated.
- UsageTray reuses the Claude Code CLI login rather than logging in itself. The Claude desktop app keeps its own login, which UsageTray can't access.
- UsageTray is an independent project and is **not affiliated with or endorsed by OpenAI or Anthropic**. Codex and Claude are trademarks of their respective owners.

## License

[MIT](LICENSE)
