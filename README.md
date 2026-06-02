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

`setup.ps1` prompts for two environment variables:

- `GARDEN_DATA` — path to a directory holding your ROIs, actions, and Lua script
- `TESSDATA_PREFIX` — directory holding `tessdata/` for Tesseract

A phone with USB debugging enabled and authorized must be connected
(`adb devices` should show `device`, not `unauthorized`).

## Interactive commands

Typed into the console while running:

| Command | Description |
|---------|-------------|
| `roi record <name>` | Record an ROI by dragging a box on the capture window |
| `roi stop` | Cancel ROI recording |
| `roi list` | List all ROIs |
| `roi remove <name>` | Delete an ROI and its image |
| `action record <name>` | Record a touch action (linear) |
| `action record path <name>` | Record a touch action (freehand path) |
| `action reset` | Clear the current recording buffer |
| `action stop` | Stop recording and save |
| `action replay <name>` | Replay a saved action |
| `action list` | List all actions |
| `action remove <name>` | Delete an action |
| `image save <file.png>` | Save a screenshot of the current frame |
| `bot start` / `bot stop` | Start/stop bot automation |
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

### Lua API

| Function | Description |
|----------|-------------|
| `roiVisible(name)` → bool | True if the ROI is currently detected |
| `queueAction(name)` | Replay an action at its recorded coordinates |
| `queueActionAt(name, roi)` | Replay an action offset to the ROI's click point |
| `getRoiScore(name)` → number | Raw match score (lower = better match) |
| `getOcrInt(key)` → int | Read an integer from a configured OCR read-area |
| `waitMs(ms)` | Block the bot thread (cancellable by `quit`/`bot stop`) |
| `queueWait(ms)` | Enqueue a pause in the action queue |

The script hot-reloads — edits are picked up live without restarting. Both
`quit` and `bot stop` cleanly unwind `main()` even from inside a `while` loop
(via a cancellation exception threaded through the bindings).

## Live overlay

The capture window shows, below the FPS counter, every ROI's current match
score updated by a background scan thread (~5 Hz, independent of bot state).
Detected ROIs are colored and boxed so you can tune templates and thresholds
in real time.

## Key design notes

- **Single detection pipeline** — ROI recording and detection both run on the
  same ffmpeg-decoded frames, so template scores are consistent by construction.
- **`TM_SQDIFF_NORMED`** — 0 is a perfect match. Lower scores mean better matches.
- **Template quality matters more than threshold tuning** — larger, distinctive
  templates give a much wider on/off-screen score gap than small ambiguous ones.
- **Input types** — left-click replays as a phone touch event; right-click
  replays as an Android `BACK` key. Both go directly through the scrcpy control
  socket.
