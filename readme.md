
<p align="center">
  <img src="FitOSC.png" alt="Centered Image" >
</p>

FitOSC is a Bluetooth Low Energy (BLE)-enabled treadmill interface that connects your treadmill to your VRChat avatar, allowing you to control in-game actions while exercising. This application synchronizes treadmill movements with avatar parameters, creating an immersive workout experience in VRChat.

## Features

- **FTMS Connectivity**: Connects to your treadmill via Bluetooth Low Energy (BLE)
- **Avatar Integration**: Receives treadmill commands from your avatar
- **OSC Query Support**: Automatically detects and connects to VRChat via OSC.
- **SteamVR Integration**: Automatically keeps your avatar moving in the same when the treadmill is in use.

## How It Works

1. **BLE Communication**: FitOSC connects to the treadmill using BLE, capturing movement data like speed and status.
2. **OSC Transmission**: Using Open Sound Control (OSC), FitOSC sends treadmill data to VRChat, updating your avatar's movement and actions in real-time.
3. **Avatar Interaction**: Your avatar mirrors the treadmill's current state (running, walking, paused) and responds to your treadmill commands.

## Demo Application

A demo application showcasing FitOSC in action is available at [fitosc.duinrahaic.app](https://fitosc.duinrahaic.app). Note that the web demo does **not** include OSC functionality. It is purely a demonstration of how the interface works with BLE-connected treadmills.

## Getting Started

### Prerequisites

- A treadmill with Bluetooth and FTMS support.
- VRChat with OSC parameters enabled.
- Windows or macOS operating system.
- A compatible BLE adapter (if not built-in). [Recommended](https://www.amazon.com/TP-Link-Bluetooth-Receiver-Controller-UB500/dp/B09DMP6T22)

### Installation

1. **Download FitOSC** from the releases section.
2. **Pair your treadmill** to the system via BLE.
3. **Launch FitOSC** and configure BLE and VRChat integration.
4. **Enable OSC in VRChat** from game settings.

### Usage

1. **Launch FitOSC**: Ensure your treadmill is paired via BLE.
2. **Start VRChat**: FitOSC will automatically sync treadmill inputs with your avatar.
3. Use the treadmill to control in-game actions such as walking, running, pausing, etc.

## Troubleshooting

1. Ensure BLE is enabled and the treadmill is properly paired.
2. Check that OSC is enabled in VRChat.
3. View FitOSC log files for detailed troubleshooting information.

## Exposed VRChat Avatar Parameters

Below are the exposed VRChat avatar parameters that can be controlled using FitOSC. These parameters are can be setup in an unity as a VRChat menu, but if you'd like an easy installation for a menu, consider buying my prefab off of [booth](https://duinrahaic.booth.pm/) or support me on [patreon](wwww.pateron.com/duinrahaic).

| **Command**        | **Type**  | **Control Type**  | **Description**                                                                                   |
|--------------------|-----------|-------------------|---------------------------------------------------------------------------------------------------|
| **TMC_SpeedUp**     | `bool`    | Button            | Increases the treadmill speed when the argument is `true`.                                        |
| **TMC_SlowDown**    | `bool`    | Button            | Decreases the treadmill speed when the argument is `true`.                                        |
| **TMC_Stop**        | `bool`    | Button            | Stops the treadmill when the argument is `true`.                                                  |
| **TMC_Start**       | `bool`    | Button            | Starts the treadmill when the argument is `true`.                                                 |
| **TMC_Pause**       | `bool`    | Button            | Pauses the treadmill when the argument is `true`.                                                 |
| **TMC_Reset**       | `bool`    | Button            | Sends a reset command to the treadmill when the argument is `true`.                               |
| **TMC_Walk**        | `bool`    | Button            | Automatically triggers your avatar to walk when `true`, syncing the treadmill walking state with the avatar. |
| **TMC_WalkingTrim** | `float`   | Radial Puppet     | Finitely adjusts the walking speed of the avatar. (Default: 0.8)                                  |

## Support

For additional support, please consider joining my discord server [here](https://discord.gg/aZQfy6H9fA).


## License

FitOSC is licensed under the MIT License. Modify and distribute as needed.

---

### Take your fitness into the virtual world with FitOSC!
