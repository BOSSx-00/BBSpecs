# Changelog

Every fix and feature bumps the version, which shows in the window title next to
the logo: `BBSpecs  -  v<version>`.

The version lives in one place — `<Version>` in `src/BBSpecs/BBSpecs.csproj` — and
the app reads it back from its own assembly at runtime.

---

## 0.7.0

### Changed

- **The status glow flickers now**, like current through a filament. Three
  stacked shadows animate on deliberately uneven keyframes: mostly steady, with
  two quick dips. A smooth pulse reads as breathing; it's the irregular drop
  that reads as electrical. The small labels and the headline verdict run at
  different speeds so they drift out of phase instead of blinking in lockstep.

  The letters themselves never change opacity — only the halo around them — so
  the text stays fully legible the whole time, and anyone who has asked their
  system for reduced motion gets a static glow instead.

### Added

- **The build can sign the executable.** Pass `-SignThumbprint`, or set
  `BBSPECS_SIGN_THUMBPRINT`, and the release is Authenticode-signed and
  timestamped. Without one the build still succeeds and says plainly that it's
  shipping unsigned.

### Repository

- **MIT licence.** `LICENSE` also lists every third-party component and its
  terms, and records why PawnIO's GPL does not extend to BBSpecs.
- **A release workflow** (`.github/workflows/release.yml`) builds both platforms
  on a tag and attaches the artifacts to a GitHub release. Building in CI from
  public source is a prerequisite for free signing, and useful on its own — the
  Linux build had never actually been run until now.

### Notes

- Signing is the *only* way to change the "Publisher: Unknown" line on the
  Administrator prompt. Windows reads that from the signature, not from the
  file's metadata — BBSpecs already declares `CompanyName = BOSSx`, which is why
  File Properties shows it, but UAC ignores that entirely. A certificate issued
  to BOSSx is required; see the README for the options.

---

## 0.6.0

### Added

- **Volumes open in your file manager.** Click any volume on the Drives tab and
  it opens that drive — Explorer on Windows, whatever the desktop uses on Linux.
  The box highlights on hover and shows an "Open" cue so it's clear it does
  something, and it answers the keyboard as well as the mouse.

### Notes

- The page can only ask BBSpecs to open a volume it just reported. The front-end
  is still the untrusted side of that boundary, and passing whatever string
  arrives to a shell would be a hole, so the request is checked against the
  volumes in the current snapshot before anything is launched.

---

## 0.5.1

### Fixed

- **VPN status went stale.** Disconnecting a VPN left BBSpecs reporting it as
  still connected, indefinitely. Detection only asked "is a tunnel adapter
  present, up, and holding an address" — and several clients, NordVPN's NordLynx
  among them, leave exactly that behind after you disconnect. It now requires the
  tunnel to actually carry the default route, so a tunnel that has stopped
  routing anything stops being reported.
- **The public address never updated.** Once looked up it sat there forever, so
  it would keep showing a VPN's address after disconnecting, or your own after
  connecting. BBSpecs now watches connection type, VPN state, address and
  adapter, and re-checks when any of them change. It still makes no lookup at all
  until you ask the first time — that opt-in is what enables the watching.
- The public address card has a **Refresh** button.

### Changed

- **Privacy starts on**, masking serial numbers, addresses and network names by
  default. Revealing them costs one click; a screenshot taken before anyone
  thought about it cannot be taken back.
- **Stronger glow on the status text.** Three stacked shadows — a bright tight
  core with two progressively wider, fainter halos — which is what reads as neon.
  A single blur either smears the letters or barely registers.

---

## 0.5.0

### Changed

- **Glow is now reserved for health readings.** It had spread to the wordmark,
  the active tab, every card title, the buttons, drive letters and the Privacy
  control, which made it decoration rather than signal. Those are all flat now;
  only the status words and the headline verdict glow.
- **The health glow itself is much softer.** It was two stacked halos at full
  colour strength, which smeared the text. It's now a single tight shadow at
  38% opacity, written per tier as rgba so the alpha can actually be dialled
  down — `currentColor` gives no way to do that.
- **Privacy is a proper button** and says what it's doing: *Privacy - Shown* or
  *Privacy - Hidden*, rather than just naming itself.
- **The opening screen talks.** Instead of one static line it cycles short baby
  puns every three seconds — "Warming the bottle", "Pampering the sensors",
  "Burping the bus", "Checking the diaper drive" — shuffled per launch so two
  starts in a row don't read the same.

### Added

- **BETA** badge beside the version in the header.

---

## 0.4.1

### Changed

- **Set in Geist Pixel** (the Square variant) instead of VT323, bundled the same
  way so nothing is fetched from the web. VT323 has an unusually small x-height
  and needed everything oversized to stay readable; Geist Pixel sits at normal
  proportions, so the whole type ramp comes back down and the wide tracking that
  VT323's blocky capitals needed is gone too.

### Fixed

