# FitShow driver checklist

Treadmill Type: **FitShow (Experimental)**. Shared steps (build, connecting, evidence, reconnect) are in
[README.md](README.md).

Status: not yet tested on hardware.

## What the driver does

Basis: Kinomap's FitShow support (the `com.xm.fit.fsble` SDK and its caller). There is no public specification.
Frames are `02`, payload, XOR of the payload, `03`, over service `0000fff0-…` (notify `fff1`, write `fff2`).

- Polls status every 500 ms: `Wrote 02-51-51-03`.
- Asks for the incline range at connect and every 2 s until answered: `Wrote 02-50-03-53-03`. Until then incline is
  0-16 %; the log shows `FitShow reported incline range <min>-<max>%` when it answers. A reported maximum of 0
  disables incline control (Kinomap would keep 16).
- START and PAUSE are not offered: the belt is started from the console, and the SDK has no pause.
- STOP sends `02-53-03-50-03`.
- Speed and incline always go together: `02-53-02-<speed>-<incline>-<xor>-03`, speed in 0.1 km/h, incline in whole
  signed percent rounded half up as Kinomap does (2.5 % is 3 %, -2.5 % is -2 %). 4.0 km/h at 0 % is
  `02-53-02-28-00-79-03`; 4.0 km/h at 2 % is `02-53-02-28-02-7B-03`.

| Value | Source | Shown as |
|---|---|---|
| Speed | status byte, 0.1 km/h | km/h (or mph) |
| Pace | derived from speed | min/km (or min/mile) |
| Incline | status byte, signed whole percent | % |
| State | status: running, paused, stopped | Debug tab |

Speed and incline are only read while the status is running, stopping or end; otherwise speed shows 0. Distance,
calories, heart rate and elapsed time are not read.

Controls offered: STOP, speed **+** / **-** and incline **+** / **-** within the range. This driver reports no speed
range, so speed targets are only limited to what the frame can encode (0-25.5 km/h).

## Checklist

Stand beside the belt for every first command. Use speeds up to 4 km/h and inclines up to 3 %.

1. **Connect.** Connected within 30 s. The log shows `Wrote 02-51-51-03` every 500 ms and `Wrote 02-50-03-53-03`
   until a `FitShow reported incline range` line. Record the reported range; if none appears within 10 s, record
   that too.
2. **Readings.** Start the belt from the console and run 2.0 and 4.0 km/h for 30 s each. FitOSC speed matches the
   console within 0.1 km/h; incline matches the console.
3. **Speed.** `FitOSC.setSpeed(3.0)`: `Wrote 02-53-02-1E-<incline>-…`, belt settles at 3.0 km/h within 0.1 km/h,
   incline unchanged. Speed **+** once: the belt rises about 0.3 km/h.
4. **Incline.** Only on a model with incline: incline **+** once from 0 %: the bytes carry the current speed and
   incline 1 (`02-53-02-<speed>-01-…`), the treadmill reaches 1 % and the speed does not change. Incline **-** returns
   to 0 %. If line 1 reported a negative minimum, `FitOSC.setIncline(-1)` must reach -1 % and FitOSC must show -1 %.
5. **Start and pause.** START and PAUSE are disabled while connected.
6. **Stop.** Belt running: STOP stops the belt (`Wrote 02-53-03-50-03`).
7. **Disconnect and reconnect.** README section 5; the range query runs again after reconnecting.

Pass: every line passes; line 4 is not applicable on a model without incline.
