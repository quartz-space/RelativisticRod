# Relativistic Rod Deformation

![icon](Assets/AppIcon-512.png)

Native WinUI 3 (.NET 10, Windows 11 ARM64 / x64) port of the interactive
"Apparent Relativistic Rod Deformation" visualization: a swung rod's true, instantaneous
shape versus the time-delayed image an observer actually sees.

## Build & run

Requires the .NET 10 SDK and the Windows App SDK / WinUI tooling (Visual Studio "WinUI application development" workload).

    dotnet build -c Release -p:Platform=ARM64          # or x64
    dotnet publish -c Release -p:Platform=ARM64 -r win-arm64 -o publish

Or open `RelativisticRod.csproj` in Visual Studio, pick **ARM64**, press F5.

## Project layout

| Path | Role |
|---|---|
| `RodModel.cs` | Physics: rod wave model and retarded-time solver |
| `MainWindow.xaml(.cs)` | Fluent UI, Win2D render loop, input, theming |
| `StateStore.cs` | Persists slider state to `%LOCALAPPDATA%\RelativisticRod\state.json` |
| `Package.appxmanifest` | MSIX identity and tiles (used only for MSIX builds) |
| `Assets/` | Generated icons — regenerate with `python tools/generate_icons.py` |

## CI / releases

* `.github/workflows/ci.yml` — builds and publishes ARM64 + x64 on every push / PR to `main`.
* `.github/workflows/release.yml` — on tag `v*` (e.g. `git tag v1.0.0 && git push --tags`) builds
  portable exe zips and MSIX packages for ARM64 and x64 and attaches them to a GitHub release.

Without secrets the MSIX is signed with a throw-away self-signed certificate (the `.cer` is attached to the release).
For a trusted signature add repo secrets `SIGNING_CERT_BASE64` (base64 of your `.pfx`) and `SIGNING_CERT_PASSWORD`.

## License

[MIT](LICENSE)
