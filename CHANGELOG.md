# Changelog

Every fix and feature bumps the version, which shows in the window title next to
the logo: `BBSpecs  -  v<version>`.

I keep the version in one place, `<Version>` in `src/BBSpecs/BBSpecs.csproj`, and
the app reads it back from its own assembly at runtime.

---

## 0.9.3

### Fixed

- **Uninstalling while BBSpecs was open left the program behind.** Everything
  else went: the shortcuts, the logon task and the uninstaller itself. Then
  removal failed on the one file that mattered and left BBSpecs.exe sitting in
  the install folder with nothing left to remove it.

  Inno's own setting for this is supposed to close running applications through
  the Restart Manager, and it didn't. Makes sense once you think about it: a
  copy started by the logon task sits in the notification area with no window on
  screen for the Restart Manager to talk to. So I close a running copy myself
  now, both before installing over one and before removing one.

  I ask rather than force, so the app saves its settings and takes its tray icon
  with it, and I only insist if that gets ignored. I also wait until you have
  committed, after you press Install or confirm removal, because closing your
  running app just because you opened the installer to look at it would be rude.
  Portable copies are left alone: I only close the installed name.

## 0.9.2

### Fixed

- **Every row in the theme menu drew itself in its own theme's colours.** The
  Astro row rendered in Astro's dark brown on a near black menu, which made it
  look disabled, and every row lit up a different colour on hover.

  I had written the palettes as `[data-theme="astro"]`, which matches any
  element carrying that attribute, and the buttons that pick a theme were
  carrying it too. So each button quietly applied that whole palette to itself
  and took its text colour from it. The Akbu row only escaped by luck: its
  palette happens not to redefine the text colour.

  Palettes are pinned to `:root` now, because a palette belongs to the page
  rather than to whatever element happens to name a theme. I moved the buttons
  onto a different attribute as well, so the two cannot collide again.

## 0.9.1

### Fixed

- **The installer could not launch BBSpecs when it finished.** Ticking "Launch
  BBSpecs" on the last page gave you "CreateProcess failed; code 740. The
  requested operation requires elevation."

  Inno runs the entries on that final page as the original, non-elevated user,
  on purpose, so that installing something does not leave it running with
  administrator rights. BBSpecs asks for administrator in its own manifest, so
  starting it that way could only ever fail. I launch it through the shell now,
  which reads the manifest and elevates properly: the app starts from the wizard
  exactly the way it starts from the Start Menu, with its own prompt, instead of
  inheriting the installer's rights.

  Everything else the wizard does was already right. The chosen folder, the
  Start Menu entry, the Add or Remove Programs record and the logon task all
  landed where they should.

## 0.9.0

### Added

- **An installer.** A proper wizard: licence, a choice of folder, a Start Menu
  entry, optional desktop shortcut, an optional "start with Windows" tick, and
  an uninstaller in Add or Remove Programs that takes the logon task with it.

  The portable single file is not going anywhere, and it is still the first
  thing on the download page. They are the same program and I do not treat
  either as second class: one is for people who want BBSpecs to live somewhere,
  the other for people who want to run it once out of their Downloads folder and
  forget about it. Installed, it is always `BBSpecs.exe` in a fixed folder, so a
  logon task or a shortcut pointing at it never goes stale when a new version
  lands.

- **Updating knows which of the two it is.** A portable copy still replaces its
  own file. An installed one downloads the new installer and runs it silently
  instead, because if it quietly swapped its own executable, Add or Remove
  Programs would sit there reporting a version that is not on the machine any
  more.

- **A greeting on the first run**, once and never again, with a link to
  bossx.ca.

- **The wizard carries my logo** instead of Inno Setup's generic box and disc,
  which tells you nothing about what you are installing. I generate it from the
  same logo file the app and its icon come from, so the three cannot drift
  apart, at two sizes so it stays sharp on a high DPI display. It lives under
  build/ rather than with the app's own assets: everything in there gets
  compiled into the executable, and installer artwork has no business adding the
  best part of a megabyte to every download.

### Changed

