# Garden

> **⚠️ In active development.** APIs, commands, and behavior may change without notice.

A Windows automation framework for Android devices mirrored through scrcpy.
Automation is driven entirely by on-screen image detection: you define Regions
of Interest (ROIs), record touch actions, and script behavior in Lua. The bot
watches for ROIs to appear and replays actions relative to where each was found.

## How it works

```
Android phone ──USB──> scrcpy server ──TCP──> Garden
                          (H.264 video + control socket)
                                  │
                                  ▼
                       ffmpeg (H.264 → BGR24)
                                  │
                                  ▼
                    phone-native frames (e.g. 1080×2400)
                          │                    │
                   ROI detection         capture window
                  (OpenCV template       (half-res overlay,
                   matching)              debug only)
                          │
                          ▼
                       Lua script  ──> touch/key events ──> phone
                                        (scrcpy control socket)
```

Everything operates in **phone-native coordinates**, so the system is
machine-agnostic — independent of host display resolution or window position.

## Tech stack

- .NET 8.0 (Windows), C#
- OpenCvSharp 4.11.0 — template matching (`TM_SQDIFF_NORMED`)
- NLua 1.7.3 — Lua-scripted bot logic
- Tesseract.NET 5.2.0 — OCR for reading on-screen integers
- NLog 6.0.4
- ffmpeg, scrcpy, adb — external, must be on PATH

## Setup

```powershell
./setup.ps1      # installs .NET 8, scrcpy, ffmpeg; sets env vars; downloads tessdata
cd src
dotnet run
```

Environment variables:

- `GARDEN_DATA` — path to a directory holding your ROIs, actions, and Lua script
- `TESSDATA_PREFIX` — directory holding `tessdata/` for Tesseract
- `GARDEN_STATE_DIR` *(optional)* — where runtime state lives (`state.json`,
  detection log). Unset, it sits next to the script. Point it at a synced
  folder (e.g. OneDrive) to share bot state between machines — data is
  durable and versioned; state is mutable runtime memory.

A phone with USB debugging enabled and authorized must be connected
(`adb devices` should show `device`, not `unauthorized`).

## Interactive commands

Typed into the console while running:

| Command | Description |
|---------|-------------|
| `roi record <name>` | Record an ROI by dragging a box on the capture window |
| `roi record fixed <name>` | Record a fixed-location ROI (matched in place, no search — for small/ambiguous targets) |
| `roi stop` | Cancel ROI recording |
| `roi list` | List all ROIs |
| `roi remove <name>` | Delete an ROI and its image |
| `roi rename <old> <new>` | Rename an ROI (image + metadata) |
| `action record <name>` | Record a touch action (linear clicks) |
| `action record path <name>` | Record a touch action (freehand path — required for swipes/gestures) |
| `action reset` | Clear the current recording buffer |
| `action stop` | Stop recording and save |
| `action replay <name>` | Replay a saved action |
| `action list` | List all actions |
| `action remove <name>` | Delete an action |
| `image save <file.png>` | Save a screenshot of the current frame |
| `lua <code>` | Run Lua against the live bot state (REPL; runs on the bot thread) |
| `abort` | Cancel a running `lua` eval |
| `scan on` / `scan off` | Toggle the background detection scan / overlay (default off) |
| `bot start` / `bot stop` | Start/stop bot automation |
| `help` | Show command help |
| `quit` | Exit |

## Scripting

The bot runs a `main()` function from your Lua script in a poll loop. A typical
script is a flat cascade of independent `if roiVisible(...)` checks — each
evaluated every pass, so the bot can pick up from any starting state. No state
machine required.

```lua
function main()
    if roiVisible("some_button") then
        queueActionAt("tap", "some_button")   -- replay "tap" at the ROI's click point
        waitMs(1000)
    end
    if roiVisible("some_icon") then
        queueAction("swipe")                  -- replay "swipe" at its recorded position
        waitMs(1000)
    end
end
```

### Lua API (engine bindings)

