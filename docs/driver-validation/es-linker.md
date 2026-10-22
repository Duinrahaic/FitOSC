# EQiSports / CitySports (ES Linker) driver checklist

Treadmill Type: **EQiSports / CitySports (Experimental)**. Shared steps (build, connecting, evidence, reconnect) are
in [README.md](README.md).

Status: not yet tested on hardware. A CITYSPORTS-Linker treadmill has been seen to offer only the EFC service.

## What the driver does

Basis: the EQI Sports app. There is no public specification. Like the app, the driver speaks one of two protocols,
chosen by the service the treadmill offers, and logs which: `EQiSports treadmill speaks the ES Linker protocol` or
`EQiSports treadmill speaks the EQiSports EFC protocol`. Both send frames as header, command, payload length,
payload, XOR of every previous byte.

### ES Linker

Service `0000fff0-…` (notify `fff1`, write `fff2`). Frames start with `A9`.

- **Pairing.** Asks for the console's units (`A9-F2-01-2F-75`) and sends `A9-08-01-<random>-…` every 3 s until the
  console sends a pairing challenge, then answers it (log: `ES Linker treadmill paired`). After that it sends a
  keep-alive (`A9-A0-03-<status>-<speed>-00-…`) every second.
- **Discovery.** After the first heartbeat it asks for the speed range (`A9-0A-01-<random>-…`); the log shows
  `ES Linker reported speed range <min>-<max> km/h, incline <min>-<max>%`, then it asks whether pause is supported
  (`A9-9E-01-FE-C8`). Until the range arrives speed is limited to 1.0-12.0 km/h and incline control is off. A
  reported minimum of 0 is accepted.
- **Units.** A console set to miles (unit reply `02`) sends speed in 0.1 mph; the driver converts it, so FitOSC always
  works in km/h.
- **Commands.** START `A9-A3-01-01-0A` (`A9-A3-01-02-09` to resume when paused). PAUSE `A9-A3-01-03-08`, offered only once the
  console says it supports pause (log: `ES Linker pause supported: True`). STOP `A9-A3-01-00-0B`. Speed
  `A9-01-01-<0.1 of the console's unit>-…`: 3.0 km/h on a metric console is `A9-01-01-1E-B7`. Incline
  `A9-04-01-<whole %>-…`: 2 % is `A9-04-01-02-AE`. Targets are rounded half up, as the app's `BigDecimal` `HALF_UP`
  does.

### EQiSports EFC

Service `ffeeddcc-bbaa-9988-7766-554433221100` (notify `…102`, write `…101`). FitOSC sends frames starting `A1`; the
treadmill sends frames starting `1A`. There is no pairing or keep-alive.

- **Start-up.** Asks for device information (`A1-05-00-A4`); the log shows `EFC treadmill manufacturer …, model …,
  firmware …`. The treadmill then sends status (`1A-01-09-…`) and workout (`1A-02-0C-…`) frames by itself.
- **Discovery.** Every status frame carries the speed and incline range, the current speed and incline, the unit and
  the state; the log shows `EQiSports EFC reported speed range <min>-<max> km/h, incline <min>-<max>%` when it
  arrives or changes.
- **Units.** A console set to miles sends speed in 0.1 mph and distance in thousandths of a mile; the driver converts
  both.
- **Commands.** START and resume `A1-03-01-01-A2`. PAUSE `A1-03-01-03-A0`, always offered, as in the app. STOP
  `A1-03-01-05-A6`. Speed `A1-01-02-01-<0.1 of the console's unit>-…`: 3.0 km/h on a metric console is
  `A1-01-02-01-1E-BD`. Incline `A1-02-02-01-<whole %>-…`: 2 % is `A1-02-02-01-02-A2`.
- **Faults.** Codes 17, 18, 20, 21, 23 and 25 in the state field are logged as `EFC treadmill reported fault <code>`
  and shown as state Unknown.

