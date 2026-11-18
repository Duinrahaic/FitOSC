# UREVO driver checklist

Treadmill Type: **UREVO (Experimental)**. Shared steps (build, connecting, evidence, reconnect) are in
[README.md](README.md).

Status: not yet tested on hardware.

## What the driver does

Basis: the UREVO app (Flutter; recovered from its compiled code). There is no public specification. Frames are `02`,
command, sub-command (not for `Q`), data, `(sum of command, sub and data) XOR 5A`, `03`, over service `0000fff0-…`
(notify `fff1`, write `fff2`).

- Polls status every 500 ms: `Wrote 02-51-0B-03`.
- Asks for the speed range (`02-50-02-08-03`) and the incline range (`02-50-03-09-03`) at connect and every 2 s until
  each is answered; the log shows `UREVO reported speed range …` and `UREVO reported incline range …`. Until then
  speed is limited to 1.0-6.0 km/h and incline control is off.
- START: `02-53-01-00-00-00-00-00-00-00-00-0E-03` (a free run with no target), or `02-53-09-06-03` to resume when
  paused. PAUSE `02-53-0A-07-03`. STOP `02-53-03-0C-03`.
- Speed and incline always go together: `02-53-02-<speed>-<incline>-<checksum>-03`, both in tenths (km/h and %),
  truncated as the app does (3.29 km/h is sent as 3.2). 3.0 km/h at 0 % is `02-53-02-1E-00-29-03`; at 1.0 % it is `02-53-02-1E-0A-27-03`.

| Value | Status field | Shown as |
|---|---|---|
| Speed | 0.1 km/h | km/h (or mph) |
| Incline | 0.1 % | % |
| Elapsed time | uint16, s | duration |
| Distance | uint16 × 10 m; × 1 m when the name is exactly `URTM018` | km (or miles) |
| Calories | 0.1 kcal | kcal |
| Steps | uint16 | `FitOSC.getAppState()` only |
| State | standby, running, paused, stopped | Debug tab |

Known gaps, which this checklist is meant to settle:

- The incline-range reply also carries a configuration byte that the app reads, including a miles flag. The
  recovered code does not name its bits, and the app's own speed unit stays 0.1 km/h, so this driver ignores the byte
  and always reads speed as km/h. Line 7 checks a console set to miles.
- The app selects the 1 m distance model when its model value equals `URTM018`; this driver compares the Bluetooth
  name the same way. Whether the app's model value is always the advertised name is not confirmed, so record the
  name and the distance in line 3.

Controls offered: START, PAUSE, STOP, speed **+** / **-** within the reported range, and incline **+** / **-** once
an incline range is reported.

## Checklist

Stand beside the belt for every first command. Use speeds up to 4 km/h and inclines up to 3 %.

1. **Connect.** Connected within 30 s. Within 5 s both range queries (`02-50-02-08-03` and `02-50-03-09-03`) stop
   repeating. Record the `UREVO reported speed range …` and `UREVO reported incline range …` lines and the Bluetooth
   name. A model without incline answers the incline query with equal limits: that query stops with no incline line
   and incline control stays off, which is a pass. A speed query that stops without a speed line means the treadmill
   sent a speed range the driver ignored; record it, because speed then stays limited to 1.0-6.0 km/h.
2. **Start.** Press START: `Wrote 02-53-01-00-…-0E-03` and the belt starts.
3. **Readings.** At 3.0 km/h for 60 s: FitOSC speed matches the console within 0.1 km/h, incline matches, elapsed time
   matches within 2 s, distance within the console's last digit (10 m), and calories within 1 kcal. Compare steps
   from `FitOSC.getAppState()` with the console if it shows them.
4. **Speed.** Speed **+** once: the belt rises about 0.3 km/h and the bytes carry the new speed with the current
   incline. `FitOSC.setSpeed(3.0)` gives `02-53-02-1E-<incline>-…`.
5. **Incline.** Only on a model with incline (line 1 logged an incline range): at 3.0 km/h, `FitOSC.setIncline(1)`
   gives `02-53-02-1E-0A-27-03` and the treadmill reaches 1.0 %, speed unchanged. Incline **+** and **-** step 0.1 %
   (one press from 1.0 % sends incline byte `0B` or `09`). `FitOSC.setIncline(0)` returns to 0 %.
6. **Pause, resume, stop.** PAUSE: `02-53-0A-07-03`, state Paused. START: `02-53-09-06-03`, belt resumes. STOP:
   `02-53-03-0C-03`, belt stops.
7. **Miles.** Only if the console can switch to miles: switch it, run 2.0 mph by the console and record what FitOSC
   shows with **Preferred Units** on Imperial. Pass if FitOSC shows 2.0 mph; if it shows 1.2 mph, the miles flag gap
   above is real.
8. **Disconnect and reconnect.** README section 5; the range queries run again after each reconnect.

Pass: lines 1-4, 6 and 8 pass; lines 5 and 7 pass or are not applicable.