- **Themes moved into the options list.** Theme is one row now, next to Privacy
  and Notifications, showing the current theme as its value. The three names
  open beside the menu instead of taking up four rows on their own. It opens
  leftward because the menu already sits at the right edge of the window. I keep
  its icon lit like the switched-on rows around it, since there is always a
  theme and nothing to switch off.

- **Astro is a light theme now**: white cards on brown, with teal running
  through the rules and headings instead of only touching the titles. It is my
  only light theme, which means two things it does not share with the others.
  The traffic lights are darker, because the green that reads perfectly on
  near-black is close to invisible on paper, and a health colour nobody can read
  is worse than none. And I turned the neon glow off, because a glow behind dark
  text is a smudge rather than a light.

- **Barbie is called Akbu now**, and the list reads BOSSx, Astro, Akbu. If you
  already had the pink one you keep it: I migrate the stored name on load rather
  than dropping you back to the default.

## 0.8.2

### Added

- **Run on start**, in the menu. BBSpecs starts with the computer, sits in the
  notification area and does not open a window: the tray icon and its readings
  panel are the whole of it until you ask for more.

  I used a logon task rather than the usual Run registry key, and that is not a
  stylistic choice. BBSpecs asks for Administrator so it can read temperature
  sensors, and Windows silently refuses to start anything from the Run key that
  needs elevation. There is no prompt at sign-in, it simply never appears. A
  logon task created with the highest privileges runs without a prompt and
  without that failure. Creating one needs Administrator, so the option says
  "Needs full access" instead of failing quietly when BBSpecs is not elevated.

  The window is not hidden, it is parked far off the desktop with its taskbar
  button removed through the shell. Hiding it is the obvious approach and it
  does not work: a webview given a hidden window never initialises, so the first
  time you opened it the window came back completely blank. Parked off-screen it
  is a real, paintable window as far as the webview is concerned, and nobody can
  see it. I only park it when the tray icon is genuinely there, because a window
  you cannot see with nothing to bring it back is an app you cannot reach.
  Launching BBSpecs again while a copy is in the tray brings that copy up
  instead of telling you it is already running.

  On Linux it is an ordinary autostart entry, and since there is no tray there,
  it starts minimised instead.

### Changed

- **Astro is darker and properly teal.** I dropped the browns several steps and
  moved the accent from a soft mint to a saturated teal. Both changes pull the
  same way: the panels recede and the readings are the only lit thing left,
  which is the whole idea of the theme.

- **New menu icons**, drawn from Lucide on a consistent grid: a paintbrush for
  the theme group, a hat and glasses for Privacy, a ringing bell for
  Notifications, and a download arrow for updates. The theme names keep an empty
  icon slot, so every piece of text in the menu lines up in one column.

## 0.8.1

### Fixed

- **The tray icon works again.** A left click was doing nothing because the
  panel was opening and closing itself inside the same instant. My previous
  attempt took the keyboard focus so it could close on losing it, but Windows
  refuses that to a process that does not already own the foreground window, and
  a click on a tray icon is input to the shell rather than to us, so the call
  failed at exactly the moment it was needed. It does not ask for focus at all
  now: while the panel is on screen it watches for a mouse button going down
  outside its own rectangle, and that closes it. Good side effect, the panel can
  appear over what you were doing without stealing what you were typing into.

  I tried two other approaches first and threw both away, and I left the reasons
  in the source. Polling the mouse on a timer misses most clicks, because a
  click is usually shorter than the gap between two polls, and the flag Windows
  offers for "pressed since you last asked" is documented as unreliable when
  anything else on the machine is polling it too. A low-level mouse hook sees
  every click however brief, is installed only while the panel is on screen, and
  passes the click straight through to wherever it was aimed.

### Changed

- **Barbie is grey now**, not violet. The surfaces lift several steps from the
  near-black default but stay neutral: a colour cast in the panels fought the
  pink instead of carrying it. The hot pink is unchanged.

- **Zoom is off.** The webview inherited a browser's zoom gestures, and this is
  a fixed layout rather than a document: half a step of zoom leaves the pixel
  typeface blurred and the gauges off their grid, and people kept finding it by
  accident with ctrl and a scroll wheel while trying to scroll the page. I
  ignore both ctrl with plus or minus and ctrl with the wheel.