| Value | ES Linker source | EFC source | Shown as |
|---|---|---|---|
| Speed | speed reports, 0.1 km/h or 0.1 mph | status, 0.1 km/h or 0.1 mph | km/h (or mph) |
| Incline | incline reports, whole percent | status, whole percent | % |
| Elapsed time | heartbeat, seconds | workout, seconds | duration |
| Distance | heartbeat, metres | workout, metres or 0.001 mile | km (or miles) |
| Calories | heartbeat, 10 cal units | workout, 0.1 kcal units | kcal |
| Steps | heartbeat | workout | `FitOSC.getAppState()` only |
| Heart rate | - | workout, hand grips; 0 is ignored | bpm |
| State | status: idle, pre-workout, running, pausing, paused, post-workout, stopped | status: the same, without stopped | Debug tab |

The console's counters roll over at 100 min, 9,999 steps and 1,000 kcal. FitOSC adds one full period each time a
counter drops, and starts again from zero when the console moves from idle, post-workout or stopped into pre-workout
or running. That reset rule is inferred, not taken from the app, and so is the 1,000 kcal period for ES Linker.

Controls offered: START, STOP, speed **+** / **-** within the reported range, incline **+** / **-** once a range is
reported, and PAUSE when the console supports it.

## Checklist

Stand beside the belt for every first command. Use speeds up to 4 km/h and inclines up to 3 %. Record which protocol
line 1 logged; where the two differ, the EFC value follows the ES Linker one.

1. **Connect.** Connected within 30 s with the protocol line in the log. ES Linker: `ES Linker treadmill paired`,
   then a keep-alive every second. EFC: the device information line, then status and workout frames. Record the range
   line and whether pause is supported.
2. **Start.** Press START: `Wrote A9-A3-01-01-0A` (EFC: `A1-03-01-01-A2`) and the belt starts.
3. **Readings.** At 3.0 km/h for 60 s: speed within 0.1 km/h of the console, incline matches, elapsed time within
   2 s, distance within 10 m, calories within 1 kcal, and steps from `FitOSC.getAppState()` within 2 of the console.
   EFC: heart rate matches the console while holding the grips.
4. **Speed.** `FitOSC.setSpeed(3.0)` gives `A9-01-01-1E-B7` (EFC: `A1-01-02-01-1E-BD`) on a metric console and the
   belt settles at 3.0 km/h. Speed **+** once raises it about 0.3 km/h.
5. **Incline.** Only on a model with incline (line 1 reported one): `FitOSC.setIncline(2)` gives `A9-04-01-02-AE`
   (EFC: `A1-02-02-01-02-A2`) and 2 %; incline **-** returns to 0 %.
6. **Pause and stop.** If pause is supported: PAUSE gives `A9-A3-01-03-08` (EFC: `A1-03-01-03-A0`) and state Paused;
   START gives `A9-A3-01-02-09` (EFC: `A1-03-01-01-A2`) and the belt resumes. If not, PAUSE stays disabled. STOP gives
   `A9-A3-01-00-0B` (EFC: `A1-03-01-05-A6`) and the belt stops.
7. **New workout.** Start a second workout after STOP: elapsed time, distance, calories and steps start again from
   zero.
8. **Miles.** Only if the console can switch to miles: switch it, reconnect, run 2.0 mph by the console. With
   **Preferred Units** on Imperial FitOSC shows 2.0 mph, and `FitOSC.setSpeed(3.2)` (km/h) sends `A9-01-01-14-…`
   (EFC: `A1-01-02-01-14-B7`), which is 2.0 mph.
9. **Rollover (optional, long).** If you can walk past 100 minutes in one workout, elapsed time keeps counting past
   1:40:00 instead of dropping to 0. The same holds for steps past 9,999.
10. **Disconnect and reconnect.** README section 5; the connection steps run again and the counters continue from the
    console's values.

Pass: lines 1-4, 6, 7 and 10 pass; lines 5, 8 and 9 pass or are not tested. Rollover arithmetic does not need a long
walk to be trusted, but line 9 is the only check of the console's real reset behaviour.