- **A second copy of BBSpecs exited silently.** Launching it while one was
  already open did nothing visible at all — the same "double-click and nothing
  happens" failure the webview check was added to prevent. It now brings the
  running window to the front, and says so plainly if it can't find it.

  The window is matched by title rather than process name, because a release is
  renamed per version (`BBSpecs-0.4.1-win-x64.exe`) and the user may rename it
  again after downloading.

---

## 0.4.0

### Added

- **BBSpecs now installs the temperature driver for you.** One button in the
  banner, one click, done — no separate download, no instructions to follow.

  Windows gives no way to read processor temperature without a kernel driver:
  the instruction that reads the thermal register is privileged, which is why
  Task Manager doesn't show CPU temperature either. Every tool that does show it
  installs a driver. BBSpecs now carries the official PawnIO installer inside its
  own executable and offers to run it, so the dependency is the app's problem
  rather than the user's.

  Nothing is installed silently. The banner explains what it is and why, and
  declining leaves the machine untouched — everything except processor and
  motherboard temperature works without it.

- After a successful install the sensor layer reopens in place, so temperatures
  appear within a second or two. No restart.

### Notes

- The bundled installer is byte-for-byte the official 2.2.0 release, verified
  against the SHA-256 published in Microsoft's winget repository and
  Authenticode-signed by namazso.eu. `Assets/PawnIO/PawnIO-NOTICE.txt` carries
  the licence, the source links and the hash so anyone can check it themselves.
- PawnIO is GPL v2 with a linking exception for programs that talk to it through
  its device interface, which is how BBSpecs uses it. BBSpecs' own licensing is
  unaffected.
- The Linux build doesn't carry any of this — the kernel exposes temperatures
  through hwmon, so there was never a dependency there.
- Windows download grows from 37.5 MB to 40.8 MB.

---

## 0.3.0

### Fixed

- **Found the real reason processor temperature was blank, and it was none of the
  things BBSpecs had been blaming.**

  Modern LibreHardwareMonitor no longer ships its own kernel driver. It carries
  small sandboxed bytecode modules and runs them inside **PawnIO**, a separate
  signed driver installed once per machine. The old approach, WinRing0, let any
  program read and write arbitrary physical memory, so Microsoft put it on the
  Vulnerable Driver Blocklist; PawnIO does the same job safely and isn't blocked.

  Without PawnIO installed, every processor temperature sensor is present and
  every one reads null — which looks exactly like a permissions problem and isn't
  one. BBSpecs now detects this specifically and says so, with a button to fetch
  the driver. Graphics and drive temperatures were never affected, because those
  come from the vendor's own interface and from SMART.

- The earlier diagnoses (Core Isolation, then a conflicting vendor tool) are still
  checked, but only after PawnIO, which is by far the more common cause.

### Added

- Banner messages can now carry a link, so a problem with a known fix comes with a
  way to act on it rather than only an explanation.

---

## 0.2.3

### Fixed

- **The "no temperatures" message blamed the wrong thing.** It asserted Core
  Isolation, which was wrong on a machine where Memory integrity was already off.
  It now names the cause it can actually detect — other software holding the
  hardware — and otherwise lists the real candidates without pretending to know
  which one it is.
- BBSpecs now spots the common culprits by name: ASUS Armoury Crate and AI Suite,
  MSI Afterburner, Dragon Center and MSI Center, AIDA64, HWiNFO, HWMonitor,
  GIGABYTE Control Center, Corsair iCUE, SignalRGB and ThrottleStop. Only one
  program at a time can hold this kind of low-level access, and vendor suites
  take it at startup and keep it.

### Added

- **`tools/driver-report.ps1`** — asks for Administrator, then writes a full
  diagnostic to `driver-report.txt`: Memory integrity and Vulnerable Driver
  Blocklist state, Secure Boot, conflicting software, registered kernel drivers,
  recent blocked-driver events, and the sensor library's own report. The Ring0
  section of that report says exactly why the driver did or didn't load.
- `dotnet run --project tools/SnapshotDump -- --report` prints the same sensor
  library diagnostic on its own.

---

## 0.2.2

### Added

- **A startup check for the system webview**, the one thing BBSpecs can't carry
  inside its own executable. Previously a PC without it launched, failed inside
  the native layer, and closed again without a word — a windowed app has no
  console to complain to, so the user double-clicked and nothing happened. Now
  they get a plain-language dialog naming exactly what's missing, and on Windows
  an offer to open the Microsoft download page. On Linux it prints the install
  command for their distribution.
- Any other startup failure now shows a dialog with the reason and the path to
  the log, instead of failing silently for the same reason.

### Notes

- The check is deliberately generous, so it can't block a machine that would
  have worked: on Windows it accepts a per-machine, per-user or
  organisation-pinned runtime, and falls back to looking for the runtime's folder
  if registry access is blocked by policy; on Linux it checks the linker cache and
  then the usual library directories.

---

