# FitOSC

FitOSC is a Bluetooth Low Energy (BLE)-enabled treadmill interface that connects your treadmill to your VRChat avatar, allowing you to control in-game actions while exercising. This application synchronizes treadmill movements with avatar parameters, creating an immersive workout experience in VRChat.

## Features

- **BLE Connectivity**: FitOSC connects to your treadmill using BLE, ensuring a seamless, wireless experience.
- **Real-Time VRChat Integration**: Control your VRChat avatar using treadmill actions, like speeding up, slowing down, or stopping.
- **Customizable Avatar Parameters**: Exposes treadmill controls to VRChat's avatar system for in-game interaction.

## Exposed VRChat Avatar Parameters

- **TMC_SpeedUp**: Increases the treadmill speed when the argument is `true`.
- **TMC_SlowDown**: Decreases the treadmill speed when the argument is `true`.
- **TMC_Stop**: Stops the treadmill when the argument is `true`.
- **TMC_Start**: Starts the treadmill when the argument is `true`.
- **TMC_Pause**: Pauses the treadmill when the argument is `true`.
- **TMC_Reset**: Sends a reset command to the treadmill when the argument is `true`.
- **TMC_Walk**: Automatically triggers your avatar to walk when `true`, syncing the treadmill walking state with the avatar.

## How It Works

1. **BLE Communication**: FitOSC connects to the treadmill using BLE, capturing movement data like speed and status.
2. **OSC Transmission**: Using Open Sound Control (OSC), FitOSC sends treadmill data to VRChat, updating your avatar's movement and actions in real-time.
3. **Avatar Interaction**: Your avatar mirrors the treadmill's current state (running, walking, paused) and responds to your treadmill commands.

## Demo Application

A demo application showcasing FitOSC in action is available at [fitosc.duinrahaic.app](https://fitosc.duinrahaic.app). Note that the web demo does **not** include OSC functionality. It is purely a demonstration of how the interface works with BLE-connected treadmills.

## Getting Started

### Prerequisites

- A treadmill with BLE support.
- VRChat with OSC parameters enabled.
- Windows or macOS operating system.

### Installation

1. **Download FitOSC** from the releases section.
2. **Pair your treadmill** to the system via BLE.
3. **Launch FitOSC** and configure BLE and VRChat integration.
4. **Enable OSC in VRChat** from game settings.

### Usage

1. **Launch FitOSC**: Ensure your treadmill is paired via BLE.
2. **Start VRChat**: FitOSC will automatically sync treadmill inputs with your avatar.
3. Use the treadmill to control in-game actions such as walking, running, pausing, etc.

### Treadmill Controls in VRChat

- **TMC_SpeedUp**: Trigger this to increase the treadmill speed and your avatar’s running speed.
- **TMC_SlowDown**: Decrease the treadmill and avatar speed.
- **TMC_Stop**: Stop the treadmill and the avatar stops moving.
- **TMC_Start**: Start the treadmill to make the avatar run.
- **TMC_Pause**: Pause the treadmill and the avatar will stop.
- **TMC_Reset**: Reset the treadmill.
- **TMC_Walk**: Automatically make your avatar walk when toggled.

## Troubleshooting

1. Ensure BLE is enabled and the treadmill is properly paired.
2. Check that OSC is enabled in VRChat.
3. View FitOSC log files for detailed troubleshooting information.

## License

FitOSC is licensed under the MIT License. Modify and distribute as needed.

---

Take your fitness into the virtual world with FitOSC!
