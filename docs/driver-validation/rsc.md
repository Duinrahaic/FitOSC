# Running Speed & Cadence (RSC) driver checklist

Treadmill Type: **Running Speed & Cadence (Experimental)**. Shared steps (build, connecting, evidence, reconnect)
are in [README.md](README.md).

Status: not yet tested on hardware.

## What the driver does

Basis: Bluetooth SIG Running Speed and Cadence Service 0x1814, RSC Measurement 0x2A53
([GATT Specification Supplement definition](https://bitbucket.org/bluetooth-SIG/public/src/main/gss/org.bluetooth.characteristic.rsc_measurement.yaml)).
It only reads; it never writes to the device beyond enabling notifications.

| Value | Source | Shown as |
|---|---|---|
| Speed | uint16, 1/256 m/s | km/h (or mph), rounded to 0.01 km/h |
| Pace | derived from speed | min/km (or min/mile) |
| Distance | uint32, 1/10 m, only when flag bit 1 is set | km (or miles) |
| State | Running when speed > 0, otherwise Stopped | Debug tab |

Not shown: cadence, stride length, the walking/running flag, incline, calories, heart rate and elapsed time (the
duration stays at 0).

Controls: none. START, PAUSE, STOP, speed and incline do nothing. Do not press speed **+** / **-**: this driver
reports no speed range, so FitOSC throws an error (see README).

## Checklist

Use a treadmill that advertises service 0x1814, or a footpod. `FitOSC.getDeviceInfo()` must list 0x1814 and 0x2A53.

1. **Connect.** Connected within 30 s. The log shows no `Failed to subscribe to RSC Measurement`.
2. **Standing still.** Speed 0.0, pace 00:00, state Stopped (Debug tab).
3. **Steady speed.** Run the belt from its console at 3.0, 5.0 and 8.0 km/h (or the mph equivalents), each for 30 s.
   - Treadmill as the sensor: FitOSC speed is within 0.1 km/h of the console once steady. The driver adds at most
     0.01 km/h of rounding (one raw unit is 0.014 km/h), so a larger gap means the device reports a different value.
   - Footpod: speed depends on the pod's calibration. Record FitOSC next to the console instead of passing or
     failing on the gap, and check pace matches speed (3600 / km/h seconds per km).
4. **Distance.** If the device sets the distance flag, run 0.50 km by the console: FitOSC distance rises by
   0.50 km ± 0.01 km. If distance stays `--`, record that the device omits it (not a failure).
5. **Stopping.** Stop from the console: speed returns to 0 and state to Stopped within 2 s.
6. **No control.** Press START, PAUSE and STOP once each with the belt stopped: nothing happens on the treadmill and
   the log shows no `Wrote` line for them.
7. **Units.** Switch **Settings** > **User** to Imperial: speed shows mph (km/h / 1.609344) and distance miles.
8. **Disconnect and reconnect.** README section 5.

Pass: lines 1-3, 5, 6 and 8 pass and line 4 passes or is recorded as not reported.