## 0.2.1

### Changed

- Health indicators are **glowing text on its own** — the blinking `x` marker is
  gone. The list bullets and per-core status markers that picked it up in 0.2.0
  are back to small static glowing dots.
- **Darker surfaces throughout.** The background drops to near-black and every
  panel shade follows it down, which the glow and the brand red both read far
  better against. Card shadows deepened to match.

---

## 0.2.0

### Added

- **Copy Specs** on the Overview — puts a short plain-text summary of the machine
  on the clipboard (processor, graphics, motherboard, memory, every drive and its
  capacity, and the network hardware), ready to paste wherever you're asking for
  help. Serial numbers and addresses are deliberately left out.
- **Upgrade ideas** on the Overview — names the cheapest part that would actually
  make a difference, in the order worth buying. It reads the real state of the
  machine, so it catches things like mismatched memory sticks dragging a whole
  set down to the slowest one's speed. Parts are named, never linked, and no
  prices are quoted because they go stale.
- Missing readings now say **why** they're missing rather than showing a dial
  stuck at zero — "Needs Administrator", "Sensor driver blocked", or "Not
  reported by this drive".
- When Windows blocks the sensor driver despite Administrator rights, BBSpecs now
  names the cause (Core Isolation / Memory integrity) and how to turn it off.

### Changed

- **Set in VT323**, bundled with the app so nothing is fetched from the web.
- **Health indicators are glowing text with a blinking `x`** instead of filled
  bubbles.
- **The Drives tab is one card per drive.** Each drive's headline, dials, details
  and volumes were previously four separate panels, which turned a four-drive
  machine into sixteen unrelated boxes.
- **The window fills its width when resized or maximised.** Grid tracks used
  `auto-fill`, which keeps empty columns around, stranding cards at two-thirds
  width on a wide screen; they now use `auto-fit`.
- The version appears once, in the app's own header — the window title is just
  "BBSpecs".
- Removed the translucent red bloom under the brand bar.
- All-caps labels are larger and more widely spaced, because VT323's capital M
  loses its middle stroke at small sizes.

### Fixed

- **The public address lookup never returned anything.** The reply resumed on a
  background thread after its network call, and the webview can only be written
  to from the window's own thread, so the result was silently dropped. It's now
  handed over on the next poll. It also falls back across several lookup services
  — behind a VPN the shared exit address is often already over one service's free
  rate limit — and explains that a VPN's address is what you'll see.
- **Drive life percentage** is read under the several names different drives use
  for it, and handles drives that report wear used rather than life remaining.
- A gauge at almost zero no longer leaves a floating dot from its rounded cap.
- Processor package power reading as exactly 0 W is now treated as "not
  reported", which is what it means.

### Developer

- `-p:Elevate=false` builds without the Administrator manifest, so the interface
  can be iterated on without a UAC prompt on every rebuild.
- `dotnet run --project tools/SnapshotDump -- --sensors` lists every raw sensor
  the machine offers, which is the quickest way to find out why a reading is
  blank on hardware you don't have.

---

## 0.1.0

First release.

### Added

- **Overview** — an at-a-glance verdict for the whole machine, scored out of 100,
  with "what's working well" and "what's worth looking at" called out in plain
  language. Summary cards for the processor, graphics, memory, motherboard,
  internet connection and every attached drive.
- **Processor** — model, family and rough release year, temperature, live load and
  clock speed, physical cores versus threads, cache sizes, package power, and a
  per-core grid showing each core's temperature, speed, load and health.
- **Graphics** — model, vendor, video memory total and in use, temperature and hot
  spot, core and memory clocks, fan speed, power draw, driver version (shown the way
  the vendor writes it, not the way Windows records it), PCI slot location, and the
  current display resolution and refresh rate.
- **Drives** — every attached drive with its type, bus, capacity, temperature,
  remaining life, lifetime data written, firmware and serial, plus each volume's
  free space.
- **Internet** — connection type, live upload and download, Wi-Fi network with signal
  strength, band, channel, 802.11 generation and security, VPN detection with a
  plain-English explanation, full adapter details, and an opt-in public IP lookup.
- **Verdicts everywhere** — every part gets rated Amazing / Good / Decent /
  Upgrade Soon / Outdated with a sentence saying why, colour-coded green, yellow,
  orange or red.
- **Privacy toggle** in the title bar that blanks serial numbers, IP and MAC
  addresses and the Wi-Fi network name before a screenshot.
- **Windows and Linux** from one codebase, each shipping as a single self-contained
  file with no runtime to install.
- **`tools/SnapshotDump`** — a console diagnostic that prints everything BBSpecs can
  read on a machine, so a wrong reading on unfamiliar hardware is traceable.

### Notes

- Runs without elevation and says so rather than showing empty boxes: a banner
  explains what's missing and offers to restart with full access.
- Makes no network requests beyond a connectivity ping; the public IP lookup only
  runs when you click the button.
