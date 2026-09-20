# BBSpecs Privacy Policy

Last updated: 20 September 2026

## The short version

BBSpecs reads the hardware inside your computer and explains it in plain
language. Everything it finds stays on your machine. There is no account, no
telemetry, no analytics, and nothing is ever sent anywhere for me to look at. I
could not tell you a single thing about your computer, because none of it ever
reaches me.

BBSpecs makes four kinds of outbound connection and I have listed all four
below. None of them carry anything about your hardware, and three of the four
you can avoid entirely.

## What stays on your computer

Everything BBSpecs reads about your machine is read, displayed, and then
forgotten. I never upload it, never store it on a server, and nobody but you
ever sees it. That includes:

- Your processor, graphics card, memory, motherboard and BIOS details
- Serial numbers of your drives, memory modules and motherboard
- Drive health, capacity, temperatures and how full each volume is
- Installed driver names, versions and dates
- Network adapter names, your local IP address, your Wi-Fi network name, and
  whether a VPN is connected
- Your computer name, your Windows user name, and how long the machine has
  been running

## What leaves your computer

### 1. Checking that you are online

Every five seconds, BBSpecs pings `1.1.1.1`, Cloudflare's public DNS resolver.
This is the most frequent thing it does on the network and it happens
automatically, so I have listed it first.

It is how the Internet tab knows whether you are actually online and what your
latency is. A ping carries no information about you or your machine beyond the
fact that a packet arrived from your address, which is true of any packet you
send anywhere. No hostname is looked up and no data is exchanged. If the ping is
blocked, which is common on locked-down networks and on Linux without elevated
rights, BBSpecs falls back to asking Windows whether a route out exists, which
involves no network traffic at all.

Cloudflare publishes a
[privacy commitment for 1.1.1.1](https://developers.cloudflare.com/1.1.1.1/privacy/public-dns-resolver/).

Blocking BBSpecs in your firewall stops this. The app then reports that you are
offline, and everything else carries on working.

### 2. Checking for updates

BBSpecs asks GitHub whether a newer version exists. This happens once when the
app starts, and again whenever you choose "Check for updates" from the menu or
the notification area icon.

The request goes to `api.github.com` and says only which version of BBSpecs is
asking. As with any request to any website, GitHub can see the internet address
it came from. Nothing about your hardware is included.

If you choose to install an update, the new file is downloaded from GitHub the
same way.

To avoid this entirely, block BBSpecs in your firewall. Every reading about your
own hardware works with no internet connection at all.

GitHub is operated by Microsoft, whose
[privacy statement](https://privacy.microsoft.com/privacystatement) applies to
what they do with the requests they receive.

### 3. Looking up your public address, only if you ask

The Internet tab can show the public internet address your provider has given
you, along with the rough location and provider name attached to it. This is the
address the rest of the internet already sees whenever you visit any website.

**BBSpecs never looks this up on its own.** It is only ever fetched when you
press the refresh control on the "Public address" card. If you never press it,
the request is never made.

One thing worth knowing: once you have asked for it in a session, BBSpecs will
quietly refresh it if your connection changes, for instance when you connect or
disconnect a VPN. I did that so it cannot sit there showing an address that is
no longer yours. It only happens after you have asked at least once, and it
stops when you close the app.

The lookup is tried against these services in order, stopping at the first that
answers:

- [ipwho.is](https://ipwho.is)
- [ifconfig.co](https://ifconfig.co)
- [ipinfo.io](https://ipinfo.io/privacy-policy)
- [ipify.org](https://www.ipify.org)

These are third parties with their own policies. The request tells them nothing
except that someone at your address asked, which is unavoidable for a service
whose entire purpose is to report your address back to you. The result is shown
on screen and kept only in memory. It is not written to disk and it is not sent
anywhere else.

### 4. Links you choose to open

Some buttons open your normal web browser: the link to bossx.ca, the driver
download pages for NVIDIA, AMD, Intel and Realtek, the PawnIO website, and the
Microsoft page for the WebView2 runtime. These are ordinary visits to those
sites, made by your browser, and each site's own policy applies. BBSpecs does
not pass anything to them beyond the address of the page.

## What BBSpecs never does

- No analytics, telemetry, usage tracking or crash reporting
- No account, no sign-in, no licence check
- It never uploads your hardware details, serial numbers or addresses
- I never sell or share anything, because I never collect anything to sell
- It does not read your documents, your browsing history, or any file except the
  settings it wrote itself

## Files BBSpecs writes

Two, both on your own machine:

| What | Where | Contains |
| --- | --- | --- |
| Settings | `%APPDATA%\BBSpecs\settings.json` | Your theme, whether details are masked, whether alerts are on |
| Crash log | `%LOCALAPPDATA%\BBSpecs\crash.log` | Only written if something goes wrong, and only read by you |

The crash log is never sent anywhere. If BBSpecs falls over, it tells you where
the file is so you can look at it, or attach it to a bug report yourself if you
want to.

Uninstalling BBSpecs leaves your settings in place on purpose, so that
reinstalling finds your preferences again. Delete the `BBSpecs` folder in
`%APPDATA%` if you want them gone.

## Two features worth explaining

**Privacy mode** masks serial numbers, addresses and network names on screen, so
you can screenshot the window without thinking about it. It is a display
setting. Those details were never leaving your machine either way.

**Copy Specs** puts a short summary of your machine on your clipboard so you can
paste it when asking for help. I deliberately leave out serial numbers and
addresses. Nothing is sent anywhere: it goes to your clipboard and nowhere else,
and what you do with it afterwards is up to you.

## Administrator access

BBSpecs asks for Administrator when it starts. That is because Windows only lets
elevated programs read temperature sensors, fan speeds and drive health. It is
used for reading those sensors and nothing else.

Processor temperature additionally needs a kernel driver, and Windows offers no
way around that. BBSpecs carries the official [PawnIO](https://pawnio.eu)
installer and offers to run it the first time it is needed. I never install it
without asking, it runs entirely on your machine, and everything except
processor and motherboard temperature works without it.

## Changes to this policy

If BBSpecs ever starts doing something new with data, this page will say so
before that version goes out, and the date at the top will change.

## Contact

Questions about any of this: [bossx.ca](https://bossx.ca/), or open an issue at
[github.com/BOSSx-00/BBSpecs](https://github.com/BOSSx-00/BBSpecs/issues).

BBSpecs is open source under the MIT licence. If you would rather not take any
of the above on trust, the code that makes these requests is in
`src/BBSpecs/Services/Updates.cs` and `src/BBSpecs/Services/NetworkService.cs`,
and you can read every line of it.
