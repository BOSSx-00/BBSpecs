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

**[Get the latest release](https://github.com/BOSSx-00/BBSpecs/releases/latest)**

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

## Working on BBSpecs

Building it yourself, the project layout, and how to add a reading are in
[BUILDING.md](BUILDING.md).

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
