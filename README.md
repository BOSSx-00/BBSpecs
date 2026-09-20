<p align="center">
  <img src="img/bbspecs_logo.png" width="128" alt="BBSpecs">
</p>

<h1 align="center">BBSpecs</h1>

<p align="center">
  A friendly hardware monitor for people who just want to know what's in their PC,
  and whether it's doing alright.
</p>

---

BBSpecs is a desktop app. You download one file, double-click it, and it tells you
what your computer is made of in plain English: what your processor is, how warm it's
running, how much video memory your graphics card has, how full your drives are, and
what your internet connection actually looks like.

It's the Performance tab of Task Manager with more detail, minus the parts that
assume you already know what a "P-state" is. Every reading comes with a plain-language
verdict: **Amazing**, **Good**, **Decent**, **Upgrade Soon** or **Outdated**, and a
sentence saying why.

Two buttons on the Overview do the things people actually want afterwards:

- **Copy Specs** puts a short summary on your clipboard (processor, graphics,
  motherboard, memory, every drive, and your network hardware), ready to paste
  wherever you're asking for help. No serial numbers, no addresses.
- **Upgrade ideas** names the cheapest part that would actually make a difference to
  *your* machine, in the order worth buying. It reads the real state of things, so it
  catches problems a spec sheet won't, like mismatched memory sticks quietly dragging
  a whole set down to the slowest one's speed.

**Windows and Linux.** One codebase, a native app on both.

---

## Download and run

### Windows

Two downloads, same program. Pick whichever suits you.

**Installed.** `BBSpecs-Setup-<version>.exe` runs a wizard: choose a folder, get a
Start Menu entry, optionally a desktop shortcut and a "start with Windows" option,
and an uninstaller in Add or Remove Programs.

**Portable.** `BBSpecs-<version>-win-x64.exe` is one file that runs from wherever you
put it and installs nothing.

Either way, double-click it and accept the Windows prompt. That prompt is Windows
asking whether to let BBSpecs run as Administrator, which it needs because Windows
only lets elevated programs read temperature sensors.

BBSpecs keeps itself up to date whichever you chose: the portable build replaces its
own file, and an installed one fetches the new installer so the entry in Add or
Remove Programs keeps saying the truth.