- **The update box says which version you are running**, closes with an x in the
  corner rather than a button competing with the action, and offers Retry in
  every state where asking again could give a different answer.

- **The theme menu lists names only.** The colour dots next to them were
  decoration: the word already tells you which one it is.

### Added

- **An Astro theme.** Warm browns and beiges for the panels with turquoise and
  baby blue on top. The readings are the only lit thing on the page, which is
  where the mission-control look comes from.

## 0.8.0

### Added

- **A Memory tab.** The slot diagram is the point: every slot on the board drawn
  at the same size, filled ones showing what is in them and empty ones dashed,
  so "can I add more, and what do I buy" is answerable at a glance instead of
  through a trip into the BIOS. A stick running below its rated speed says so,
  because a mismatched set quietly drags the whole lot down to the slowest
  module and nothing else on the machine tells you.

- **A Drivers tab.** It reports what is installed and how old it is, and it does
  not pretend to know whether something newer exists, because nothing on your
  computer does: Windows Update knows about some drivers, the vendors know about
  the rest, and there is no way to ask either. I rate age differently per
  category for the same reason. A year-old graphics driver is genuinely leaving
  performance behind; a sound driver Microsoft last touched in 2019 is almost
  certainly still the newest one there will ever be, and calling it out of date
  would send you hunting for a file that does not exist. The BIOS is listed
  alongside, with the warning that a failed firmware update can stop a machine
  booting.

- **A tray icon**, with the readings widget on a left click and a menu on a
  right click. I draw the widget directly rather than making it a second
  webview: Photino's extra windows run a nested message loop that would freeze
  the main window for as long as the panel stayed open, and a browser engine is
  a heavy way to print six numbers.

- **Desktop notifications**, off by default, for the handful of things worth
  interrupting you over: a processor or graphics card hot enough to throttle, a
  drive near the end of its life, the system volume nearly full, and the
  connection dropping. A reading has to stay bad for five seconds before it
  counts, nothing repeats inside an hour, and a condition has to clear before it
  can fire again. A monitor that cries wolf gets muted, and then it is no use on
  the day something is actually wrong.

- **Themes**, in the new menu. BOSSx is unchanged. Barbie keeps the same layout
  and swaps the reds for hot pink, the trim for a lighter pink, and lifts every
  surface a few steps with a violet cast. I deliberately left the green, yellow
  and red health colours alone in both: green has to mean fine and red has to
  mean trouble whatever the paint.

- **Updating in place.** BBSpecs checks GitHub on startup, and offers you
  Install or Later if there is something newer. Install downloads it, puts it
  where the running program is, and restarts. I rename the old file rather than
  deleting it until the next launch has proved the new one works, so a download
  that fails halfway leaves your working version exactly where it was.

