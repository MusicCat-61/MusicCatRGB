# KeyGlowMaster

![KeyGlowMaster](assets/preview-en.png)

**English** · [Русский](README.ru.md)

A small, fast replacement for the stock Control Center on Clevo / Monster Notebook laptops. Written in C# on .NET 10 + WPF, it talks to the keyboard through the Insyde DCHU or WMI interfaces — no drivers of its own, no background services, no installer.

KeyGlowMaster is a fork of the [GutsV project](https://github.com/gutsv-project/Clevo-Monster-RGB-Controller): the interface was rebuilt from scratch, localisation, themes, new effects and small quality-of-life features were added. See [Credits](#credits--license).

## What is inside

| | |
|---|---|
| 🪟 **Mica window** | Native Windows 11 backdrop, no system frame, custom title bar |
| 🌗 **Frost themes** | Light and dark palettes, switched on the fly from the title bar |
| 🌐 **Two languages** | English and Russian, switchable without a restart, follows the system language on first run |
| 🎨 **15 effects** | From calm breathing to a chaotic Soviet garland — list below |
| 🎯 **Colour slots** | Three colours for animations plus a separate applied colour, each with its own preview |
| ⌨️ **Live preview** | The keyboard on screen shows the colour that is actually being sent to the hardware |
| 😴 **Sleep timer** | The backlight fades out after 5 minutes of idle time and comes back on input |
| ♻️ **Portable** | Single folder, runs as administrator, auto-start through Task Scheduler |

## Effects

| Effect | What it does |
|---|---|
| Breathing | Smooth single-colour fade in and out |
| Neon pulse | Short bright flashes of a neon colour |
| Two-Color Fade | Sine fade between the two picked colours |
| Spectrum Cycle | Continuous walk around the full hue circle |
| Strobe blink | Sharp on/off blinking |
| Wave | Travelling colour wave across the three zones |
| Zone Gradient | Three zones, three picked colours, the palette scrolls across the keyboard |
| Random Neon | A new neon shade every 2–5 seconds |
| Day / Night | Warm in the morning, white by day, deep blue at night — follows the clock |
| Soviet garland | Chaotic warm flashes, dim-outs and burn-outs of old filament bulbs |
| Audio Sync | Lighting reacts to the music playing on the system |
| System Monitor | Colour driven by CPU load and ACPI temperature |
| Ambient (Screen Sync) | Colour sampled from the screen edges |
| Weather Sync | Live weather colours (rain — blue, sun — yellow, clouds — grey, snow — white) |
| Dual Breathe | Two layered sine waves with different periods for a complex rhythm |

## How the colour model works

There are **four** colours in the app, and they are not the same thing:

- **Colour** — the applied colour. Press *Apply colour* and the keyboard switches to it; it is what the preview shows when no effect is running.
- **Primary / Second colour / Third colour** — the palette used by effects. Click a swatch and the RGB sliders start editing that slot; the applied colour stays untouched.

The third swatch only appears for *Zone Gradient*, the only effect that needs three zones. Values are stored in `%LOCALAPPDATA%\MusicCat.CLVKM\settings.json`.

## Zones

Zone count is detected automatically. On a single-zone keyboard the zone-based effects (*Wave*, *Zone Gradient*) fall back to one colour — the app warns about it in the device panel.

## Sleep timer

Tick **Turn off lighting when idle (5 min)** in the device card. After five minutes without mouse or keyboard input the backlight fades to black in about half a second, the animation freezes and the brightness slider shows `0%`. Any input brings the previous state back with a short fade.

## Installation

1. Download the latest release.
2. Unpack the folder anywhere (for example `C:\KeyGlowMaster`).
3. Run **`KGMaster.exe`** as administrator — the app needs it to talk to the keyboard driver.
4. On the first run accept **Service Mode** to create a scheduled task, so Windows starts the app with the saved lighting.

> The original Control Center (`FnKey.exe`) should be running: KeyGlowMaster relies on the drivers it initialises.

## Languages and themes

Both switches live in the title bar: `EN | RU` for the interface language and the sun/moon button for the theme. The choice is remembered. The language defaults to the system one on first launch.

## Building from source

Requires the .NET 10 SDK and Windows 10/11.

```powershell
dotnet build MusicCat.CLVKM.slnx -c Release
```

or run `publish.bat` to get a self-contained folder in `Build_Output`.

Layout: `MusicCat.CLVKM.Core` (models, settings, animation engine, weather), `MusicCat.CLVKM.Drivers` (Insyde / WMI), `MusicCat.CLVKM.UI` (WPF shell), plus a small service and a diagnostics console.

## Troubleshooting

- **Device not found** — start the original Control Center first, then restart KeyGlowMaster. In the driver box you can also force *Insyde* or *WMI* instead of auto-detect.
- **Auto-start does nothing** — open the app as administrator and accept *Service Mode* again; the old scheduled task may have been removed by a cleanup.
- **Weather stays grey** — the effect needs internet access. It tries Open-Meteo first and falls back to `api.met.no`; if both are unreachable the lighting stays neutral until the next attempt in a couple of minutes.
- **Light goes off by itself** — uncheck the sleep timer.

## Credits & license

KeyGlowMaster is based on **GutsV Colour** by [acerhizm](https://github.com/gutsv-project/Clevo-Monster-RGB-Controller) — the drivers, the animation approach and the original idea are theirs. This fork was made by **MusicCat161** and adds a redesigned interface, Frost themes, localisation, new effects and the sleep timer.

Distributed under the MIT License, see [LICENSE](LICENSE).
