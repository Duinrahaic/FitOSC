# KingSmith FTMS driver checklist

Treadmill Type: **KingSmith FTMS (Experimental)**. Shared steps (build, connecting, evidence, reconnect) are in
[README.md](README.md).

Status: not yet tested on hardware.

## What the driver does

The generic FTMS driver plus the unlock newer KingSmith treadmills expect before they accept control, as the KS Fit
app sends it. FTMS fields follow the Bluetooth SIG definitions:
[Treadmill Data](https://bitbucket.org/bluetooth-SIG/public/src/main/gss/org.bluetooth.characteristic.treadmill_data.yaml),
[Supported Speed Range](https://bitbucket.org/bluetooth-SIG/public/src/main/gss/org.bluetooth.characteristic.supported_speed_range.yaml),
[Supported Inclination Range](https://bitbucket.org/bluetooth-SIG/public/src/main/gss/org.bluetooth.characteristic.supported_inclination_range.yaml).

- **Unlock.** If the treadmill has characteristic `d18d2c10-c44c-11e8-a355-529269fb1459`, the driver writes
  `01-00` followed by the last six characters of the Bluetooth name as hex digits, one per byte (a non-hex character
  becomes `00`), then waits 200 ms. For a name ending `A1B2C3` that is `01-00-0A-01-0B-02-0C-03`. The log shows
  `Unlocking KingSmith treadmill <name>`. Without the characteristic it behaves like the generic FTMS driver.
- **Control.** After the unlock it requests FTMS control (`Wrote 00` to `00002ad9-…`). A refusal fails the
  connection. START repeats unlock and request, waits 500 ms and sends `07`; if the request is refused it logs
  `Not starting the KingSmith treadmill` and sends nothing more.
- **Commands.** PAUSE `08-02`, STOP `08-01`, speed `02` + uint16 little-endian in 0.01 km/h (4.00 km/h is
  `02-90-01`), incline `03` + sint16 in 0.1 % (2 % is `03-14-00`). Speed and incline are clamped to the ranges the
  treadmill reports at connect (logged as `Supported Speed Range` / `Supported Incline Range`).
- **Telemetry.** Whatever the treadmill flags in Treadmill Data: speed, distance, incline, pace, energy, heart rate,
  elapsed time and so on.

Known gaps, which this checklist is meant to settle:

- KS Fit unlocks and requests control again before every speed change; this driver only does so at connect and START.
  Line 6 checks whether that matters.
- The conversion review reported that KS Fit reads a step count behind Treadmill Data flag bit 13, which the SIG
  reserves; its byte position is not confirmed. This driver ignores it, so steps are not shown.
- Shared FitOSC limits (README): speed **+** / **-** need a reported speed range, incline **-** stops at 0 %, and an
  incline reported as unavailable shows as 3276.7 %.

## Checklist

Stand beside the belt for every first command. Use speeds up to 4 km/h.

1. **Connect.** Connected within 30 s. Record whether the log shows `Unlocking KingSmith treadmill` and the
   `Wrote 01-00-…` bytes; check they match the last six characters of the name. Then `Wrote 00` to `00002ad9-…` and
   no `rejected control point opcode 00`.
2. **Readings.** Run 2.0 and 4.0 km/h from the console or remote for 30 s each. FitOSC speed is within 0.1 km/h of
   the console and distance rises with it within 0.01 km.
3. **Start.** Belt stopped: press START. The log shows the unlock, `Wrote 00` and `Wrote 07`; the treadmill starts.
4. **Speed.** `FitOSC.setSpeed(3.0)`: `Wrote 02-2C-01`, belt at 3.0 km/h within 0.1 km/h. Speed **+** once (only if
   line 1 logged a speed range): belt rises about 0.32 km/h.
5. **Incline.** Only on a model with incline: `FitOSC.setIncline(2)` gives `Wrote 03-14-00` and 2 %; `setIncline(0)`
   returns to 0 %. Otherwise mark not tested.
6. **Speed after a while.** Leave it running 5 minutes, then `FitOSC.setSpeed(3.5)`. Pass if the belt changes. If the
   log shows `rejected control point opcode 02`, record the result code: that is the KS Fit re-unlock gap above.
7. **Pause and stop.** PAUSE: `Wrote 08-02`, belt stops; START resumes. STOP: `Wrote 08-01`, belt stops.
8. **Steps.** Record whether the console counts steps. FitOSC is expected not to show them.
9. **Disconnect and reconnect.** README section 5; the unlock and `Wrote 00` appear again after each reconnect.

Pass: lines 1-4, 6, 7 and 9 pass; line 5 passes or is not applicable.