- **A way out of an unpartitioned disk.** A disk with no volumes, or with a
  chunk of unallocated space going unused, now says so and opens Disk Management
  (or your desktop's equivalent on Linux). I leave partitioning to the tool that
  owns it: shipping a way to do it here would mean shipping a way to destroy
  data by accident.

- **Settings that survive a restart.** Theme, privacy and notifications live
  beside the app's own data rather than in the page, because a webview loaded
  from a file has an origin that browsers treat as untrustworthy and its storage
  can be cleared without warning.

### Changed

- **The interface explains itself less.** I had fourteen blocks of prose sitting
  under readings that were perfectly fine, in the voice of something teaching a
  class. An explanation only shows up now when a verdict is below good, which is
  the moment you actually want to know why. The processor glossary folds away
  instead of holding a third of the screen permanently.

- **The Overview is no longer six identical boxes in an even grid.** The
  processor and graphics card get a roomy two-across row because they are what
  you opened this for; memory, storage and the connection are packed tighter
  below. Card corners are sharper and I dropped the drop shadows.

- **Privacy and the new settings moved into a menu**, so the brand bar is a
  brand bar again.

- **Copy Specs and Upgrade ideas are outlined**, not filled. The hover fill is
  unchanged.

- **Tab and label wording.** Drives is Disks now, and a Drivers tab sits beside
  it. Processor and Graphics say "(CPU)" and "(GPU)" so you learn the acronym
  and the word together. The wireless verdict says "Wireless" rather than
  "Wireless Connection".

- **Logos cannot be dragged out of the window**, which only ever looked like a
  bug.

- **Unallocated space is measured against the partition table**, not against the
  volumes with drive letters. A normal Windows disk keeps an EFI, an MSR and a
  recovery partition that carry no letter, so counting volumes had me reporting
  about 26 GB going spare on a perfectly healthy 512 GB drive. A disk I cannot
  measure properly now says nothing, because a storage tool that invents free
  space is worse than one that stays quiet.

- **A failed update check says so**, instead of telling you that you are up to
  date. Being unable to reach GitHub and having nothing to install are different
  answers, and running them together is how you end up several releases behind
  while being told you are current.

## 0.7.0

### Changed

- **The status glow flickers now**, like current through a filament. Three
  stacked shadows animate on deliberately uneven keyframes: mostly steady, with
  two quick dips. A smooth pulse reads as breathing; it's the irregular drop
  that reads as electrical. The small labels and the headline verdict run at
  different speeds so they drift out of phase instead of blinking in lockstep.

  The letters themselves never change opacity, only the halo around them, so
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
  public source is a prerequisite for free signing, and useful on its own: the
  Linux build had never actually been run until now.

### Notes

- Signing is the *only* way to change the "Publisher: Unknown" line on the
  Administrator prompt. Windows reads that from the signature, not from the
  file's metadata: BBSpecs already declares `CompanyName = BOSSx`, which is why
  File Properties shows it, but UAC ignores that entirely. A certificate issued
  to BOSSx is required; see the README for the options.

---

## 0.6.0

### Added

- **Volumes open in your file manager.** Click any volume on the Drives tab and
  it opens that drive: Explorer on Windows, whatever the desktop uses on Linux.
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
  present, up, and holding an address", and several clients, NordVPN's NordLynx
  among them, leave exactly that behind after you disconnect. It now requires the
  tunnel to actually carry the default route, so a tunnel that has stopped
  routing anything stops being reported.
- **The public address never updated.** Once looked up it sat there forever, so
  it would keep showing a VPN's address after disconnecting, or your own after
  connecting. BBSpecs now watches connection type, VPN state, address and
  adapter, and re-checks when any of them change. It still makes no lookup at all
  until you ask the first time: that opt-in is what enables the watching.
- The public address card has a **Refresh** button.

### Changed

- **Privacy starts on**, masking serial numbers, addresses and network names by
  default. Revealing them costs one click; a screenshot taken before anyone
  thought about it cannot be taken back.
- **Stronger glow on the status text.** Three stacked shadows, a bright tight
  core with two progressively wider, fainter halos, which is what reads as neon.
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
  down: `currentColor` gives no way to do that.
- **Privacy is a proper button** and says what it's doing: *Privacy - Shown* or
  *Privacy - Hidden*, rather than just naming itself.
- **The opening screen talks.** Instead of one static line it cycles short baby
  puns every three seconds ("Warming the bottle", "Pampering the sensors",
  "Burping the bus", "Checking the diaper drive"), shuffled per launch so two
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
  already open did nothing visible at all: the same "double-click and nothing
  happens" failure the webview check was added to prevent. It now brings the
  running window to the front, and says so plainly if it can't find it.

  The window is matched by title rather than process name, because a release is
  renamed per version (`BBSpecs-0.4.1-win-x64.exe`) and the user may rename it
  again after downloading.

---

## 0.4.0

### Added

- **BBSpecs now installs the temperature driver for you.** One button in the
  banner, one click, done: no separate download, no instructions to follow.

  Windows gives no way to read processor temperature without a kernel driver:
  the instruction that reads the thermal register is privileged, which is why
  Task Manager doesn't show CPU temperature either. Every tool that does show it
  installs a driver. BBSpecs now carries the official PawnIO installer inside its
  own executable and offers to run it, so the dependency is the app's problem
  rather than the user's.

  Nothing is installed silently. The banner explains what it is and why, and
  declining leaves the machine untouched: everything except processor and
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
- The Linux build doesn't carry any of this: the kernel exposes temperatures
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
  every one reads null, which looks exactly like a permissions problem and isn't
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
  It now names the cause it can actually detect: other software holding the
  hardware, and otherwise lists the real candidates without pretending to know
  which one it is.
- BBSpecs now spots the common culprits by name: ASUS Armoury Crate and AI Suite,
  MSI Afterburner, Dragon Center and MSI Center, AIDA64, HWiNFO, HWMonitor,
  GIGABYTE Control Center, Corsair iCUE, SignalRGB and ThrottleStop. Only one
  program at a time can hold this kind of low-level access, and vendor suites
  take it at startup and keep it.

### Added

- **`tools/driver-report.ps1`**: asks for Administrator, then writes a full
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
  the native layer, and closed again without a word: a windowed app has no
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

- Health indicators are **glowing text on its own**: the blinking `x` marker is
  gone. The list bullets and per-core status markers that picked it up in 0.2.0
  are back to small static glowing dots.
- **Darker surfaces throughout.** The background drops to near-black and every
  panel shade follows it down, which the glow and the brand red both read far
  better against. Card shadows deepened to match.

---

## 0.2.0

### Added

- **Copy Specs** on the Overview: puts a short plain-text summary of the machine
  on the clipboard (processor, graphics, motherboard, memory, every drive and its
  capacity, and the network hardware), ready to paste wherever you're asking for
  help. Serial numbers and addresses are deliberately left out.
- **Upgrade ideas** on the Overview: names the cheapest part that would actually
  make a difference, in the order worth buying. It reads the real state of the
  machine, so it catches things like mismatched memory sticks dragging a whole
  set down to the slowest one's speed. Parts are named, never linked, and no
  prices are quoted because they go stale.
- Missing readings now say **why** they're missing rather than showing a dial
  stuck at zero: "Needs Administrator", "Sensor driver blocked", or "Not
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
- The version appears once, in the app's own header: the window title is just
  "BBSpecs".
- Removed the translucent red bloom under the brand bar.
- All-caps labels are larger and more widely spaced, because VT323's capital M
  loses its middle stroke at small sizes.

### Fixed

- **The public address lookup never returned anything.** The reply resumed on a
  background thread after its network call, and the webview can only be written
  to from the window's own thread, so the result was silently dropped. It's now
  handed over on the next poll. It also falls back across several lookup services
 : behind a VPN the shared exit address is often already over one service's free
  rate limit, and explains that a VPN's address is what you'll see.
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

- **Overview**: an at-a-glance verdict for the whole machine, scored out of 100,
  with "what's working well" and "what's worth looking at" called out in plain
  language. Summary cards for the processor, graphics, memory, motherboard,
  internet connection and every attached drive.
- **Processor**: model, family and rough release year, temperature, live load and
  clock speed, physical cores versus threads, cache sizes, package power, and a
  per-core grid showing each core's temperature, speed, load and health.
- **Graphics**: model, vendor, video memory total and in use, temperature and hot
  spot, core and memory clocks, fan speed, power draw, driver version (shown the way
  the vendor writes it, not the way Windows records it), PCI slot location, and the
  current display resolution and refresh rate.
- **Drives**: every attached drive with its type, bus, capacity, temperature,
  remaining life, lifetime data written, firmware and serial, plus each volume's
  free space.
- **Internet**: connection type, live upload and download, Wi-Fi network with signal
  strength, band, channel, 802.11 generation and security, VPN detection with a
  plain-English explanation, full adapter details, and an opt-in public IP lookup.
- **Verdicts everywhere**: every part gets rated Amazing / Good / Decent /
  Upgrade Soon / Outdated with a sentence saying why, colour-coded green, yellow,
  orange or red.
- **Privacy toggle** in the title bar that blanks serial numbers, IP and MAC
  addresses and the Wi-Fi network name before a screenshot.
- **Windows and Linux** from one codebase, each shipping as a single self-contained
  file with no runtime to install.
- **`tools/SnapshotDump`**: a console diagnostic that prints everything BBSpecs can
  read on a machine, so a wrong reading on unfamiliar hardware is traceable.

### Notes

- Runs without elevation and says so rather than showing empty boxes: a banner
  explains what's missing and offers to restart with full access.
- Makes no network requests beyond a connectivity ping; the public IP lookup only
  runs when you click the button.
