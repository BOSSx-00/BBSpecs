# Building BBSpecs

Everything in here is for working on BBSpecs. If you just want to run it, the
[README](README.md) has the downloads.

You need the [.NET 9 SDK](https://dotnet.microsoft.com/download). Nothing else.

```bash
git clone https://github.com/BOSSx-00/BBSpecs.git
cd BBSpecs
```

## The builds

**Windows**, producing `dist\BBSpecs-<version>-win-x64.exe`:

```powershell
.\build\build-windows.ps1
```

To build the installer as well, producing `dist\BBSpecs-Setup-<version>.exe`.
This one needs [Inno Setup 6](https://jrsoftware.org/isinfo.php), which
`winget install JRSoftware.InnoSetup` will fetch:

```powershell
.\build\build-installer.ps1
```

**Linux**, producing `dist/BBSpecs-<version>-linux-x64` plus the install script:

```bash
./build/build-linux.sh
```

Both produce a self-contained single file: the .NET runtime is bundled, so
whoever downloads it needs nothing installed.

For a quick development build, `dotnet run --project src/BBSpecs` works too.

## Checking the other platform compiles

The target framework follows the machine you are building on, but you can
compile-check the other side from either host:

```bash
dotnet build src/BBSpecs -p:BBSpecsTarget=net9.0           # the Linux code, from Windows
dotnet build src/BBSpecs -p:BBSpecsTarget=net9.0-windows   # the Windows code, from Linux
```

## Iterating on the interface

On Windows the Administrator manifest means a UAC prompt on every launch, which
gets old fast while working on the front-end. `-p:Elevate=false` leaves it out:

```powershell
dotnet build src\BBSpecs -p:Elevate=false -p:PublishSingleFile=false -p:SelfContained=false
```

The app then runs unelevated and shows its own "some readings are unavailable"
banner, which is exactly what somebody without Administrator rights sees, so it
is worth testing anyway. The build scripts never pass the flag, so releases
always ship with it.

## Seeing what BBSpecs reads on a given machine

`tools/SnapshotDump` prints one full snapshot to the console and writes the
exact JSON the interface receives. It is the fastest way to work out why a
reading looks wrong on hardware you do not have in front of you, and on Linux it
also lists which optional tools are present.

```bash
dotnet run --project tools/SnapshotDump
sudo dotnet run --project tools/SnapshotDump     # Linux, to include the root-only bits
```

To find out why one particular reading is blank, list every raw sensor the
machine offers. If a value is not in that list, the hardware or the driver is
not providing it:

```bash
dotnet run --project tools/SnapshotDump -- --sensors
```

## How it is put together

```
src/BBSpecs/
  Program.cs              Window host, collector thread, host/page messaging
  Models/Snapshot.cs      The shape of everything the interface receives
  Services/               Shared: verdict engine, network, volumes, naming, JSON
  Platform/Windows/       WMI, LibreHardwareMonitor, the native Wi-Fi API
  Platform/Linux/         /proc, /sys, hwmon, and the optional tools
  web/                    The interface: HTML, CSS and one JavaScript file
```

It is a native window hosting a webview, the same approach VS Code, Discord and
Spotify use. The C# side collects a full snapshot once a second on a background
thread and hands it to the page as JSON; the page patches the DOM in place so
scroll position and animations survive every update.

Only the `Platform/` folder matching the build target is compiled, so neither
build carries code it could never run.

## Adding a reading

1. Add the field to `Models/Snapshot.cs`.
2. Fill it in both `Platform/Windows/WindowsHardware.cs` and
   `Platform/Linux/LinuxHardware.cs`. Leaving it null on one platform is fine,
   the interface renders a dash.
3. Render it in `web/app.js`.
4. Bump `<Version>` in `src/BBSpecs/BBSpecs.csproj` and add a line to
   `CHANGELOG.md`.

The version in the window title comes from that one `<Version>` property.
Nothing else needs editing.

## Releasing

`.github/workflows/release.yml` runs on a tag matching `v*`. It builds both
platforms, compiles the installer, and publishes a GitHub release with every
file attached.

```bash
git tag -a v1.2.3 -m "BBSpecs 1.2.3"
git push origin v1.2.3
```

The tag follows `<Version>` in the `.csproj`, which stays the one place a
version number lives.

## Signing

Releases are unsigned right now, so Windows shows **Publisher: Unknown** on the
elevation prompt and SmartScreen warns about an unrecognised app. No amount of
metadata changes that: Windows reads the Authenticode signature, not the version
resource.

I have BBSpecs set up for [SignPath Foundation](https://signpath.org), which
signs open-source projects for free. Signing runs in CI rather than locally, and
that is the point: the certificate never touches my machine, and SignPath
verifies the artifact came from this workflow on this repository. The step is in
the release workflow, commented out, waiting on approval.

One thing to fix when I enable it: the step has to run *after* the upload it
references, and the signed result needs uploading again so the release job
publishes that rather than the original.

If you hold a certificate yourself, the build will sign locally too:

```powershell
.\build\build-windows.ps1 -SignThumbprint <certificate thumbprint>
```

or set `BBSPECS_SIGN_THUMBPRINT` once on the build machine. It needs
`signtool.exe` from the Windows SDK. Without a thumbprint the build still works
and tells you it is shipping unsigned.

A self-signed certificate does not help: the prompt still reads "Unknown" on
every machine that does not already trust it, which is every machine but yours.
