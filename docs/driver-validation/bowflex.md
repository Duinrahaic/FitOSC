# Bowflex driver checklist

Treadmill Type: **Bowflex (Experimental)**. Shared steps (build, connecting, evidence, reconnect) are in
[README.md](README.md).

Status: not yet tested on hardware.

## What the driver does

Basis: Zwift's Bowflex treadmill handler (`libzwiftjni.so`, `BowflexTreadmillStreamHandler`), which only reads from
the treadmill. There is no public specification for this protocol.

- Connects to service `b5c78780-cad7-11e5-b9f8-0002a5d5c51b` or, failing that, `edff9e80-cad7-11e5-ab63-0002a5d5c51b`.
- Subscribes to the stream characteristics and to the status characteristics `4e349c00-…` / `e3f9af20-…`. After the
  first status notification it writes `07-00-D0-00-1F-0A-00` to `1717b3c0-9803-11e3-90e1-0002a5d5c51b` once to start
  the data stream (log: `Starting Bowflex data stream`). That is its only write.
- Reassembles stream records: the first packet carries record bytes 6-19, continuation packets bytes 1-19.

| Value | Source | Shown as |
|---|---|---|
| Speed | tag `a`, uint16 in 0.01 mph | km/h (or mph), rounded to 0.01 km/h |
| Pace | derived from speed | min/km (or min/mile) |
| Incline | tag `b`, whole percent, 0-255 | % |
| State | Running when speed > 0; tag `\|` = 04 zeroes speed | Debug tab |

Not shown: distance, calories, heart rate, elapsed time.

Controls: none. START, PAUSE and STOP do nothing; stop the belt with the console. Incline **+** / **-** send nothing.
Do not press speed **+** / **-**: this driver reports no speed range, so FitOSC throws an error (see README).

## Checklist

1. **Connect.** Connected within 30 s. `FitOSC.getDeviceInfo()` lists one of the two services. The log has
   `Starting Bowflex data stream` followed by `Wrote 07-00-D0-00-1F-0A-00`, exactly once.
2. **Standing still.** Speed 0.0, incline as on the console, state Stopped.
3. **Steady speed.** With **Preferred Units** on Imperial, run 2.0, 3.5 and 5.0 mph from the console for 30 s each.
   FitOSC shows the console's mph within 0.1 mph once steady, and updates at least once a second with no stalls
   (a stall that recovers on the next change points at stream reassembly; attach the log).
4. **Incline.** Step the console incline 0 → 5 → 10 → 0%. FitOSC matches each step within 2 s.
5. **Stopping.** Stop from the console: speed returns to 0 within 2 s and state shows Stopped.
6. **No control.** Press START, PAUSE, STOP and incline **+** once each with the belt stopped: nothing happens on the
   treadmill and the log shows no new `Wrote` line.
7. **Disconnect and reconnect.** README section 5. After each reconnect, step 1's stream-start write appears once
   more and data resumes.

Pass: every line passes.
