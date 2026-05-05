# SonarDataPlayer

SonarDataPlayer is a lightweight Windows desktop application for creating and playing back processed sonar projects. Raw recording conversion is delegated to PINGverter, so the New Project flow can process any format that PINGverter supports and can export into the SonarDataPlayer format.

The app is intentionally a single-process .NET desktop program: no web server, Node.js, or browser frontend is required for playback. A local PINGverter checkout is currently used only when creating a new processed project from raw sonar recordings.

## Current Status

- WPF desktop application shell.
- Core playback and telemetry models.
- Processed project manifest loader.
- In-app raw recording project creation through PINGverter for supported formats.
- Raw sample playback using `frames.jsonl` and `samples.u16le`, with PNG preview/fallback assets.
- Play, pause, seek, speed selection, channel visibility, opacity controls, and telemetry readouts.
- Depth, speed, temperature unit controls, time/depth zoom, auto depth range, and stacked/overlay channel views.
- Python/PINGverter settings dialog with interpreter testing and saved project root selection.

Raw recording parsing is currently delegated to a configured local PINGverter checkout. Porting direct parsers into the application remains a future cleanup step.

## Repository Layout

```text
SonarDataPlayer.App/      WPF desktop application
SonarDataPlayer.Core/     Recording, channel, telemetry, playback, and project loading code
docs/                     Project format and development notes
```

## Prerequisites

- Windows 10 or later.
- .NET 8 SDK or newer.
- Python 3 with `numpy`, `pandas`, and `pillow` if you want to create projects from raw recordings inside the app.
- A local PINGverter checkout with `export_sonar_data_player_project(...)` support.

Current New Project input extensions are:

- `.dat` (Humminbird)
- `.rsd` (Garmin)
- `.sl2` and `.sl3` (Lowrance)
- `.svlog` (Cerulean)
- `.jsf` (EdgeTech)
- `.xtf` (XTF)

Check your SDK:

```powershell
dotnet --info
```

## Build

From the repository root:

```powershell
dotnet restore .\SonarDataPlayer.App\SonarDataPlayer.App.csproj
dotnet build .\SonarDataPlayer.App\SonarDataPlayer.App.csproj -c Release
```

## Run

```powershell
dotnet run --project .\SonarDataPlayer.App\SonarDataPlayer.App.csproj
```

Use **Open Project** and select a processed `manifest.json`.

See [docs/processed-project-format.md](docs/processed-project-format.md) for the expected folder layout.

## Settings

Use **Python...** to configure project creation:

- View the `SONAR_DATA_PLAYER_PYTHON` environment variable.
- Set and save a Python executable path.
- Choose whether Python resolution prefers the environment variable or saved path.
- Test the selected Python interpreter and dependency imports.
- Set the local PINGverter repository root.

Settings are saved under:

```text
%AppData%\SonarDataPlayer\settings.json
```

## Create A Project From A Recording

In the app:

1. Use **Python...** to select a `python.exe` that can import `numpy`, `pandas`, and `PIL`.
2. Set the local PINGverter repository root in **Python...**.
3. Use **New Project**.
4. Select a supported raw sonar file (`.dat`, `.rsd`, `.sl2`, `.sl3`, `.svlog`, `.jsf`, or `.xtf`).
5. Choose the **Projects root** folder where generated projects should be stored.
6. Click **Process Recording**.

The dialog generates the actual project folder from the recording file name, for example:

```text
<Projects root>\8A cobia
```

If **Open project when complete** is checked, the app loads the generated `manifest.json` automatically.

Python selection priority is:

1. `SONAR_DATA_PLAYER_PYTHON` environment variable.
2. Saved app setting from **Python...**.
3. Local/bundled Python candidates.
4. System `python` or `py`.

Install the parser dependencies into your chosen Python environment:

```powershell
python -m pip install numpy pandas pillow
```

The processing dialog streams PINGverter output so parse/export errors are visible without leaving the app.

## Package A Portable Windows Build

```powershell
dotnet publish .\SonarDataPlayer.App\SonarDataPlayer.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o .\publish\win-x64
```

The portable executable will be written to `publish\win-x64`.

## Processed Recording Workflow

For each generated project, PINGverter writes:

- `manifest.json`
- `pings.csv`
- `frames.jsonl`
- `samples.u16le`
- `channels\*_channel_*.png`

The project creation workflow is:

1. Parse the raw sonar file.
2. Export ping metadata to CSV.
3. Extract raw samples grouped by channel or beam.
4. Group channel records into synchronized ping frames.
5. Write `uint16-le` channel arrays into `samples.u16le`.
6. Write frame metadata and sample offsets into `frames.jsonl`.
7. Render optional preview waterfall PNGs.
8. Write a `manifest.json` that points to the CSV, frame index, sample blob, and PNGs.
9. Open the manifest in SonarDataPlayer.

This workflow keeps playback deployment simple while raw format parsing remains in PINGverter.

When `frames.jsonl` and `samples.u16le` are present, the app renders from raw samples with one shared intensity scale. PNGs are kept as preview/fallback assets.

## Planned Next Steps

- Port raw sonar parsing into `SonarDataPlayer.Core`.
- Add direct rendering controls for gain, contrast, palettes, and side-scan handling.
- Improve packaged PINGverter deployment or remove the Python dependency entirely.