| Function | Description |
|----------|-------------|
| `roiVisible(name)` → bool | True if the ROI is currently detected (also triggers its OCR read-areas) |
| `queueAction(name)` | Replay an action at its recorded coordinates |
| `queueActionAt(name, roi)` | Replay an action offset to the ROI's click point |
| `getRoiScore(name)` → number | Raw match score (lower = better match) |
| `getOcrInt(key)` → int | Read an integer from an OCR read-area (`key` = `"roiName/areaName"`); −1 if unread |
| `waitMs(ms)` | Block the bot thread (cancellable by `quit`/`bot stop`/`abort`) |
| `queueWait(ms)` | Enqueue a pause in the action queue |
| `log(msg)` | Print `[bot] msg` to the console |
| `stateSave(table)` | Persist a Lua table as JSON, atomically (temp file + rename) |
| `stateLoad()` → table\|nil | Load the persisted state; numeric keys round-trip correctly |

### stdlib (engine-shipped Lua combinators)

`stdlib.lua` loads before the user script — generic idioms composed from the
primitives above. Action names are parameters, never assumptions:

| Function | Description |
|----------|-------------|
| `doIf(roi, action, ms)` → bool | If the ROI is visible: replay action at it, wait, return true |
| `repeatUntilVisible(action, roi, tries, ms)` | Repeat an action until a target ROI appears (capped) |
| `drainWhileVisible(roi, action, ms)` | Repeat an action while an ROI remains visible |

The script hot-reloads — edits are picked up live without restarting (note:
script-local variables re-initialize on every reload). Both `quit` and
`bot stop` cleanly unwind `main()` even from inside a `while` loop (via a
cancellation exception threaded through the bindings).

## State persistence

`stateSave`/`stateLoad` give scripts crash-safe memory: a single JSON file
(`state.json` in `GARDEN_STATE_DIR`, or next to the script), written atomically
on every save so a kill mid-write can never corrupt it. Disk is always current,
so a hot-reload or restart resumes from up-to-date state.

## Detection flight recorder

Every `roiVisible` result (HIT/miss + match score) is appended to
`roi_detections.log` alongside `state.json` — a rolling trace of the bot's
recent perception. Consecutive repeats of the same (roi, outcome) collapse
into a `repeated xN` line so poll loops don't flush real history. The file
rotates at 10KB into `roi_detections.old`; total disk is bounded and logging
never needs to stop. Reading scores: a miss just above the threshold is a
near-miss (template/threshold problem — the thing is on screen); a high score
means genuinely absent.

## Live overlay

With `scan on`, the capture window shows every ROI's current match score,
updated by a background scan thread independent of bot state. Detected ROIs
are colored and boxed, with OCR read-area results and click points drawn, so
you can tune templates and thresholds in real time. Off by default — the bot's
own detection does not depend on it.

## OCR read-areas

An ROI can carry named read-areas (rectangles relative to the ROI) whose
contents are OCR'd (digit-whitelisted) whenever the ROI is detected. Results
are read from Lua via `getOcrInt("roiName/areaName")`. Size read boxes
generously and validate/parse in script — overshoot is recoverable, undershoot
truncates.

## Key design notes

- **Single detection pipeline** — ROI recording and detection both run on the
  same ffmpeg-decoded frames, so template scores are consistent by construction.
- **`TM_SQDIFF_NORMED`** — 0 is a perfect match. Lower scores mean better matches.
- **Template quality matters more than threshold tuning** — larger, distinctive
  templates give a much wider on/off-screen score gap than small ambiguous ones.
- **Fixed-location ROIs** — for small or ambiguous targets, record with
  `roi record fixed`: the template is matched only at its recorded position,
  eliminating false positives from whole-frame search.
- **Input types** — left-click replays as a phone touch event; right-click
  replays as an Android `BACK` key. Both go directly through the scrcpy control
  socket. Gestures (swipes, holds) must be recorded with `action record path` —
  linear recordings carry no movement events and won't register as gestures.
- **Single-threaded ffmpeg decode (`-threads 1`)** — frame-threading buffers
  frames and adds latency proportional to thread count; single-threaded decode
  emits each frame immediately, keeping detection in lock-step with the phone.
