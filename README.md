# MeasureTools [![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

Click-to-measure ruler and protractor tools for [Kitten Space Agency](https://ahwoo.com/app/100000/kitten-space-agency).

This mod is written against the [StarMap loader](https://github.com/StarMapLoader/StarMap).

## Features

- **Ruler** - click two points in map view to measure the straight-line distance. Snaps to
  bodies and to points on orbit lines, or places free points on a reference plane.
- **Protractor** - click three points (arm, apex, arm) to read the true 3D angle, e.g. the
  phase angle between two planets around their star.
- **Surface measuring** - great-circle distance, chord, and bearing between two points
  on a planet surface.
- **Editor measuring** - distances between points on parts in the vehicle editor.

## Installation

1. Install [StarMap](https://github.com/StarMapLoader/StarMap).
2. Download the latest release from the [GitHub Releases](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/releases) tab or from [SpaceDock](https://spacedock.info/mod/XXXX/MeasureTools).
3. Extract into `Documents\My Games\Kitten Space Agency\mods\MeasureTools\`.
4. The game auto-discovers new mods and prompts you to enable them. Alternatively, add to `Documents\My Games\Kitten Space Agency\manifest.toml`:

```toml
[[mods]]
id = "MeasureTools"
enabled = true
```

## Dependencies

| Package | Purpose | Tested version |
| --- | --- | --- |
| [StarMap](https://github.com/StarMapLoader/StarMap) | Mod loader, required at runtime (see [Installation](#installation)) | 0.4.5 |

## Build dependencies

Required only to build the mod from source. Targets **.NET 10**.

| Package | Source | Tested Version |
| --- | --- | --- |
| [StarMap.API](https://github.com/StarMapLoader/StarMap) | NuGet | 0.3.6 |
| [Lib.Harmony](https://www.nuget.org/packages/Lib.Harmony) | NuGet | 2.4.2 |

## Mod compatibility

- Known conflicts: none

## Community

Thread on the KSA forums: TBD

## Check out my other mods

- [AdvancedFlightComputer](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer) - Transfer Planner quick-tools (set Pe/Ap, match/set inclination, circularize), multi-pass burn splitting, and hyperbolic-target support (Oumuamua, 2I/Borisov, 3I/ATLAS) ([forum thread](https://forums.ahwoo.com/threads/advanced-flight-computer.783/))
- [AutoRemoveFinishedBurns](https://github.com/Maximilian-Nesslauer/KSA-AutoRemoveFinishedBurns) - automatically removes finished auto-burns from the burn plan ([forum thread](https://forums.ahwoo.com/threads/autoremovefinishedburns.928/))
- [AutoStage](https://github.com/Maximilian-Nesslauer/KSA-AutoStage) - automatic staging during auto-burns and manual flight, with configurable ignition delays ([forum thread](https://forums.ahwoo.com/threads/autostage.891/))
- [DeltaVMap](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap) - interactive delta-v subway map and transfer-window planner, auto-generated from the loaded system ([forum thread](https://forums.ahwoo.com/threads/deltavmap.978/))
- [StageInfo](https://github.com/Maximilian-Nesslauer/KSA-StageInfo) - extra info in the stock Stage/Sequence window: per-stage delta V, TWR, burn time, fuel pool, RCS, and more ([forum thread](https://forums.ahwoo.com/threads/stageinfo.905/))
