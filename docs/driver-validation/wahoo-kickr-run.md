# Wahoo KICKR RUN driver checklist

Treadmill Type: **Wahoo KICKR RUN (Experimental)**. Shared steps (build, connecting, evidence, reconnect) are in
[README.md](README.md).

Status: not yet tested on hardware.

## What the driver does

The generic FTMS driver plus Wahoo's own incline command, taken from Zwift's
`WahooTreadmillControlComponent::UpdateIncline` (`libzwiftjni.so`). FTMS fields follow the Bluetooth SIG
definitions: [Treadmill Data](https://bitbucket.org/bluetooth-SIG/public/src/main/gss/org.bluetooth.characteristic.treadmill_data.yaml),
[Supported Speed Range](https://bitbucket.org/bluetooth-SIG/public/src/main/gss/org.bluetooth.characteristic.supported_speed_range.yaml),
[Supported Inclination Range](https://bitbucket.org/bluetooth-SIG/public/src/main/gss/org.bluetooth.characteristic.supported_inclination_range.yaml).

On connect it reads the supported speed and incline ranges (logged as `Supported Speed Range` and
`Supported Incline Range`), requests FTMS control (`Wrote 00` to `00002ad9-…`; a refusal fails the connection) and
looks for the Wahoo control point `a026e03e-0a7d-4ab3-97fa-f1500f9feb8b`.

| Control | Bytes written |
|---|---|
| START | `07` to the FTMS control point |
| PAUSE | `08-02` |
| STOP | `08-01` |
| Speed | `02` + uint16 little-endian in 0.01 km/h, clamped to the reported range: 4.00 km/h is `02-90-01` |
| Incline, Wahoo point present | `11` + sint16 little-endian in 0.01 %: 2 % is `11-C8-00`, -1 % is `11-9C-FF` |
| Incline, Wahoo point missing | FTMS `03` + sint16 in 0.1 %: 2 % is `03-14-00` (log: `Wahoo treadmill control point not found`) |

Telemetry is whatever the treadmill flags in Treadmill Data: speed (0.01 km/h), distance (m), incline (0.1 %),
pace (s per 500 m, shown per km or mile), energy (kcal), heart rate, elapsed time and the others.

Shared FitOSC limits that affect this driver (README): the speed **+** / **-** controls only work if the treadmill
reported a speed range; incline **-** stops at 0 %; an incline the treadmill reports as unavailable (0x7FFF) shows as
3276.7 %.

## Checklist

Stand beside the belt for every first command. Use low speeds (up to 5 km/h) and small inclines (0-3 %).

1. **Connect.** Connected within 30 s. The log has `Supported Speed Range` and `Supported Incline Range` lines with
   the console's limits, and `Wrote 00` to `00002ad9-…` with no `rejected control point opcode 00`. Record whether the Wahoo control
   point was found.
2. **Readings.** Run 3.0 and 5.0 km/h from the console for 30 s each. FitOSC speed is within 0.1 km/h of the
   console, pace equals 3600 / speed seconds per km within 1 s, distance rises with the console within 0.01 km, and
   incline matches the console.
3. **Start.** Belt stopped: press START. The log shows `Wrote 07` and the treadmill starts (or begins its
   countdown).
4. **Speed.** In the console run `FitOSC.setSpeed(4.0)`: the log shows `Wrote 02-90-01` and the belt settles at
   4.0 km/h within 0.1 km/h. Then press speed **+** once (only if line 1 logged a speed range): the belt rises about
   0.32 km/h.
5. **Incline.** `FitOSC.setIncline(2)`: the log shows `Wrote 11-C8-00` to `a026e03e-…` (or `03-14-00` to `00002ad9-…`
   without the Wahoo point) and the treadmill reaches 2 %. `FitOSC.setIncline(0)` returns it to 0 %. A `rejected
   opcode` warning in the log is a fail.
6. **Pause and stop.** PAUSE: `Wrote 08-02`, the belt stops and the treadmill shows paused; START resumes it. STOP:
   `Wrote 08-01`, the belt stops and the session ends.
7. **Units.** Imperial shows mph = km/h / 1.609344 and miles.
8. **Disconnect and reconnect.** README section 5; after reconnecting, `Wrote 00` appears again and line 5 still
   works.

Pass: every line passes. If the Wahoo control point is missing, line 5 is tested through FTMS and recorded as such.
