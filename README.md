# DS5Windows

Play **Minecraft Java Edition** with a DualSense (PS5) controller — no mods, no plugins, no Steam Input. Just plug in and play.

DS5Windows maps your DualSense directly to keyboard and mouse inputs, so it works with vanilla Minecraft Java out of the box. No Controllable mod, no third-party input layers — the game sees normal keyboard/mouse input.

Also includes an Xbox 360 emulation mode via ViGEmBus for other games.

## How It Works

Minecraft Java Edition has no native controller support. DS5Windows reads your DualSense controller over USB or Bluetooth and injects keyboard presses and mouse movements directly into Windows. Minecraft sees regular keyboard/mouse input, so it works with any version — no mods or server plugins required.

## Default Controls

### Gameplay

| DualSense | Action |
|---|---|
| Left Stick | WASD (move) |
| Right Stick | Mouse look (camera) |
| Cross (X) | Jump (Space) |
| Circle | Sneak (LShift) |
| Square | Swap hand (F) |
| Triangle | Inventory (E) |
| L1 | Hotbar scroll left |
| R1 | Hotbar scroll right |
| L2 | Place / Use (right click) |
| R2 | Attack / Mine (left click) |
| L3 (toggle) | Sprint (LCtrl) |
| R3 | Unbound |
| D-pad Up | Perspective (F5) |
| D-pad Down | Drop item (Q) — Smart Drop: tap = single, hold = stack |
| D-pad Left | Pick block (middle click) |
| D-pad Right | Unbound |
| Options | Escape |
| Share | Tab (player list) |
| PS | Unbound |
| Touchpad | Chat (T) |

### Menu / Inventory

When a menu or inventory is open, controls switch automatically:

| DualSense | Action |
|---|---|
| Left / Right Stick | Move cursor |
| Cross (X) | Left click (pick up / place item) |
| Square | Right click (split stack) |
| Circle | Shift + left click (quick move) |
| Triangle | Close inventory (E) |
| L1 / R1 | Scroll tabs |
| D-pad Up / Down | Scroll list |

## Features

- **Full button remapping** — configure all 18 buttons via the settings dialog
- **Controllable-style deadzone** — proper rescaled deadzone formula: `sign(x) × (|x| - dz) / (1 - dz)`
- **Separate X/Y sensitivity** — independent yaw and pitch control
- **Configurable trigger deadzone** — adjustable threshold for L2/R2 activation
- **Invert look X/Y** — flip camera axes as needed
- **Smart Drop** — tap D-pad Down to drop one item, hold to drop the full stack (Ctrl+Q)
- **Combo actions** — `combo:ctrl+q` (drop stack) and `combo:ctrl+middle` (creative duplicate) available as bindings
- **Gyroscope aiming** — optional DualSense gyro for fine camera control
- **Haptic rumble** — controller vibration on attack/use triggers
- **Auto menu detection** — switches between gameplay and menu cursor mode using cursor visibility
- **Settings persistence** — bindings saved to `mc-bindings.json`, settings to `mc-settings.json`
- **Dark themed UI** — modern dark interface with lightbar color control
- **Xbox 360 mode** — ViGEmBus emulation for non-Minecraft games

## Requirements

- Windows 10/11
- [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- [ViGEmBus driver](https://github.com/nefarius/ViGEmBus/releases) (only needed for Xbox 360 mode)

## Download

Grab the latest build from [**Releases**](../../releases/latest) — no install needed, just extract and run.

## Build from Source

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o ./publish
```

## Credits

- Original DualSense mapper by [**Zartoz**](https://github.com/Zartoz/DualSenseMapper)
- Minecraft mode and configurable bindings by [**Weeeishy**](https://github.com/Weeeishy)

## License

See [LICENSE](LICENSE) for details.
