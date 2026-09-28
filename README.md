# Garden

> **Stable / maintained.** The engine changes when a script needs a capability
> it doesn't have; day-to-day development happens in the data repo (ROIs,
> actions, the Lua script), not here.

A Windows automation framework for Android devices mirrored through scrcpy.
Automation is driven entirely by on-screen image detection: you define Regions
of Interest (ROIs), record touch actions, and script behavior in Lua. The bot
watches for ROIs to appear and replays actions relative to where each was found.

## How it works

```
Android phone ──USB──> scrcpy server ──TCP──> Garden
                          (H.264 video + control socket)
                                  │
                        ┌─────────┴─────────┐
                        ▼                   ▼
             ffmpeg (H.264 → BGR24)    video ring (raw H.264,
                        │              always-on forensics)
                        ▼
             phone-native frames (e.g. 1080×2400)
                   │                    │
            ROI detection         capture window
           (OpenCV template       (half-res overlay,
            matching + OCR)        debug only)
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
- Tesseract.NET 5.2.0 — OCR (one engine per language, created on demand)
- NLog 6.0.4
- ffmpeg, scrcpy, adb — external, must be on PATH

## Setup

```powershell
./setup.ps1      # installs .NET 8, scrcpy, ffmpeg; sets env vars; downloads tessdata
cd src
dotnet run
```

Environment variables:

- `GARDEN_DATA` — directory holding your ROIs, actions, and Lua script
- `TESSDATA_PREFIX` — directory holding `tessdata/` (`jpn` and `eng` are used)
- `GARDEN_STATE_DIR` *(optional)* — where runtime state lives (`state.json`,
  the detection log). Unset, it sits next to the script. Point it at a synced
  folder (e.g. OneDrive) to share bot state between machines — data is
  durable and versioned; state is mutable runtime memory.

A phone with USB debugging enabled and authorized must be connected
(`adb devices` should show `device`, not `unauthorized`).

### Auto-start at logon

`launch.bat` is the one thing a machine starts: it waits for the phone
(`adb wait-for-device`), runs `dotnet run` from `src` (rebuilds if the source
changed; `bot.autoStart` arms the bot), and relaunches 30s after a crash. A
typed `quit` ends it. To install, drop a shortcut to it (minimized) in
`%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup`. The file is
CRLF + ASCII on purpose (`.gitattributes` pins `*.bat`): bare-LF lines make
`cmd` on a CP932 console eat leading characters.

### config.json

Shipped next to the executable (`../config.json`, linked into the build):

```json
{
    "scale": 1.75,
    "windowPositions": { "topLeft": { "x": 50, "y": 50 }, "spacing": 50 },
    "ocr": { "lang": "jpn" },
    "bot": { "autoStart": true }
}
```

| Key | Meaning |
|-----|---------|
| `scale` | scrcpy window scale |
| `windowPositions` | where the scrcpy and capture windows are placed |
| `ocr.lang` | default Tesseract language for read-areas that don't name their own |
| `bot.autoStart` | arm the bot on launch, exactly as `bot start` would — set `false` for a recording session |

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
| `roi fixed <name>` | Toggle an ROI's fixed-location flag |
| `roi clickpoint <name>` | Re-set the click point (then click the target) |
| `roi box <name>` | Re-draw the box (then drag); re-crops the template, keeps click point and read-areas |
| `roi threshold <name> <v\|off>` | Per-ROI detection threshold override |
| `roi tune <name>` | Sample the on-screen ROI ~3s and set its threshold to 3× the worst score (capped 0.05, floored at the global) |
| `roi readarea <name> add` | Add an OCR read-area (drag, then name it) |
| `roi readarea <name> remove <area>` | Remove a read-area |
| `ocr read <png\|dir> [lang]` | Run the live OCR pipeline on saved crops — verify a read-area offline against known values |
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
| `bot pause` / `bot resume` | Hold at the next relative wait -- mid-script, Lua stack intact -- and continue from that exact statement. Never lands mid-action or inside a `waitUntil` poll; absolute time (schedule, `os.time()`) keeps running |
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
| `roiVisible(name)` → bool | True if the ROI is currently detected (also runs its OCR read-areas) |
| `queueAction(name)` | Replay an action at its recorded coordinates |
| `queueActionAt(name, roi)` | Replay an action offset to the ROI's click point |
| `getRoiScore(name)` → number | Raw match score (lower = better match) |
| `getOcrInt(key)` → int | Digits of the last OCR read of `"roiName/areaName"`; −1 if no digits or never read |
| `getOcrStr(key)` → string | The raw OCR text (for mixed text such as dates) |
| `waitMs(ms)` | Block the bot thread (cancellable by `quit`/`bot stop`/`abort`) |
| `queueWait(ms)` | Enqueue a pause in the action queue |
| `pressHome()` | Android HOME key via the control socket |
| `log(msg)` | Print `[bot] msg` to the console and the detection log |
| `stateSave(table)` | Persist a Lua table as JSON, atomically (temp file + rename) |
| `stateLoad()` → table\|nil | Load the persisted state (see the float caveat below) |
| `saveVideoEvidence(tag)` → path | Copy the newest video-ring segments into `video_ring/saved/<time>_<tag>/` |

### stdlib (engine-shipped Lua combinators)

`stdlib.lua` loads before the user script — generic idioms composed from the
primitives above. Action names are parameters, never assumptions:

| Function | Description |
|----------|-------------|
| `doIf(roi, action, ms)` → bool | If the ROI is visible: replay action at it, wait, return true |
| `repeatUntilVisible(action, roi, tries, ms)` | Repeat an action until a target ROI appears (capped) |
| `drainWhileVisible(roi, action, ms)` | Repeat an action while an ROI remains visible |
| `waitUntil(ms, fn)` | Poll `fn` until it returns non-nil, up to `ms` of real time; nil on timeout. `bot pause` never lands inside one |

The script hot-reloads — edits are picked up live without restarting (note:
script-local variables re-initialize on every reload). `roi_metadata.json`
hot-reloads too, so thresholds, read-areas and languages take effect on the
running engine. Both `quit` and `bot stop` cleanly unwind `main()` even from
inside a `while` loop (via a cancellation exception threaded through the
bindings).

## State persistence

`stateSave`/`stateLoad` give scripts crash-safe memory: a single JSON file
(`state.json` in `GARDEN_STATE_DIR`, or next to the script), written atomically
on every save so a kill mid-write can never corrupt it. Disk is always current,
so a hot-reload or restart resumes from up-to-date state.

**Float caveat:** numbers come back from `stateLoad` as Lua floats. Table
lookups with numeric keys are unaffected (Lua normalizes integral floats), but a
string built from a loaded number gets a trailing `.0` — `"k" .. loadedTime`
forks into a second key after every reload. `math.floor()` numbers before
concatenating them into keys.

## Detection

Templates are matched with `TM_SQDIFF_NORMED` (0 = perfect). A detection is
`score < threshold`, where the threshold is the ROI's own `threshold` from
`roi_metadata.json` if set, else the global 0.005.

**Why per-ROI thresholds exist.** The video path re-encodes every frame, so a
template cut from one frame still scores ~0.003 against other frames of the
same screen — an irreducible noise floor. Any template sits only ~40% under the
global gate and drifts over it as encoding conditions shift, so re-recording
"a fresher variant" holds for about a week. When a score sits just above the
gate at the same location every time, the template is right and the gate is
too tight: `roi tune` measures and sets it. Loosen only after checking the
nearest *confusable sibling* (a different item drawn in the same frame style)
scores well above the new gate — otherwise the bot clicks the wrong thing.

**Reading scores:** a miss just above the threshold is a near-miss (the thing
is on screen); a high score (≳0.15) means genuinely absent.

## OCR read-areas

An ROI can carry named read-areas — rectangles relative to the ROI — that are
OCR'd whenever the ROI is detected. Each entry in `roi_metadata.json` is
`{ name, x, y, width, height, lang? }`. Scripts read results via
`getOcrInt("roiName/areaName")` / `getOcrStr(...)`.

Pipeline: 3× cubic upscale → grayscale → Otsu binarize → pad with a margin in
the image's own page colour → Tesseract `SingleWord` → circled/fullwidth
numerals normalized to ASCII digits. There is no character whitelist; callers
interpret the raw text.

**Language is per read-area.** The jpn model is right for mixed text (a date
with 年/月/日) and wrong for bare digits, where it hallucinates kana; give
digit-only areas `"lang": "eng"`. **Draw digit boxes tight** — the digits and
nothing else. Background caught inside the box (a badge's rounded corner, a
neighbouring glyph) is read as a character, and a wrong *digit* is worse than
no read: `getOcrInt` returns −1 on garbage, which a script can skip.

Verify a read-area before trusting it: cut crops from the video ring, name
them by their true value, and run `ocr read <dir> [lang]`. Every keyed read
also drops what Tesseract saw (gray over binary, tagged `@lang=result`) into
`images/ocr_ring/` — 64 slots, so read soon after the event.

## Detection flight recorder

Every `roiVisible` result (HIT/miss + score) and every `log()` line is appended
to `roi_detections.log` alongside `state.json` — the bot's decision trail.
Consecutive repeats of the same (roi, outcome) collapse into a `repeated xN`
line so poll loops don't flush real history. The file rotates at 8MB into
`roi_detections.old` (weeks of history); a blocked rotation is logged, never
fatal. Read this first when the bot misbehaves — the answer is usually already
on disk.

## Video ring

The raw H.264 packets are also teed into rolling segments under
`images/video_ring/` (~27MB / 30–45s each on a busy screen; 185 kept ≈ 5GB,
hours of active footage). Every segment plays standalone. Retention is derived
from the directory at startup, so it spans restarts. `saveVideoEvidence(tag)`
copies the newest segments into `saved/<time>_<tag>/` and remuxes them to
.mp4; the archive keeps 30 days (100-directory storm backstop). Any IO failure
disables the ring, never the stream. This is the ground truth for anything the
recorder can't settle: what a screen actually showed when a read went wrong.

## Capture resilience

The capture session rebuilds itself when the video stream ends or stalls
(input was injected but no frame followed for 30s). Rendering and scanning
gate on real frame arrival, so a static screen costs almost nothing — a frozen
capture window on a static screen is by design; "the phone changed but the
capture didn't" is the wedge signature.

## Live overlay

With `scan on`, the capture window shows every ROI's current match score,
updated by a background scan thread independent of bot state. Detected ROIs
are colored and boxed, with OCR read-area results and click points drawn, so
you can tune templates and thresholds in real time. Off by default — the bot's
own detection does not depend on it.

## Key design notes

- **Single detection pipeline** — ROI recording and detection both run on the
  same ffmpeg-decoded frames, so template scores are consistent by construction.
- **Fixed-location ROIs** — for small or ambiguous targets, record with
  `roi record fixed`: the template is matched only at its recorded position,
  eliminating false positives from whole-frame search. Position is identity.
- **Input types** — left-click replays as a phone touch event; right-click
  replays as an Android `BACK` key. Both go directly through the scrcpy control
  socket. Gestures (swipes, holds) must be recorded with `action record path` —
  linear recordings carry no movement events and won't register as gestures.
- **Single-threaded ffmpeg decode (`-threads 1`)** — frame-threading buffers
  frames and adds latency proportional to thread count; single-threaded decode
  emits each frame immediately, keeping detection in lock-step with the phone.
- **The `main()` loop lives in C#** — it is the one checkpoint where hot-reload
  is applied between passes and where `quit`/`bot stop` can cancel. Don't move
  the top-level loop into Lua.