Processor temperature needs a kernel driver. Windows offers no other way, which is
why Task Manager doesn't show it either. BBSpecs carries the official
[PawnIO](https://pawnio.eu) installer inside its own executable and offers to run it
with one click the first time it's needed. Nothing is installed without asking, and
everything except processor and motherboard temperature works without it. See
`src/BBSpecs/Assets/PawnIO/PawnIO-NOTICE.txt` for its licence, source and checksum.

The only component BBSpecs can't carry inside itself is the **Microsoft Edge WebView2
Runtime**, which it draws its display into. That ships with Windows 11 and with
Microsoft Edge on Windows 10, so virtually every PC already has it. On the rare one
that doesn't, BBSpecs says so on launch and offers to open the download page rather
than closing without a word.

### Linux

1. Download `BBSpecs-<version>-linux-x64`, `install.sh`, `bbspecs.desktop` and `bbspecs.png`
   into the same folder.
2. Run `./install.sh`.

Or skip the installer and just run the binary directly:

```bash
chmod +x BBSpecs-*-linux-x64
./BBSpecs-*-linux-x64
```

BBSpecs needs a system webview, which most desktop installs already have. If it's
missing, BBSpecs says so on launch and prints the command for your distribution
rather than failing silently:

| Distribution  | Install                                       |
| ------------- | --------------------------------------------- |
| Debian/Ubuntu | `sudo apt install libwebkit2gtk-4.1-0`        |
| Fedora        | `sudo dnf install webkit2gtk4.1`              |
| Arch          | `sudo pacman -S webkit2gtk-4.1`               |
| openSUSE      | `sudo zypper install libwebkit2gtk-4_1-0`     |

---

## Why it asks for Administrator / root

Temperature sensors sit behind a privilege wall on both platforms. Without elevation
BBSpecs still runs and still shows your specs. You just get blanks where the
temperatures should be, and a banner explaining it with a button to restart properly.

|                             | Normal user | Administrator / root |
| --------------------------- | ----------- | -------------------- |
| Model names, cores, RAM size | yes         | yes                  |
| Drive sizes and free space   | yes         | yes                  |
| Network and Wi-Fi details    | yes         | yes                  |
| CPU / GPU / drive temperature | Linux only¹ | yes                  |
| Fan speeds                   | Linux only¹ | yes                  |
| Memory slot layout           | no          | yes                  |
| Drive health and lifetime writes | no      | yes                  |

¹ On Linux the kernel publishes temperatures through `hwmon`, which any user can read.
Windows keeps them behind the sensor driver, which requires elevation.

---

## Optional extras on Linux

BBSpecs reads nearly everything straight from `/proc` and `/sys`, which need nothing
installed. A few readings come from standard tools, and each one is optional. If it
isn't there, that row shows a dash instead.

| Tool         | Adds                                          | Usually in package |
| ------------ | --------------------------------------------- | ------------------ |
| `lspci`      | Graphics card model name                      | `pciutils`         |
| `nvidia-smi` | NVIDIA temperature, VRAM use, driver version   | NVIDIA driver      |
| `dmidecode`  | Memory slot layout (root)                      | `dmidecode`        |
| `smartctl`   | Drive health and lifetime writes (root)        | `smartmontools`    |
| `nmcli`      | Wi-Fi network name and security                | `network-manager`  |
| `iw`         | Wi-Fi signal, link rates, 802.11 generation    | `iw`               |
| `lsblk`      | Drive serial numbers                           | `util-linux`       |

If temperatures are missing entirely, the sensor modules probably aren't loaded:

```bash
sudo modprobe coretemp      # Intel
sudo modprobe k10temp       # AMD
sudo modprobe drivetemp     # SATA drive temperatures
```

Installing `lm-sensors` and running `sudo sensors-detect` sets this up permanently.

---

## Privacy

Nothing about your machine is uploaded anywhere, and there is no telemetry, no
analytics and no account. Everything BBSpecs reads is read, shown and forgotten.

Four things touch the network, all of them deliberate:

- A ping to `1.1.1.1` every five seconds, purely to tell you whether you're online.
- A check with GitHub for a newer version, once at startup and whenever you ask.
- The **public address** lookup on the Internet tab, which only runs when you click
  the button. It asks a public lookup service (`ipwho.is`, falling back to
  `ifconfig.co`, `ipinfo.io` or `ipify.org`) what address the internet sees.
- Links you choose to open, which go to your normal browser.

None of them carry anything about your hardware. Blocking BBSpecs in a firewall
leaves every reading about your own machine working.

There's also a **Privacy** toggle in the menu that blanks out serial numbers,
IP addresses, MAC addresses and your Wi-Fi network name. Worth a click before you
screenshot anything.

The full policy, including where settings and crash logs are written, is in
[PRIVACY.md](PRIVACY.md).

---

## Building from source

You need the [.NET 9 SDK](https://dotnet.microsoft.com/download). Nothing else.

```bash
git clone https://github.com/<owner>/BBSpecs.git
cd BBSpecs
```

**Windows**, producing `dist\BBSpecs-<version>-win-x64.exe`:

```powershell
.\build\build-windows.ps1
```

To build the installer as well, producing `dist\BBSpecs-Setup-<version>.exe`.
This needs [Inno Setup 6](https://jrsoftware.org/isinfo.php), which
`winget install JRSoftware.InnoSetup` will fetch:

```powershell
.\build\build-installer.ps1
```

**Linux**, producing `dist/BBSpecs-<version>-linux-x64` plus the installer:

```bash
./build/build-linux.sh
```

Both produce a self-contained single file: the .NET runtime is bundled, so whoever
downloads it needs nothing installed.

For a quick development build, `dotnet run --project src/BBSpecs` works too.

### Checking the other platform compiles

The target framework follows the machine you're building on, but you can compile-check
the other side from either host:

```bash
dotnet build src/BBSpecs -p:BBSpecsTarget=net9.0           # the Linux code, from Windows
dotnet build src/BBSpecs -p:BBSpecsTarget=net9.0-windows   # the Windows code, from Linux
```

### Signing the release

Windows shows **Publisher: Unknown** on the Administrator prompt for any unsigned
program, and no amount of metadata changes that. It reads the Authenticode
signature, not the version resource. To show *BOSSx* there you need a code-signing
certificate issued to BOSSx. The same signature is what eventually clears the
SmartScreen "unrecognised app" warning, which matters more for a download.

BBSpecs is set up for [SignPath Foundation](https://signpath.org), which signs
open-source projects for free. Their conditions, and where BBSpecs stands:

| Condition | Status |
| --------- | ------ |
| OSI-approved licence, no commercial dual-licensing | MIT, see `LICENSE` |
| Publicly available codebase | needs the repository to be public |
| Actively maintained | yes |
| Already released in the form to be signed | needs one public GitHub release |
| Functionality described on the download page | this README |
| No proprietary, non-open-source component | see the note below |

The last one deserves attention: BBSpecs bundles the prebuilt PawnIO installer.
PawnIO is GPL-2.0, so it is open source, but it is a third-party binary this
repository does not build. If SignPath objects, the options are to fetch PawnIO
on demand instead of bundling it, or to buy a certificate outright.

Signing runs in CI rather than locally, which is the point. The certificate
never touches a developer machine. `.github/workflows/release.yml` builds both
platforms on a tag and has the SignPath step ready to uncomment.

For a certificate you hold yourself, the build signs locally too:

```powershell
.uilduild-windows.ps1 -SignThumbprint <certificate thumbprint>
```

or set `BBSPECS_SIGN_THUMBPRINT` once on the build machine. It needs `signtool.exe`
from the Windows SDK. Without a thumbprint the build still works and says it is
shipping unsigned.

Roughly what the options cost, cheapest first:

| Route | Notes |
| ----- | ----- |
| [Azure Trusted Signing](https://azure.microsoft.com/products/trusted-signing) | Around $10/month, run by Microsoft. Individuals need a verifiable history; organisations need to be registered. |
| [SignPath Foundation](https://signpath.org) | Free for qualifying open-source projects. |
| OV certificate (Sectigo, DigiCert, …) | A few hundred a year. Since 2023 the key must live on a hardware token or cloud HSM. |
| EV certificate | More again, but carries SmartScreen reputation from day one. |

A self-signed certificate does *not* help: the prompt still reads "Unknown" on every
machine that doesn't already trust it, which is every machine but yours.

### Iterating on the interface

On Windows the Administrator manifest means a UAC prompt on every launch, which gets
old fast while working on the front-end. `-p:Elevate=false` leaves it out:

```powershell
dotnet build src\BBSpecs -p:Elevate=false -p:PublishSingleFile=false -p:SelfContained=false
```

The app then runs unelevated and shows its own "some readings are unavailable" banner,
which is exactly what a user without Administrator rights sees, so it's worth testing
anyway. The build scripts never pass the flag, so releases always ship with it.

### Seeing what BBSpecs reads on a given machine

`tools/SnapshotDump` prints one full snapshot to the console and writes the exact JSON
the interface receives. It's the fastest way to work out why a reading looks wrong on
hardware you don't have in front of you, and on Linux it also lists which optional
tools are present.

```bash
dotnet run --project tools/SnapshotDump
sudo dotnet run --project tools/SnapshotDump     # Linux, to include the root-only bits
```

To find out why one particular reading is blank, list every raw sensor the machine
offers. If a value isn't in that list, the hardware or the driver isn't providing it:

```bash
dotnet run --project tools/SnapshotDump -- --sensors
```

---

## How it's put together

```
src/BBSpecs/
  Program.cs              Window host, collector thread, host/page messaging
  Models/Snapshot.cs      The shape of everything the interface receives
  Services/               Shared: verdict engine, network, volumes, naming, JSON
  Platform/Windows/       WMI, LibreHardwareMonitor, the native Wi-Fi API
  Platform/Linux/         /proc, /sys, hwmon, and the optional tools above
  web/                    The interface: HTML, CSS and one JavaScript file
```

The app is a native window hosting a webview, the same approach VS Code, Discord and
Spotify use. The C# side collects a full snapshot once a second on a background thread
and hands it to the page as JSON; the page patches the DOM in place so scroll position
and animations survive every update.

Only the `Platform/` folder matching the build target is compiled, so neither build
carries code it could never run.

### Adding a reading

1. Add the field to `Models/Snapshot.cs`.
2. Fill it in both `Platform/Windows/WindowsHardware.cs` and
   `Platform/Linux/LinuxHardware.cs`. Leaving it null on one platform is fine, the
   interface renders a dash.
3. Render it in `web/app.js`.
4. Bump `<Version>` in `src/BBSpecs/BBSpecs.csproj` and add a line to `CHANGELOG.md`.

The version in the window title comes from that one `<Version>` property; nothing else
needs editing.

---

## Licence and credits

BBSpecs is released under the **MIT License**. See [`LICENSE`](LICENSE), which
also lists every third-party component and its terms.

Set in [Geist Pixel](https://github.com/vercel/geist-pixel-font) by Vercel in
collaboration with basement.studio (SIL Open Font License), bundled with the app so it
never fetches anything from the web.

Hardware sensor readings on Windows come from
[LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0),
which reads processor temperature through [PawnIO](https://pawnio.eu) (GPL-2.0, bundled
installer). The window is [Photino](https://github.com/tryphotino/photino.NET) (Apache-2.0).

BBSpecs is by [BOSSx](https://bossx.ca).
