# Endex driver checklist

Treadmill Type: **Endex (Experimental)**. Shared steps (build, connecting, evidence, reconnect) are in
[README.md](README.md).

Status: not yet tested on hardware.

## What the driver does

Basis: Kinomap's Endex support. There is no public specification. Commands are six bytes (command, four data bytes,
check byte) over service `0000fff0-…` (notify `fff1`, write `fff2`); replies are seven bytes.

- **Handshake.** `A1-C2-00-00-00-9C`, then on each reply `E2-00-00-00-00-1D`, `D4-02-00-00-00-29`,
  `D3-00-05-00-01-28` and `E1-00-00-00-00-1E`. Connecting waits until the console answers the last one; a missing
  reply restarts the handshake after 3 s.
- **Polling**, once a second: `A1-8A…`, `A1-84…`, `A1-85…`, `A1-86…`, `A1-82…`, `A1-83…`, `A1-91…`.
- **Incline** is the only command: `D2-<percent>-00-00-00-<check>`, whole percent from 0 to 16 (2 % is
  `D2-02-00-00-00-2F`). 16 is Kinomap's default equipment cap; its Endex class also holds a constant of 40. Treat 16
  as FitOSC's limit, not the console's.
- START, PAUSE, STOP and speed do nothing: **stop the belt with the console or the safety key**.

| Value | Reply | Shown as |
|---|---|---|
| Speed | `A1 82`: whole km/h + hundredths, only while the status is running | km/h (or mph) |
| Pace | derived from speed | min/km (or min/mile) |
| Incline | `A1 83`: signed whole percent | % |
| Distance | `A1 85`: byte 2 × 1000 m + byte 3 × 10 m | km (or miles) |
| State | `A1 91`: 1 idle, 3 running, 5 paused | Debug tab |

Kinomap drops the hundredths of speed; FitOSC keeps them, so FitOSC may read up to 0.09 km/h above Kinomap.
Calories, heart rate and elapsed time are not read.

Do not press speed **+** / **-**: this driver reports no speed range, so FitOSC throws an error (see README).

## Checklist

Stand beside the belt for every first command. Use inclines up to 3 %.

1. **Connect.** Connected within 30 s. The log shows the handshake writes above in order, then the polling writes
   every second. If connecting times out, attach the log: it shows which reply never came.
2. **Readings.** Start the belt from the console and run 3.0 and 5.0 km/h for 30 s each. FitOSC speed is within
   0.1 km/h of the console; state shows Running. Pause from the console: state Paused, speed 0.
3. **Distance.** Over 0.50 km by the console, FitOSC distance rises 0.50 km ± 0.01 km.
4. **Incline.** Incline **+** from 0 %: `Wrote D2-01-00-00-00-2C`, the treadmill reaches 1 % and FitOSC shows 1 %.
   `FitOSC.setIncline(2)` gives `D2-02-…` and 2 %; incline **-** steps back down to 0 %.
5. **Negative incline.** Only if the console can decline: set -1 % or -2 % on the console. FitOSC shows the same
   negative value (not 255 or 254).
6. **Controls that do nothing.** Belt running at a walking speed, hand on the console stop: press STOP in FitOSC. The
   belt keeps running and the log shows no new write other than polling. Stop it from the console.
7. **Disconnect and reconnect.** README section 5; the handshake runs again after each reconnect.

Pass: lines 1-4, 6 and 7 pass; line 5 passes or is not applicable.
