# iConsole iRunning+ driver checklist

Treadmill Type: **iConsole iRunning+ (Experimental)**. Shared steps (build, connecting, evidence, reconnect) are in
[README.md](README.md).

Status: not yet tested on hardware.

> **Connecting can start the workout.** When the console reports it is ready and waiting, the driver sends the start
> command (`F0-A5-…-02-…`), as Kinomap does. Clear the belt, stand beside it and keep the safety key attached before
> connecting. Turn off **Automatically connect to last known device on startup** while testing this driver.

## What the driver does

Basis: Kinomap's iConsole support. There is no public specification. Frames are `F0`, command, two console IDs,
data, then the low byte of the sum of all previous bytes, over service `0000fff0-…` (notify `fff1`, write `fff2`) or,
when that is missing, the Microchip ISSC service `49535343-fe7d-4ae5-8fa9-9fafd205e455`.

- **Handshake.** `F0-A0-02-02-94`; the console answers with its IDs, which every later frame carries. Then reset
  (`A5 04`, three times 500 ms apart), `A1`, sport mode `A3`, route `AC` and sport data `A4`, and loading status `AF`
  until the console reports loaded. Connecting waits for that; a missing reply restarts the handshake after 5 s.
- **Polling**, once a second after the handshake: `F0-A2-<ids>-<sum>`.
- **Start**: when a data reply says the console is ready and waiting, the driver sends `A5 02`. The START, PAUSE and
  STOP buttons and speed do nothing: **stop the belt with the console or the safety key**.
- **Incline**: `AC` with incline + 1, `64`, `64`. Whole percent from 0 to 16, rounded (2 % with IDs `02 02` is
  `F0-AC-02-02-03-64-64-6B`). 16 is Kinomap's default equipment cap; Kinomap's iConsole code itself has no upper
  limit.

| Value | Source | Shown as |
|---|---|---|
| Speed | data reply bytes 12-13: two base-100 digits, each sent + 1, in 0.1 km/h; only while running | km/h (or mph) |
| Pace | derived from speed | min/km (or min/mile) |
| Incline | **the last incline FitOSC sent**, not a reading from the console | % |
| State | data reply program/phase/mode: running, paused, stopped | Debug tab |

Distance, calories, heart rate and elapsed time are not read. Do not press speed **+** / **-**: this driver reports
no speed range, so FitOSC throws an error (see README).

## Checklist

Stand beside the belt for every command. Use inclines up to 3 %.

1. **Connect.** Belt clear. Connected within 30 s. The log shows `Wrote F0-A0-02-02-94`, the handshake frames and
   then `F0-A2-…` once a second. Record whether the belt or the console's workout started on its own, and when.
2. **Readings.** Run 3.0 and 5.0 km/h from the console for 30 s each. FitOSC speed is within 0.1 km/h of the
   console; state Running.
3. **Incline.** Incline **+** from 0 %: `Wrote F0-AC-<ids>-02-64-64-…` and the treadmill reaches 1 %. Incline **-**
   returns to 0 %. FitOSC shows the value it sent, so compare it with the console's own display.
4. **Pause and stop from the console.** Pause on the console: state Paused, speed 0. Stop: state Stopped.
5. **Controls that do nothing.** Belt running at walking speed, hand on the console stop: press STOP in FitOSC. The
   belt keeps running and the log shows no write other than polling. Stop it from the console.
6. **Disconnect mid-handshake.** Disconnect within 2 s of connecting: the handshake writes stop in the log.
7. **Disconnect and reconnect.** README section 5; the handshake runs again with `F0-A0-02-02-94`.

Pass: every line passes. Line 1's automatic start is expected; an unexpected belt movement at any other time is a
fail.
