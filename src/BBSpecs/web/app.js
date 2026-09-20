/* ==========================================================================
   BBSpecs front-end
   Receives a full snapshot from the host once a second and renders the active
   tab. The DOM is patched in place rather than rebuilt so scroll position,
   hover states and bar animations survive every update.
   ========================================================================== */

'use strict';

/* ───────────────────────────── host bridge ───────────────────────────── */

// Photino carries strings both ways: window.external.sendMessage to the host,
// window.external.receiveMessage for anything coming back.
const host = {
  send(cmd, extra) {
    const payload = JSON.stringify(Object.assign({ cmd }, extra || {}));
    try {
      window.external.sendMessage(payload);
    } catch (_) { /* running outside the app shell */ }
  },

  listen(handler) {
    try {
      window.external.receiveMessage(raw => {
        let message;
        try {
          message = JSON.parse(raw);
        } catch (err) {
          // Never swallow this: a message that won't parse is the difference
          // between a live panel and one that sits on the loading screen.
          host.send('uiError', {
            detail: `Unreadable message (${String(raw).length} chars): ${String(raw).slice(0, 200)}`
          });
          return;
        }
        handler(message);
      });
    } catch (_) { /* running outside the app shell */ }
  }
};

/* ──────────────────────────────── state ──────────────────────────────── */

const state = {
  snap: null,
  tab: 'overview',
  // Starts masked: it costs nothing to reveal, and a screenshot taken
  // before anyone thinks about it cannot be taken back.
  privacy: true,
  // bossx | akbu | astro. Replaced by the stored choice when the host says hello.
  theme: 'bossx',
  notifications: false,
  // Read from the operating system on every launch, never from the settings
  // file: a logon task can be removed from outside the app.
  runOnStart: false,
  canRunOnStart: true,
  menuOpen: false,
  themeMenuOpen: false,
  welcome: false,
  // null, or the release the host found. Cleared by Later, and not asked for
  // again until the app is reopened.
  update: null,
  // offered | installing | ready | failed | current | unreachable | unsupported
  updateStage: 'offered',
  publicIp: null,
  publicIpLoading: false,
  open: {},
  // Filled in by the host's "hello" message.
  platform: 'windows',
  roleName: 'Administrator',
  canRelaunch: false,
  relaunchFailed: false,
  showUpgrades: false,
  copied: false,
  canInstallDriver: false,
  // idle | working | done | failed
  driverInstall: 'idle',
  driverMessage: ''
};

/* ───────────────────────────── formatting ────────────────────────────── */

const DASH = '—';

const has = v => v !== null && v !== undefined && !(typeof v === 'number' && !isFinite(v));

function esc(s) {
  if (!has(s)) return '';
  return String(s)
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
}

/** Blanks out anything identifying while Privacy mode is on. */
function priv(value) {
  if (!has(value) || value === '') return DASH;
  return state.privacy ? '<span class="masked">••••••••</span>' : esc(value);
}

const n = (v, digits = 0, suffix = '') =>
  has(v) ? Number(v).toFixed(digits) + suffix : DASH;

const pct = v => has(v) ? Math.round(v) + '%' : DASH;
const temp = v => has(v) ? Math.round(v) + '°C' : DASH;
const ghz = v => has(v) ? Number(v).toFixed(2) + ' GHz' : DASH;
const mhz = v => has(v) ? Math.round(v) + ' MHz' : DASH;
const watts = v => has(v) ? Math.round(v) + ' W' : DASH;

function gb(v, digits = 1) {
  if (!has(v)) return DASH;
  return v >= 1024 ? (v / 1024).toFixed(2) + ' TB' : Number(v).toFixed(digits) + ' GB';
}

/** Input is KiB/s. */
function rate(v) {
  if (!has(v)) return DASH;
  if (v >= 1024) return (v / 1024).toFixed(2) + ' MB/s';
  if (v >= 1) return v.toFixed(0) + ' KB/s';
  return '0 KB/s';
}

function uptime(hours) {
  if (!has(hours)) return DASH;
  const d = Math.floor(hours / 24);
  const h = Math.floor(hours % 24);
  const m = Math.floor((hours * 60) % 60);
  if (d > 0) return `${d} day${d === 1 ? '' : 's'}, ${h} hr`;
  if (h > 0) return `${h} hr ${m} min`;
  return `${m} min`;
}

/** Tier for a plain 0-100 utilisation figure. */
function loadTier(v) {
  if (!has(v)) return 'unknown';
  if (v < 60) return 'good';
  if (v < 85) return 'ok';
  return 'aging';
}

/** Tier for a "how full" figure, where more is worse. */
function fullTier(usedPercent) {
  if (!has(usedPercent)) return 'unknown';
  if (usedPercent < 65) return 'good';
  if (usedPercent < 85) return 'ok';
  if (usedPercent < 95) return 'aging';
  return 'poor';
}

/* ─────────────────────────── view components ─────────────────────────── */

function card(title, sub, body, extraClass) {
  return `<section class="card ${extraClass || ''}">
    <div class="card__head">
      <h2 class="card__title">${esc(title)}</h2>
      ${sub ? `<div class="card__sub">${sub}</div>` : ''}
    </div>
    <div class="card__body">${body}</div>
  </section>`;
}

function pill(verdict) {
  if (!verdict) return '';
  return `<span class="pill pill--${esc(verdict.tier)}">${esc(verdict.label)}</span>`;
}

function plainPill(text) {
  return `<span class="pill pill--plain">${esc(text)}</span>`;
}

function brandPill(text) {
  return `<span class="pill pill--brand">${esc(text)}</span>`;
}

function stat(key, value, opts) {
  const o = opts || {};
  const cls = 'stat__v' + (o.mono ? ' stat__v--mono' : '') + (o.dim ? ' stat__v--dim' : '');
  return `<div class="stat">
    <span class="stat__k">${esc(key)}</span>
    <span class="${cls}">${o.raw ? value : esc(value)}</span>
  </div>`;
}

function stats(rows) {
  return `<div class="stats">${rows.filter(Boolean).join('')}</div>`;
}

function bar(label, valueText, percent, tier) {
  const p = has(percent) ? Math.max(0, Math.min(100, percent)) : 0;
  return `<div class="bar-block">
    <div class="bar-label"><span>${esc(label)}</span><strong>${valueText}</strong></div>
    <div class="bar"><div class="bar__fill bar__fill--${tier || 'good'}" style="width:${p.toFixed(1)}%"></div></div>
  </div>`;
}

/**
 * Circular gauge. `max` scales the arc; the printed number is still the raw
 * value. When there is no reading the dial dims and says so: an empty ring on
 * its own just looks like the app is broken. `naNote` explains why in a few words.
 */
function gauge(name, value, unit, tier, max, naNote) {
  const R = 44;
  const CIRC = 2 * Math.PI * R;
  const live = has(value);
  const ceiling = max || 100;
  const frac = live ? Math.max(0, Math.min(1, value / ceiling)) : 0;
  const offset = CIRC * (1 - frac);

  return `<div class="gauge ${live ? '' : 'gauge--na'}">
    <div class="gauge__ring">
      <svg viewBox="0 0 104 104" width="104" height="104">
        <circle class="gauge__track" cx="52" cy="52" r="${R}"></circle>
        <circle class="gauge__arc gauge__arc--${live ? (tier || 'unknown') : 'unknown'}" cx="52" cy="52" r="${R}"
                stroke-dasharray="${CIRC.toFixed(2)}"
                stroke-dashoffset="${offset.toFixed(2)}"
                stroke-linecap="${frac < 0.01 ? 'butt' : 'round'}"></circle>
      </svg>
      <div class="gauge__mid">
        <div class="gauge__value ${live ? 'tone--' + (tier || 'unknown') : ''}">${live ? Math.round(value) : 'n/a'}</div>
        <div class="gauge__unit">${esc(unit)}</div>
      </div>
    </div>
    <div class="gauge__name">${esc(name)}</div>
    ${!live && naNote ? `<div class="gauge__na">${esc(naNote)}</div>` : ''}
  </div>`;
}

/*
  Prints the reasoning behind a verdict, but only when the verdict is not good
  news. A paragraph explaining why a healthy drive is healthy is the kind of
  thing nobody reads twice: it pads the page out and buries the readings that
  do need attention. Anything below "good" keeps its explanation, because that
  is the moment somebody actually wants to know why.
*/
function verdictNote(verdict) {
  if (!verdict || !verdict.reason) return '';
  if (verdict.tier === 'excellent' || verdict.tier === 'good') return '';
  return `<p class="verdict-note">${esc(verdict.reason)}</p>`;
}

function collapsible(key, label, body) {
  const open = !!state.open[key];
  return `<button class="toggle-row ${open ? 'is-open' : ''}" data-action="toggle" data-key="${esc(key)}" type="button">
      <span>${esc(label)}</span>
      <svg class="toggle-row__chev" viewBox="0 0 12 12" width="11" height="11" aria-hidden="true">
        <path d="M2.5 4.5 6 8l3.5-3.5" fill="none" stroke="currentColor" stroke-width="1.4"/>
      </svg>
    </button>
    ${open ? `<div style="margin-top:11px">${body}</div>` : ''}`;
}

function signalBars(percent, tier) {
  const lit = has(percent) ? Math.ceil(percent / 20) : 0;
  let out = '<div class="signal">';
  for (let i = 1; i <= 5; i++) {
    out += `<div class="signal__bar ${i <= lit ? 'is-lit--' + tier : ''}"></div>`;
  }
  return out + '</div>';
}

function emptyNote(text) {
  return `<p class="empty">${esc(text)}</p>`;
}

/* ────────────────────────────── tab: memory ──────────────────────────── */

/*
  The slot diagram is the reason this tab exists. Everything here is available
  elsewhere in ones and twos, but "which slots are full and what is in them" is
  the question people actually have before they buy more memory, and no built-in
  tool answers it without a trip through the BIOS.
*/
function renderMemory(d) {
  const m = d.memory;

  const filled = m.sticks.map(stick => {
    const rated = stick.speedMhz;
    const running = stick.configuredSpeedMhz;
    // A stick clocked below its rating is the quiet tax on a mismatched set.
    const throttled = has(rated) && has(running) && running < rated - 50;

    return `<div class="slot is-filled">
      <div class="slot__name">${esc(stick.slot || 'Slot')}</div>
      <div class="slot__cap">${gb(stick.capacityGb, 0)}</div>
      <div class="slot__spec">${esc(stick.type || m.type || '')}${has(running) ? ` ${running} MHz` : ''}</div>
      ${throttled ? `<div class="slot__warn">rated ${rated} MHz</div>` : ''}
      <div class="slot__maker">${esc(stick.manufacturer || 'Unbranded')}</div>
      ${stick.partNumber ? `<div class="slot__part">${esc(stick.partNumber)}</div>` : ''}
    </div>`;
  }).join('');

  const spare = Math.max(0, m.slotsTotal - Math.max(m.slotsUsed, m.sticks.length));
  const empty = Array.from({ length: spare }, () =>
    `<div class="slot is-empty"><div class="slot__cap">Empty</div></div>`).join('');

  const diagram = (m.sticks.length || spare)
    ? `<div class="slots">${filled}${empty}</div>`
    : emptyNote(state.platform === 'windows'
        ? 'Windows did not report the individual sticks on this machine.'
        : 'Reading the slot layout needs root access.');

  const headline = card('Memory (RAM)', pill(m.verdict), `
    <div class="headline">${gb(m.totalGb, 0)}${m.type ? ' ' + esc(m.type) : ''}${has(m.speedMhz) ? ` at ${m.speedMhz} MHz` : ''}</div>
    <div class="subline">${m.slotsUsed} of ${m.slotsTotal} slot${m.slotsTotal === 1 ? '' : 's'} filled${spare ? `, ${spare} free for more` : ''}</div>
    ${bar('In use right now',
          has(m.usedGb) ? `${gb(m.usedGb)} of ${gb(m.totalGb, 0)}` : DASH,
          m.loadPercent, fullTier(m.loadPercent))}
    ${verdictNote(m.verdict)}`);

  const layout = card('Slot layout', plainPill(`${m.slotsTotal} slot${m.slotsTotal === 1 ? '' : 's'}`), diagram);

  const detail = card('Details', '', stats([
    stat('Total installed', gb(m.totalGb, 0)),
    stat('In use', has(m.usedGb) ? `${gb(m.usedGb)} (${pct(m.loadPercent)})` : DASH),
    stat('Free', has(m.availableGb) ? gb(m.availableGb) : DASH),
    stat('Type', m.type || DASH),
    stat('Running at', mhz(m.speedMhz)),
    stat('Slots used', `${m.slotsUsed} of ${m.slotsTotal}`),
    m.sticks.length
      ? stat('Serial numbers',
             m.sticks.map(x => priv(x.serial)).join(' / '), { raw: true, mono: true })
      : null
  ]));

  return `<div class="grid grid--full">${headline}</div>
          <div class="grid grid--full">${layout}</div>
          <div class="grid grid--wide">${detail}</div>`;
}

/* ────────────────────────────── tab: drivers ─────────────────────────── */

/*
  What this tab does NOT do is claim to know whether a newer driver exists.
  Nothing on the machine knows that: Windows Update knows about some of them,
  the vendors know about the rest, and there is no way to ask either. So it
  reports the installed version and how old it is, which are facts, and says
  plainly what age does and does not mean for each kind of device.
*/
function renderDrivers(d) {
  if (!d.driversReady) {
    return `<div class="grid grid--full">${card('Drivers', plainPill('Reading'),
      emptyNote('Going through your devices. This takes a few seconds the first time.'))}</div>`;
  }

  if (!d.drivers.length) {
    return `<div class="grid grid--full">${card('Drivers', '', emptyNote(
      state.platform === 'windows'
        ? 'Windows did not report any driver dates on this machine.'
        : 'Linux does not keep driver release dates, so there is nothing to compare.'))}</div>`;
  }

  // Anything rated below "good" is the reason someone opened this tab.
  const attention = d.drivers.filter(x => x.verdict.tier === 'ok' || x.verdict.tier === 'aging' || x.verdict.tier === 'poor');

  // The worst of them sets the colour. Taking it from the first row meant the
  // headline could read green-ish while something below it was red.
  const rank = { poor: 0, aging: 1, ok: 2 };
  const worst = attention.slice().sort((a, b) => rank[a.verdict.tier] - rank[b.verdict.tier])[0];

  const summary = card('Drivers', attention.length
      ? pill({ tier: worst.verdict.tier, label: `${attention.length} worth a look` })
      : pill({ tier: 'good', label: 'Nothing urgent' }), `
    <div class="headline headline--sm">${attention.length
      ? `${attention.length} driver${attention.length === 1 ? '' : 's'} ${attention.length === 1 ? 'is' : 'are'} old enough to be worth updating`
      : 'Everything here is recent enough'}</div>
    <p class="verdict-note">
      Nothing on your computer can tell you whether a newer driver exists, so BBSpecs shows you how old
      the installed one is instead. That matters a lot for graphics and hardly at all for a sound card
      that Microsoft last touched in 2019 because there was nothing left to fix.
    </p>`);

  const groups = ['System firmware', 'Graphics', 'Network', 'Audio', 'Chipset'];

  const sections = groups.map(group => {
    const items = d.drivers.filter(x => x.category === group);
    if (!items.length) return '';

    const rows = items.map(x => `
      <div class="driver driver--${esc(x.verdict.tier)}">
        <div class="driver__top">
          <span class="driver__name">${esc(x.device)}</span>
          ${pill(x.verdict)}
        </div>
        <div class="driver__facts">
          <span>${x.version ? 'Version ' + esc(x.version) : 'Version not reported'}</span>
          <span>${x.date ? esc(x.date) : 'Date not reported'}</span>
          ${x.vendor ? `<span>${esc(x.vendor)}</span>` : ''}
        </div>
        ${verdictNote(x.verdict)}
        ${x.vendorUrl && x.verdict.tier !== 'excellent' && x.verdict.tier !== 'good'
          ? `<button class="btn btn--small" data-action="openHelp" data-url="${esc(x.vendorUrl)}" type="button">
               Where to get it
             </button>`
          : ''}
      </div>`).join('');

    return card(group, plainPill(`${items.length}`), rows);
  }).filter(Boolean);

  return `<div class="grid grid--full">${summary}</div>` +
         `<div class="grid grid--wide">${sections.join('')}</div>`;
}

/* ───────────────────────────── tab: overview ─────────────────────────── */

function renderOverview(d) {
  const gpu = d.gpus[0];
  const sysDrive = d.drives.find(x => x.isSystemDrive) || d.drives[0];
  const net = d.network;

  const R = 55;
  const CIRC = 2 * Math.PI * R;
  const offset = CIRC * (1 - Math.max(0, Math.min(100, d.system.score)) / 100);

  const hero = `<section class="card">
    <div class="hero">
      <div class="hero__score">
        <svg viewBox="0 0 128 128" width="128" height="128">
          <circle class="gauge__track" cx="64" cy="64" r="${R}"></circle>
          <circle class="gauge__arc gauge__arc--${esc(d.system.overall.tier)}" cx="64" cy="64" r="${R}"
                  stroke-dasharray="${CIRC.toFixed(2)}" stroke-dashoffset="${offset.toFixed(2)}"></circle>
        </svg>
        <div class="hero__scoremid">
          <div class="hero__num tone--${esc(d.system.overall.tier)}">${d.system.score}</div>
          <div class="hero__outof">out of 100</div>
        </div>
      </div>
      <div class="hero__text">
        <div class="hero__label tone--${esc(d.system.overall.tier)}">${esc(d.system.overall.label)}</div>
        <p class="hero__reason">${esc(d.system.overall.reason)}</p>
        <div class="hero__meta">
          ${brandPill(d.system.osName || 'Windows')}
          ${d.system.manufacturer ? plainPill(`${d.system.manufacturer} ${d.system.model}`.trim()) : ''}
          ${plainPill('Up ' + uptime(d.system.uptimeHours))}
        </div>
      </div>
    </div>
  </section>`;

  const notes = card('What stands out', '', `
    <div class="grid grid--wide" style="margin:0">
      <div>
        <div class="subline" style="margin-bottom:9px">Working well</div>
        <div class="notes">
          ${d.system.highlights.length
            ? d.system.highlights.map(h => `<div class="note note--good"><span class="note__dot"></span><span>${esc(h)}</span></div>`).join('')
            : emptyNote('Nothing scored highly enough to call out here.')}
        </div>
      </div>
      <div>
        <div class="subline" style="margin-bottom:9px">Worth looking at</div>
        <div class="notes">
          ${d.system.watchouts.length
            ? d.system.watchouts.map(w => `<div class="note note--warn"><span class="note__dot"></span><span>${esc(w)}</span></div>`).join('')
            : emptyNote('Nothing is holding this machine back right now.')}
        </div>
      </div>
    </div>`);

  const cpuCard = card('Processor (CPU)', pill(d.cpu.verdict), `
    <div class="headline">${esc(d.cpu.shortName || d.cpu.name || 'Unknown')}</div>
    <div class="subline">${esc(d.cpu.family || d.cpu.vendor || '')}</div>
    ${bar('Currently using', pct(d.cpu.loadPercent), d.cpu.loadPercent, loadTier(d.cpu.loadPercent))}
    ${stats([
      stat('Cores', `${d.cpu.physicalCores} cores · ${d.cpu.logicalProcessors} threads`),
      stat('Speed now', ghz(d.cpu.currentClockGhz)),
      stat('Temperature', `${temp(d.cpu.tempC)} <span class="pill pill--${esc(d.cpu.thermal.tier)}">${esc(d.cpu.thermal.label)}</span>`, { raw: true })
    ])}`);

  const gpuCard = gpu
    ? card('Graphics (GPU)', pill(gpu.verdict), `
        <div class="headline">${esc(gpu.name)}</div>
        <div class="subline">${esc(gpu.vendor)}${gpu.isIntegrated ? ' · built into the processor' : ''}</div>
        ${bar('Currently using', pct(gpu.loadPercent), gpu.loadPercent, loadTier(gpu.loadPercent))}
        ${stats([
          stat('Video memory', has(gpu.vramTotalGb) ? gb(gpu.vramTotalGb, 0) : DASH),
          stat('Memory in use', has(gpu.vramUsedGb) ? `${gb(gpu.vramUsedGb, 1)} (${pct(gpu.vramPercent)})` : DASH),
          stat('Temperature', `${temp(gpu.tempC)} <span class="pill pill--${esc(gpu.thermal.tier)}">${esc(gpu.thermal.label)}</span>`, { raw: true })
        ])}`)
    : card('Graphics (GPU)', '', emptyNote('No display adapter was reported.'));

  const memCard = card('Memory (RAM)', pill(d.memory.verdict), `
    <div class="headline">${gb(d.memory.totalGb, 0)} ${esc(d.memory.type || '')}</div>
    <div class="subline">${d.memory.slotsUsed} of ${d.memory.slotsTotal} slot${d.memory.slotsTotal === 1 ? '' : 's'} filled${has(d.memory.speedMhz) ? ` · ${d.memory.speedMhz} MHz` : ''}</div>
    ${bar('In use right now', has(d.memory.usedGb) ? `${gb(d.memory.usedGb)} of ${gb(d.memory.totalGb, 0)}` : DASH,
          d.memory.loadPercent, fullTier(d.memory.loadPercent))}
    ${collapsible('ov-ram', `Show what's in each slot`, d.memory.sticks.length
      ? stats(d.memory.sticks.map(s => stat(
          s.slot || 'Slot',
          `${gb(s.capacityGb, 0)} ${esc(s.type || '')} ${s.manufacturer ? '· ' + esc(s.manufacturer) : ''}`,
          { raw: true })))
      : emptyNote('Windows did not report the individual sticks.'))}`);

  const boardCard = card('Motherboard', '', `
    <div class="headline headline--sm">${esc(d.board.manufacturer || 'Unknown')}</div>
    <div class="subline">${esc(d.board.product || '')}</div>
    ${stats([
      d.board.biosVersion ? stat('BIOS version', d.board.biosVersion) : null,
      d.board.biosDate ? stat('BIOS date', d.board.biosDate) : null,
      has(d.board.tempC) ? stat('Board temperature', temp(d.board.tempC)) : null,
      stat('Serial number', priv(d.board.serial), { raw: true, mono: true })
    ])}`);

  const netCard = card('Internet', pill(net.verdict), `
    <div class="headline headline--sm">${esc(net.connectionType)}${net.wifi ? ' · ' : ''}${net.wifi ? priv(net.wifi.ssid) : ''}</div>
    <div class="subline">${net.online ? 'Connected to the internet' : 'No internet connection detected'}</div>
    ${net.wifi
      ? bar('Signal strength', `${net.wifi.signalPercent}% · ${esc(net.wifi.signalLabel)}`,
            net.wifi.signalPercent, net.wifi.verdict.tier)
      : ''}
    ${stats([
      stat('Download', rate(net.downloadKbps)),
      stat('Upload', rate(net.uploadKbps)),
      stat('VPN', net.vpn.active ? esc(net.vpn.name || 'Active') : 'Not in use',
        { raw: true, dim: !net.vpn.active })
    ])}`);

  const driveRows = d.drives.map(dr => {
    const total = dr.volumes.reduce((a, v) => a + v.totalGb, 0);
    const free = dr.volumes.reduce((a, v) => a + v.freeGb, 0);
    const used = total > 0 ? ((total - free) / total) * 100 : null;
    return `<div class="bar-block">
      <div class="bar-label">
        <span>${esc(dr.model)} ${dr.isSystemDrive ? brandPill('Windows') : ''}</span>
        <strong>${total > 0 ? `${gb(free)} free of ${gb(total)}` : gb(dr.sizeGb)}</strong>
      </div>
      <div class="bar"><div class="bar__fill bar__fill--${fullTier(used)}" style="width:${has(used) ? used.toFixed(1) : 0}%"></div></div>
      <div class="bar-label" style="margin-top:4px">
        <span>${esc(dr.kind)}</span>
        <span>${has(dr.tempC) ? temp(dr.tempC) : ''}${has(dr.healthPercent) ? ` · ${Math.round(dr.healthPercent)}% life left` : ''}</span>
      </div>
    </div>`;
  }).join('');

  const drivesCard = card('Storage', plainPill(`${d.drives.length} drive${d.drives.length === 1 ? '' : 's'}`),
    d.drives.length ? driveRows : emptyNote('No drives were reported.'));

  return hero + actionsCard(d) +
    `<div class="grid grid--pair">${cpuCard}${gpuCard}</div>` +
    `<div class="grid">${memCard}${drivesCard}${netCard}</div>` +
    `<div class="grid grid--full">${notes}</div>` +
    `<div class="grid grid--wide">${boardCard}${systemCard(d)}</div>`;
}

/** Copy Specs and the upgrade suggestions, side by side under the headline. */
function actionsCard(d) {
  const count = d.upgrades.length;

  return `<section class="card" style="margin-bottom:14px"><div class="card__body">
    <div class="btn-row">
      <button class="btn ${state.copied ? 'btn--done' : ''}" data-action="copySpecs" type="button">
        ${state.copied ? 'Copied to clipboard' : 'Copy Specs'}
      </button>
      <button class="btn" data-action="toggleUpgrades" type="button">
        ${state.showUpgrades ? 'Hide upgrade ideas' : `Upgrade ideas${count ? ` (${count})` : ''}`}
      </button>
      <span class="verdict-note" style="margin:0">
        Copy Specs puts a short summary of this machine on your clipboard, ready to paste
        wherever you're asking for help. No serial numbers or addresses go with it.
      </span>
    </div>
    ${state.showUpgrades ? upgradesPanel(d) : ''}
  </div></section>`;
}

function upgradesPanel(d) {
  if (!d.upgrades.length) {
    return `<div style="margin-top:14px">
      <div class="section-label">Upgrade ideas</div>
      <p class="verdict-note" style="margin-top:0">
        Nothing here needs replacing. Every major part of this machine is doing its job:
        save your money.
      </p>
    </div>`;
  }

  const rows = d.upgrades.map(u => `
    <div class="upgrade upgrade--${esc(u.priority)}">
      <div class="upgrade__top">
        <span class="upgrade__part">${esc(u.part)}</span>
        <span class="upgrade__pick">${esc(u.pick)}</span>
      </div>
      <div class="upgrade__why">${esc(u.why)}</div>
      <div class="upgrade__now">Right now: ${esc(u.current)}</div>
    </div>`).join('');

  return `<div style="margin-top:14px">
    <div class="section-label">Upgrade ideas: best value first</div>
    ${rows}
    <p class="verdict-note">
      These are the cheapest parts that would actually make a difference to this machine,
      in the order worth buying them. Prices change constantly, so BBSpecs names the part
      and leaves the shopping to you.
    </p>
  </div>`;
}

function systemCard(d) {
  return card('This computer', '', stats([
    stat('Computer name', priv(d.system.computerName), { raw: true }),
    stat('Signed in as', priv(d.system.userName), { raw: true }),
    stat('Windows', `${d.system.osName} · ${d.system.osBuild}`),
    stat('System type', d.system.osArchitecture),
    d.system.osInstalledOn ? stat('Windows installed', d.system.osInstalledOn) : null,
    stat('Running for', uptime(d.system.uptimeHours)),
    stat('BBSpecs version', 'v' + d.version, { dim: true })
  ]));
}

/* ─────────────────────────────── tab: cpu ────────────────────────────── */

function renderCpu(d) {
  const c = d.cpu;

  const head = card('Processor (CPU)', pill(c.verdict), `
    <div class="headline">${esc(c.name || 'Unknown processor')}</div>
    <div class="subline">${esc(c.family || c.vendor || '')}${c.releaseYear ? ` · released around ${c.releaseYear}` : ''}</div>
    ${verdictNote(c.verdict)}`);

  const blocked = sensorHint(d);

  const live = card('Right now', '', `
    <div class="gauges">
      ${gauge('Temperature', c.tempC, '°C', c.thermal.tier, 100, blocked)}
      ${gauge('In use', c.loadPercent, '%', loadTier(c.loadPercent))}
      ${gauge('Speed', c.currentClockGhz, 'GHz', 'good',
              Math.max(c.maxClockGhz || 0, c.baseClockGhz || 0, 5), blocked)}
    </div>
    ${verdictNote(c.thermal)}`);

  const specs = card('Specifications', '', stats([
    stat('Model', c.shortName || DASH),
    stat('Maker', c.vendor || DASH),
    stat('Cores', `${c.physicalCores} physical`),
    stat('Logical processors', `${c.logicalProcessors} threads`),
    stat('Base speed', ghz(c.baseClockGhz)),
    stat('Speed right now', ghz(c.currentClockGhz)),
    stat('Fastest core now', ghz(c.maxClockGhz)),
    has(c.powerW) ? stat('Power draw', watts(c.powerW)) : null,
    has(c.l2CacheKb) ? stat('L2 cache', (c.l2CacheKb / 1024).toFixed(1) + ' MB') : null,
    has(c.l3CacheKb) ? stat('L3 cache', (c.l3CacheKb / 1024).toFixed(1) + ' MB') : null,
    c.socket ? stat('Socket', c.socket) : null
  ]));

  const explain = card('What this means', '', collapsible('cpu-explain', 'What the numbers mean', `
    <div class="notes">
      <div class="note note--good"><span class="note__dot"></span>
        <span><strong>Cores</strong> are separate workers inside the chip. More cores means more things can happen at once.</span></div>
      <div class="note note--good"><span class="note__dot"></span>
        <span><strong>Threads</strong> (logical processors) let each core juggle two jobs, so Windows sees ${c.logicalProcessors} workers rather than ${c.physicalCores}.</span></div>
      <div class="note note--good"><span class="note__dot"></span>
        <span><strong>GHz</strong> is how fast each core runs. It rises and falls on its own depending on what you're doing: that's normal.</span></div>
      <div class="note note--warn"><span class="note__dot"></span>
        <span><strong>Temperature</strong> under 85°C is fine. Consistently above that, and it's worth cleaning the dust out of your cooler.</span></div>
    </div>`));

  const coreCards = c.cores.length
    ? `<div class="cores">${c.cores.map(core => `
        <div class="core">
          <div class="core__top">
            <span class="core__name">${esc(core.label)}</span>
            <span class="core__dot core__dot--${esc(core.health)}"></span>
          </div>
          <div class="core__temp tone--${core.health === 'good' ? 'excellent' : core.health === 'warm' ? 'ok' : core.health === 'hot' ? 'poor' : 'unknown'}">${temp(core.tempC)}</div>
          <div class="core__clock">${ghz(core.clockGhz)} · ${pct(core.loadPercent)}</div>
          <div class="bar"><div class="bar__fill bar__fill--${loadTier(core.loadPercent)}" style="width:${has(core.loadPercent) ? Math.min(100, core.loadPercent).toFixed(1) : 0}%"></div></div>
        </div>`).join('')}</div>`
    : emptyNote(d.sensorsReady
        ? 'This processor does not expose per-core readings.'
        : 'Per-core readings need Administrator access.');

  const cores = card('Core health', plainPill(`${c.cores.length || c.physicalCores} core${c.physicalCores === 1 ? '' : 's'}`),
    coreCards);

  return `<div class="grid grid--wide">${head}${live}</div>` +
    `<div class="grid grid--wide">${specs}${explain}</div>` +
    `<div class="grid grid--full">${cores}</div>`;
}

/* ─────────────────────────────── tab: gpu ────────────────────────────── */

function renderGpu(d) {
  if (!d.gpus.length) {
    return `<div class="grid grid--full">${card('Graphics', '', emptyNote('No display adapter was reported by Windows.'))}</div>`;
  }

  return d.gpus.map(g => {
    const head = card('Graphics card', pill(g.verdict), `
      <div class="headline">${esc(g.name)}</div>
      <div class="subline">${esc(g.vendor)}${g.isIntegrated ? ' · built into the processor' : ''}${g.releaseYear ? ` · released around ${g.releaseYear}` : ''}</div>
      ${verdictNote(g.verdict)}`);

    const live = card('Right now', '', `
      <div class="gauges">
        ${gauge('Temperature', g.tempC, '°C', g.thermal.tier, 100, sensorHint(d))}
        ${gauge('In use', g.loadPercent, '%', loadTier(g.loadPercent), 100, sensorHint(d))}
        ${has(g.fanPercent) || has(g.fanRpm)
          ? gauge('Fan', has(g.fanPercent) ? g.fanPercent : null, '%', 'good', 100,
                  has(g.fanRpm) ? Math.round(g.fanRpm) + ' RPM' : sensorHint(d))
          : gauge('Power', g.powerW, 'W', 'good', 450, sensorHint(d))}
      </div>
      ${bar('Video memory in use',
            has(g.vramUsedGb) ? `${gb(g.vramUsedGb)} of ${gb(g.vramTotalGb, 0)}` : DASH,
            g.vramPercent, fullTier(g.vramPercent))}
      ${verdictNote(g.thermal)}`);

    const specs = card('Specifications', '', stats([
      stat('Model', g.name),
      stat('Maker', g.vendor),
      stat('Video memory', has(g.vramTotalGb) ? gb(g.vramTotalGb, 0) : DASH),
      stat('Driver version', g.driverVersionFriendly
        ? `${esc(g.driverVersionFriendly)} <span class="stat__v--dim">(${esc(g.driverVersion)})</span>`
        : (g.driverVersion || DASH), { raw: !!g.driverVersionFriendly }),
      g.driverDate ? stat('Driver date', g.driverDate) : null,
      stat('Location', g.location || DASH),
      g.resolution ? stat('Display output', `${g.resolution}${has(g.refreshHz) ? ` at ${Math.round(g.refreshHz)} Hz` : ''}`) : null
    ]));

    const perf = card('Live readings', '', stats([
      stat('Core speed', mhz(g.coreClockMhz)),
      stat('Memory speed', mhz(g.memoryClockMhz)),
      stat('Temperature', temp(g.tempC)),
      has(g.hotspotTempC) ? stat('Hot spot', temp(g.hotspotTempC)) : null,
      has(g.fanRpm) ? stat('Fan speed', Math.round(g.fanRpm) + ' RPM') : null,
      has(g.fanPercent) ? stat('Fan output', pct(g.fanPercent)) : null,
      has(g.powerW) ? stat('Power draw', watts(g.powerW)) : null
    ]) + `<p class="verdict-note">Numbers sitting near zero mean the card is idle. They climb the moment you open a game or a video.</p>`);

    return `<div class="grid grid--wide">${head}${live}</div>` +
      `<div class="grid grid--wide">${specs}${perf}</div>`;
  }).join('');
}

/* ────────────────────────────── tab: drives ──────────────────────────── */

function renderDisks(d) {
  if (!d.drives.length) {
    return `<div class="grid grid--full">${card('Disks', '', emptyNote('No disks were reported.'))}</div>`;
  }

  // One drive, one card. Splitting a single drive across four panels made a
  // four-drive machine look like sixteen unrelated things.
  return d.drives.map(dr => {
    const total = dr.volumes.reduce((a, v) => a + v.totalGb, 0);
    const free = dr.volumes.reduce((a, v) => a + v.freeGb, 0);
    const usedPct = total > 0 ? ((total - free) / total) * 100 : null;
    const freePct = total > 0 ? (free / total) * 100 : null;

    const title = dr.isSystemDrive ? 'Main drive (Windows)'
                : dr.isRemovable ? 'External drive'
                : 'Drive';

    const dials = `<div class="gauges">
      ${gauge('Temperature', dr.tempC, '°C', driveTempTier(dr.tempC), 80,
              d.sensorsReady ? 'Not reported by this drive' : 'Needs full access')}
      ${gauge('Life left', dr.healthPercent, '%', lifeTier(dr.healthPercent), 100,
              d.sensorsReady ? "This drive doesn't report wear" : 'Needs full access')}
      ${gauge('Space free', freePct, '%', fullTier(usedPct), 100, 'No volumes')}
    </div>`;

    const details = stats([
      stat('Type', dr.kind),
      stat('Connection', dr.busType || DASH),
      stat('Capacity', gb(dr.sizeGb, 0)),
      dr.healthStatus ? stat('Reported health', dr.healthStatus) : null,
      dr.firmware ? stat('Firmware', dr.firmware) : null,
      has(dr.dataWrittenTb) ? stat('Total data written', dr.dataWrittenTb.toFixed(2) + ' TB') : null,
      has(dr.dataReadTb) ? stat('Total data read', dr.dataReadTb.toFixed(2) + ' TB') : null,
      has(dr.powerOnHours) ? stat('Powered on for', Math.round(dr.powerOnHours / 24) + ' days') : null,
      stat('Serial number', priv(dr.serial), { raw: true, mono: true })
    ]);

    const volumes = dr.volumes.length
      ? dr.volumes.map(v => `
          <div class="volume volume--open" data-action="openVolume" data-path="${esc(v.letter)}"
               role="button" tabindex="0"
               title="Open ${esc(v.letter)} in your file manager">
            <div class="volume__top">
              <span class="volume__letter">${esc(v.letter)}</span>
              <span class="volume__label">${esc(v.label || 'Local Disk')}</span>
              ${v.isSystem && !/windows/i.test(v.label || '') ? brandPill('Windows') : ''}
              <span class="volume__fs">${esc(v.fileSystem || '')}</span>
              <span class="volume__open" aria-hidden="true">
                <svg viewBox="0 0 16 16" width="13" height="13">
                  <path d="M2 4.5A1.5 1.5 0 0 1 3.5 3h2.2l1.2 1.5h5.6A1.5 1.5 0 0 1 14 6v5.5A1.5 1.5 0 0 1 12.5 13h-9A1.5 1.5 0 0 1 2 11.5v-7Z"
                        fill="none" stroke="currentColor" stroke-width="1.3" stroke-linejoin="round"/>
                </svg>
                Open
              </span>
            </div>
            <div class="bar"><div class="bar__fill bar__fill--${fullTier(v.usedPercent)}" style="width:${v.usedPercent.toFixed(1)}%"></div></div>
            <div class="bar-label" style="margin-top:4px">
              <span>${gb(v.usedGb)} used</span>
              <strong>${gb(v.freeGb)} free of ${gb(v.totalGb)}</strong>
            </div>
          </div>`).join('')
      : emptyNote(state.platform === 'windows'
          ? 'This disk has no formatted volumes, so Windows cannot store anything on it yet.'
          : 'This disk has no mounted filesystems.');

    // A brand new disk, or one with space left over, is the one case where the
    // useful next step is somewhere else entirely. Say so and open the door,
    // rather than leaving a card that reports a problem and offers nothing.
    //
    // Measured against the partition table, never against the volumes below.
    // A normal Windows disk keeps an EFI, an MSR and a recovery partition that
    // carry no drive letter, so counting volumes reported about 26 GB missing
    // on a perfectly healthy drive. Anything that cannot be measured properly
    // says nothing at all, because a storage tool that invents free space is
    // worse than one that stays quiet.
    const unallocatedGb = has(dr.partitionedGb) ? dr.sizeGb - dr.partitionedGb : 0;
    const unallocated = dr.sizeGb > 0 && unallocatedGb > Math.max(8, dr.sizeGb * 0.05);

    const partitionHelp = (!dr.volumes.length || unallocated)
      ? `<div class="claim">
          <div class="claim__text">
            ${!dr.volumes.length
              ? 'This disk is not partitioned yet. Nothing can be saved to it until it is.'
              : `About ${esc(gb(unallocatedGb, 0))} of this disk is unallocated and going unused.`}
          </div>
          <button class="btn" data-action="diskManagement" type="button">
            ${state.platform === 'windows' ? 'Open Disk Management' : 'Open Disks'}
          </button>
        </div>`
      : '';

    return `<section class="card" style="margin-bottom:14px">
      <div class="card__head">
        <h2 class="card__title">${esc(title)}</h2>
        <div class="card__sub">${pill(dr.verdict)}</div>
      </div>
      <div class="card__body">
        <div class="drive__top">
          <div class="drive__facts">
            <div class="headline">${esc(dr.model)}</div>
            <div class="subline">${esc(dr.kind)} · ${gb(dr.sizeGb, 0)}${dr.busType ? ` · ${esc(dr.busType)}` : ''}</div>
            ${bar('Space used', total > 0 ? `${gb(total - free)} of ${gb(total)}` : DASH, usedPct, fullTier(usedPct))}
            ${verdictNote(dr.space)}
            ${verdictNote(dr.verdict)}
          </div>
          <div class="drive__dials">${dials}</div>
        </div>

        <div class="drive__split">
          <div>
            <div class="section-label">Details</div>
            ${details}
          </div>
          <div>
            <div class="section-label">Volumes${dr.volumes.length > 1 ? ` (${dr.volumes.length})` : ''}</div>
            ${partitionHelp}
            ${volumes}
          </div>
        </div>
      </div>
    </section>`;
  }).join('');
}

/** Why a live reading is missing, in the few words a dial has room for. */
function sensorHint(d) {
  if (d.sensorsReady) return 'Not reported';
  return d.isAdmin ? 'Sensor driver blocked' : `Needs ${state.roleName}`;
}

function driveTempTier(t) {
  if (!has(t)) return 'unknown';
  if (t < 45) return 'excellent';
  if (t < 60) return 'good';
  if (t < 70) return 'ok';
  return 'poor';
}

function lifeTier(v) {
  if (!has(v)) return 'unknown';
  if (v >= 80) return 'excellent';
  if (v >= 50) return 'good';
  if (v >= 20) return 'aging';
  return 'poor';
}

/* ───────────────────────────── tab: internet ─────────────────────────── */

function renderInternet(d) {
  const net = d.network;
  const p = net.primary;

  const head = card('Connection', pill(net.verdict), `
    <div class="headline">${esc(net.connectionType)}</div>
    <div class="subline">${p ? esc(p.description) : 'No active connection'}</div>
    ${verdictNote(net.verdict)}
    ${stats([
      stat('Status', net.online ? 'Online' : 'Offline'),
      has(net.pingMs) ? stat('Response time', net.pingMs + ' ms') : null,
      p && has(p.linkSpeedMbps) ? stat('Link speed', p.linkSpeedMbps >= 1000
        ? (p.linkSpeedMbps / 1000).toFixed(1) + ' Gbps'
        : Math.round(p.linkSpeedMbps) + ' Mbps') : null
    ])}`);

  const traffic = card('Traffic right now', '', `
    <div class="gauges">
      ${gauge('Download', net.downloadKbps, 'KB/s', 'good', 12500)}
      ${gauge('Upload', net.uploadKbps, 'KB/s', 'good', 12500)}
    </div>
    ${stats([
      stat('Downloading', rate(net.downloadKbps)),
      stat('Uploading', rate(net.uploadKbps))
    ])}
    <p class="verdict-note">This is how much data is moving over your connection this second: not your maximum internet speed.</p>`);

  const wifi = net.wifi
    ? card('Wi-Fi', pill(net.wifi.verdict), `
        <div class="headline headline--sm">${priv(net.wifi.ssid)}</div>
        <div class="subline">${esc(net.wifi.radioType || 'Wi-Fi')}${net.wifi.band ? ` · ${esc(net.wifi.band)}` : ''}</div>
        <div style="display:flex;align-items:center;gap:12px;margin:12px 0 10px">
          ${signalBars(net.wifi.signalPercent, net.wifi.verdict.tier)}
          <span class="tone--${esc(net.wifi.verdict.tier)}" style="font-weight:700">${net.wifi.signalPercent}% · ${esc(net.wifi.signalLabel)}</span>
        </div>
        ${stats([
          stat('Network name', priv(net.wifi.ssid), { raw: true }),
          stat('Standard', net.wifi.radioType || DASH),
          stat('Band', net.wifi.band || DASH),
          has(net.wifi.channel) ? stat('Channel', String(net.wifi.channel)) : null,
          stat('Security', net.wifi.security || DASH),
          stat('Encryption', net.wifi.cipher || DASH),
          has(net.wifi.receiveMbps) ? stat('Receive rate', Math.round(net.wifi.receiveMbps) + ' Mbps') : null,
          has(net.wifi.transmitMbps) ? stat('Send rate', Math.round(net.wifi.transmitMbps) + ' Mbps') : null,
          stat('Router address', priv(net.wifi.bssid), { raw: true, mono: true })
        ])}
        ${verdictNote(net.wifi.verdict)}`)
    : card('Wi-Fi', plainPill('Not in use'), emptyNote(
        net.connectionType === 'Ethernet'
          ? 'You are connected with a network cable, so there is no wireless link to report.'
          : 'No wireless network is currently connected.'));

  const vpn = card('VPN', net.vpn.active ? pill({ tier: 'good', label: 'Active' }) : plainPill('Off'), `
    <div class="headline headline--sm">${net.vpn.active ? esc(net.vpn.name) : 'No VPN detected'}</div>
    <p class="verdict-note">${esc(net.vpn.summary)}</p>
    <p class="verdict-note">A VPN sends your internet traffic through another company's server first. It hides what you're doing from your internet provider, and can make things a little slower.</p>`);

  const adapterCard = p
    ? card('Your network details', plainPill(p.kind), stats([
        stat('Adapter', p.description),
        stat('IP address', priv(p.ipv4), { raw: true, mono: true }),
        stat('Subnet mask', priv(p.subnetMask), { raw: true, mono: true }),
        stat('Router (gateway)', priv(p.gateway), { raw: true, mono: true }),
        stat('DNS servers', p.dns.length ? priv(p.dns.join(', ')) : DASH, { raw: true, mono: true }),
        stat('IPv6 address', priv(p.ipv6), { raw: true, mono: true }),
        stat('Physical address', priv(p.mac), { raw: true, mono: true }),
        stat('Address assigned by', p.dhcpEnabled ? 'Your router (automatic)' : 'Set manually')
      ]))
    : card('Your network details', '', emptyNote('No active adapter was found.'));

  const publicCard = card('Public address', '', publicIpBody(d));

  const others = net.adapters.filter(a => !p || a.name !== p.name);
  const allAdapters = card('All network adapters', plainPill(`${net.adapters.length} found`),
    collapsible('net-adapters', `Show every adapter Windows knows about`,
      others.length
        ? stats(others.map(a => stat(
            a.name,
            `${esc(a.kind)}${a.isUp ? '' : ' · off'}${a.ipv4 ? ' · ' + (state.privacy ? '••••••' : esc(a.ipv4)) : ''}`,
            { raw: true, dim: !a.isUp })))
        : emptyNote('No other adapters.')));

  return `<div class="grid grid--wide">${head}${traffic}</div>` +
    `<div class="grid grid--wide">${wifi}${adapterCard}</div>` +
    `<div class="grid grid--wide">${vpn}${publicCard}</div>` +
    `<div class="grid grid--full">${allAdapters}</div>`;
}

function publicIpBody(d) {
  // The host owns this now: it re-looks-up on its own when the connection
  // changes, so the page follows its state rather than keeping its own.
  const live = d.network.publicIp;
  const busy = d.network.publicIpRefreshing || state.publicIpLoading;

  if (busy) {
    return `<div class="headline headline--sm">Looking it up…</div>
      <p class="verdict-note">Asking a public lookup service what address the internet sees.</p>`;
  }

  if (!live) {
    return `<div class="headline headline--sm">Hidden</div>
      <p class="verdict-note">This is the address the rest of the internet sees when you connect: it comes from your internet provider, not from your PC. BBSpecs only looks it up when you ask.</p>
      <button class="btn" data-action="publicIp" type="button" style="margin-top:11px">Look up my public address</button>`;
  }

  if (live.error) {
    return `<div class="headline headline--sm tone--aging">Couldn't look it up</div>
      <p class="verdict-note">${esc(live.error)}</p>
      <button class="btn" data-action="publicIp" type="button" style="margin-top:11px">Try again</button>`;
  }

  const where = [live.city, live.region, live.country].filter(Boolean).join(', ');
  const vpn = d.network.vpn.active;

  return stats([
    stat('Public IP address', priv(live.ip), { raw: true, mono: true }),
    stat('Internet provider', priv(live.isp), { raw: true }),
    where ? stat('Approximate location', priv(where), { raw: true }) : null
  ]) + `<p class="verdict-note">${vpn
    ? `You're on a VPN, so this is your VPN server's address and location: not yours. That's exactly what a VPN is for.`
    : `The location is a rough guess based on your provider: it's usually a nearby city rather than where you actually are.`}</p>
  <div class="btn-row" style="margin-top:11px">
    <button class="btn" data-action="publicIp" type="button">Refresh</button>
    <span class="verdict-note" style="margin:0">Updates on its own when your connection changes.</span>
  </div>`;
}

/* ─────────────────────────────── rendering ───────────────────────────── */

const RENDERERS = {
  overview: renderOverview,
  cpu: renderCpu,
  gpu: renderGpu,
  memory: renderMemory,
  disks: renderDisks,
  drivers: renderDrivers,
  internet: renderInternet
};

function adminBanner(d) {
  if (d.isAdmin && d.sensorsReady) return '';

  let message;
  if (!d.isAdmin) {
    message = state.platform === 'windows'
      ? `BBSpecs is not running as Administrator. Windows only lets elevated programs read temperature sensors, fan speeds and drive health.`
      : `BBSpecs is not running as root. Temperatures still work, but the memory slot layout and drive health need root access.`;
  } else {
    message = d.sensorNote || 'The sensor driver could not be started on this machine.';
  }

  // A fix the user can act on beats an explanation they can't: and doing it for
  // them beats sending them to a download page.
  let action = '';

  if (d.sensorHelpUrl && state.canInstallDriver) {
    if (state.driverInstall === 'working') {
      action = `<button class="btn" type="button" disabled style="margin-top:10px">Installing…</button>`;
    } else if (state.driverInstall === 'done') {
      action = `<button class="btn btn--done" type="button" disabled style="margin-top:10px">Installed</button>`;
    } else {
      action = `<button class="btn" data-action="installDriver" type="button" style="margin-top:10px">Install it for me</button>`;
      if (state.driverInstall === 'failed') {
        action += `<button class="btn" data-action="openHelp" data-url="${esc(d.sensorHelpUrl)}" type="button" style="margin-top:10px;margin-left:8px">Do it myself</button>`;
      }
    }
  } else if (d.sensorHelpUrl) {
    action = `<button class="btn" data-action="openHelp" data-url="${esc(d.sensorHelpUrl)}" type="button" style="margin-top:10px">Get the driver</button>`;
  } else if (!d.isAdmin && state.canRelaunch) {
    action = `<button class="btn" data-action="relaunch" type="button" style="margin-top:10px">Restart with full access</button>`;
  }

  const installNote = state.driverMessage
    ? `<span style="display:block;margin-top:7px">${esc(state.driverMessage)}</span>`
    : '';

  const failed = state.relaunchFailed
    ? `<span style="display:block;margin-top:7px">That didn't work: you can start BBSpecs as ${esc(state.roleName)} yourself instead.</span>`
    : '';

  return `<div class="banner">
    <svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true" style="flex:0 0 auto;margin-top:1px">
      <path d="M12 3 4 6.5V12c0 4.6 3.4 8.3 8 9 4.6-.7 8-4.4 8-9V6.5L12 3Z"
            fill="none" stroke="currentColor" stroke-width="1.6"/>
      <path d="M12 8v5M12 15.5v.5" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"/>
    </svg>
    <div>
      <strong>${d.isAdmin ? 'Sensors are unavailable' : 'Some readings are unavailable'}</strong>
      <span>${esc(message)}</span>
      ${failed}
      ${installNote}
      ${action}
    </div>
  </div>`;
}

const view = document.getElementById('view');

function render() {
  if (!state.snap) return;

  let html;
  try {
    html = adminBanner(state.snap) + RENDERERS[state.tab](state.snap);
  } catch (err) {
    // Better to show what broke than to sit on the loading screen forever.
    host.send('uiError', { detail: `render(${state.tab}): ${err && err.stack || err}` });
    html = `<div class="banner">
      <div>
        <strong>This panel couldn't be drawn</strong>
        <span>${esc(String(err && err.message || err))}</span>
      </div>
    </div>`;
  }

  const next = document.createElement('div');
  next.innerHTML = html;
  patchChildren(view, next);
}

/* ───────────────────────────── DOM patching ──────────────────────────── */

function patchNode(oldNode, newNode) {
  if (oldNode.nodeType !== newNode.nodeType || oldNode.nodeName !== newNode.nodeName) {
    oldNode.replaceWith(newNode.cloneNode(true));
    return;
  }

  if (oldNode.nodeType === Node.TEXT_NODE) {
    if (oldNode.nodeValue !== newNode.nodeValue) oldNode.nodeValue = newNode.nodeValue;
    return;
  }

  if (oldNode.nodeType !== Node.ELEMENT_NODE) return;

  // Attributes: drop the ones that vanished, then apply the rest.
  for (const attr of Array.from(oldNode.attributes)) {
    if (!newNode.hasAttribute(attr.name)) oldNode.removeAttribute(attr.name);
  }
  for (const attr of Array.from(newNode.attributes)) {
    if (oldNode.getAttribute(attr.name) !== attr.value) oldNode.setAttribute(attr.name, attr.value);
  }

  patchChildren(oldNode, newNode);
}

function patchChildren(parent, newParent) {
  const oldKids = Array.from(parent.childNodes);
  const newKids = Array.from(newParent.childNodes);
  const count = Math.max(oldKids.length, newKids.length);

  for (let i = 0; i < count; i++) {
    const oldKid = oldKids[i];
    const newKid = newKids[i];
    if (oldKid && newKid) patchNode(oldKid, newKid);
    else if (newKid) parent.appendChild(newKid.cloneNode(true));
    else if (oldKid) parent.removeChild(oldKid);
  }
}

/* ──────────────────────────── host messages ──────────────────────────── */

host.listen(msg => {
  if (!msg || !msg.type) return;

  if (msg.type === 'snapshot') {
    state.snap = msg.data;
    document.getElementById('version').textContent = 'v' + msg.data.version;
    render();
  } else if (msg.type === 'hello') {
    state.platform = msg.platform || 'windows';
    state.roleName = msg.roleName || 'Administrator';
    state.canRelaunch = !!msg.canRelaunch;
    state.canInstallDriver = !!msg.canInstallDriver;
    document.getElementById('version').textContent = 'v' + msg.version;

    state.canRunOnStart = msg.canRunOnStart !== false;
    applyRunOnStart(!!msg.runOnStart);

    if (msg.settings) {
      applyTheme(THEMES.includes(msg.settings.theme) ? msg.settings.theme : 'bossx');
      applyPrivacy(msg.settings.privacy !== false);
      applyNotifications(!!msg.settings.notifications);

      state.welcome = !msg.settings.seenWelcome;
      renderWelcome();

      render();
    }
  } else if (msg.type === 'publicIp') {
    state.publicIpLoading = false;
    render();
  } else if (msg.type === 'driverInstall') {
    state.driverInstall = msg.state;
    state.driverMessage = msg.detail || '';
    render();
  } else if (msg.type === 'runOnStart') {
    applyRunOnStart(!!msg.enabled);
  } else if (msg.type === 'updateAvailable') {
    state.update = { version: msg.version, notes: msg.notes || '', sizeBytes: msg.sizeBytes || 0 };
    state.updateStage = 'offered';
    renderUpdate();
  } else if (msg.type === 'updateNone') {
    state.update = { version: msg.version, notes: '', sizeBytes: 0 };
    // Being unable to ask is not the same as having nothing to install, and
    // saying "you are up to date" when the check failed is how somebody ends
    // up several releases behind believing they are current.
    state.updateStage = msg.supported === false ? 'unsupported'
                      : msg.reachable === false ? 'unreachable'
                      : 'current';
    renderUpdate();
  } else if (msg.type === 'updateReady') {
    state.updateStage = 'ready';
    renderUpdate();
  } else if (msg.type === 'updateFailed') {
    state.updateStage = 'failed';
    renderUpdate();
  } else if (msg.type === 'relaunchFailed') {
    state.relaunchFailed = true;
    render();
  }
});

/* ──────────────────────────── update prompt ──────────────────────────── */

const updateLayer = document.getElementById('updateLayer');

function renderUpdate() {
  if (!state.update) {
    updateLayer.hidden = true;
    updateLayer.innerHTML = '';
    return;
  }

  const u = state.update;
  const size = u.sizeBytes > 0 ? ` (${(u.sizeBytes / 1048576).toFixed(0)} MB)` : '';

  let body;
  let buttons;

  if (state.updateStage === 'unreachable') {
    body = `<p class="dialog__text">BBSpecs could not reach GitHub, so it does not know whether
            there is a newer version. Your connection or GitHub itself is the likely reason.</p>`;
    buttons = `<button class="btn" data-action="retryCheck" type="button">Retry</button>`;
  } else if (state.updateStage === 'unsupported') {
    body = `<p class="dialog__text">This copy was not built as a single downloadable file, so it
            cannot replace itself. Updating means downloading the new one from GitHub.</p>`;
    buttons = '';
  } else if (state.updateStage === 'current') {
    body = `<p class="dialog__text">You already have the newest version.</p>`;
    buttons = `<button class="btn" data-action="retryCheck" type="button">Retry</button>`;
  } else if (state.updateStage === 'installing') {
    body = `<p class="dialog__text">Downloading version ${esc(u.version)}${size}. This takes a moment.</p>`;
    buttons = `<button class="btn" type="button" disabled>Installing…</button>`;
  } else if (state.updateStage === 'ready') {
    body = `<p class="dialog__text">Version ${esc(u.version)} is installed. BBSpecs needs to restart to use it.</p>`;
    buttons = `<button class="btn" data-action="restartUpdate" type="button">Restart now</button>
               <button class="btn btn--quiet" data-action="dismissUpdate" type="button">Later</button>`;
  } else if (state.updateStage === 'failed') {
    body = `<p class="dialog__text">The download did not finish, so nothing was changed.
            Your current version still works. Try again, or download it yourself from GitHub.</p>`;
    buttons = `<button class="btn" data-action="installUpdate" type="button">Retry</button>`;
  } else {
    body = `<p class="dialog__text">Version ${esc(u.version)} is available${size}.</p>
            ${u.notes ? `<p class="dialog__notes">${esc(u.notes)}</p>` : ''}
            <p class="dialog__fine">BBSpecs will replace itself and restart. Nothing else on your computer changes.</p>`;
    buttons = `<button class="btn" data-action="installUpdate" type="button">Install</button>
               <button class="btn btn--quiet" data-action="dismissUpdate" type="button">Later</button>`;
  }

  // The version belongs in the box: "up to date" means nothing on its own if
  // you cannot see which version it is talking about.
  const running = (state.snap && state.snap.version) || '';

  updateLayer.innerHTML = `<div class="dialog" role="dialog" aria-modal="true" aria-labelledby="updateTitle">
      <button class="dialog__close" data-action="dismissUpdate" type="button" aria-label="Close">
        <svg viewBox="0 0 16 16" width="13" height="13" aria-hidden="true">
          <path d="M4 4l8 8M12 4l-8 8" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/>
        </svg>
      </button>
      <h2 class="dialog__title" id="updateTitle">
        ${state.updateStage === 'current' ? 'BBSpecs is up to date'
          : state.updateStage === 'unreachable' ? 'Could not check'
          : state.updateStage === 'unsupported' ? 'This copy cannot update itself'
          : 'Update available'}
      </h2>
      ${running ? `<p class="dialog__version">You are running v${esc(running)}</p>` : ''}
      ${body}
      ${buttons ? `<div class="dialog__actions">${buttons}</div>` : ''}
    </div>`;
  updateLayer.hidden = false;
}

updateLayer.addEventListener('click', e => {
  const button = e.target.closest('[data-action]');
  if (!button) return;

  switch (button.dataset.action) {
    case 'installUpdate':
      state.updateStage = 'installing';
      renderUpdate();
      host.send('installUpdate');
      break;

    case 'restartUpdate':
      host.send('restartForUpdate');
      break;

    case 'retryCheck':
      state.update = null;
      renderUpdate();
      host.send('checkUpdates');
      break;

    case 'dismissUpdate':
      // Gone until the app is opened again, which is what "Later" should mean.
      state.update = null;
      renderUpdate();
      break;
  }
});

/* ─────────────────────────── first-run greeting ──────────────────────── */

const welcomeLayer = document.getElementById('welcomeLayer');

/*
  Shown once, ever. The flag goes to the host the moment it is dismissed rather
  than when the app closes, because the one thing worse than a greeting is the
  same greeting a second time.
*/
function renderWelcome() {
  if (!state.welcome) {
    welcomeLayer.hidden = true;
    welcomeLayer.innerHTML = '';
    return;
  }

  welcomeLayer.innerHTML = `<div class="dialog" role="dialog" aria-modal="true" aria-labelledby="welcomeTitle">
      <button class="dialog__close" data-action="dismissWelcome" type="button" aria-label="Close">
        <svg viewBox="0 0 16 16" width="13" height="13" aria-hidden="true">
          <path d="M4 4l8 8M12 4l-8 8" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/>
        </svg>
      </button>
      <h2 class="dialog__title" id="welcomeTitle">Thank you for installing BBSpecs</h2>
      <p class="dialog__text">
        I tried making this as beginner-friendly as I possibly could. Everything's
        in plain text, and none of this info leaves your computer.
      </p>
      <button class="dialog__link" data-action="openSite" type="button">
        View more projects
        <small>bossx.ca</small>
      </button>
      <div class="dialog__actions">
        <button class="btn" data-action="dismissWelcome" type="button">Start</button>
      </div>
    </div>`;
  welcomeLayer.hidden = false;
}

welcomeLayer.addEventListener('click', e => {
  const button = e.target.closest('[data-action]');
  if (!button) return;

  if (button.dataset.action === 'openSite') {
    // Opened in the real browser, never in here.
    host.send('openHelp', { url: 'https://bossx.ca/' });
    return;
  }

  state.welcome = false;
  renderWelcome();
  host.send('setting', { key: 'seenWelcome', value: true });
});

/* ────────────────────────────── interaction ──────────────────────────── */

document.getElementById('tabs').addEventListener('click', e => {
  const btn = e.target.closest('.tab');
  if (!btn) return;
  state.tab = btn.dataset.tab;
  for (const t of document.querySelectorAll('.tab')) {
    t.classList.toggle('is-active', t === btn);
  }
  view.scrollTop = 0;
  render();
});

/* ─────────────────────────────── the menu ────────────────────────────── */

const menu = document.getElementById('menu');
const menuButton = document.getElementById('menuButton');
const menuPanel = document.getElementById('menuPanel');

function setMenuOpen(open) {
  state.menuOpen = open;
  menuPanel.hidden = !open;
  menuButton.setAttribute('aria-expanded', String(open));

  // The flyout belongs to the menu, so it never outlives it or comes back
  // already open the next time the menu is raised.
  if (!open) setThemeMenuOpen(false);
}

menuButton.addEventListener('click', () => setMenuOpen(!state.menuOpen));

// Clicking anywhere else, or pressing Escape, closes it. Without this the panel
// sits open behind the cursor and gets in the way of the page it configures.
document.addEventListener('pointerdown', e => {
  if (state.menuOpen && !menu.contains(e.target)) setMenuOpen(false);
});
document.addEventListener('keydown', e => {
  if (e.key !== 'Escape') return;

  if (state.themeMenuOpen) {
    setThemeMenuOpen(false);
    themeToggle.focus();
  } else if (state.menuOpen) {
    setMenuOpen(false);
    menuButton.focus();
  }
});

const THEMES = ['bossx', 'astro', 'akbu'];
const THEME_NAMES = { bossx: 'BOSSx', astro: 'Astro', akbu: 'Akbu' };

/** Paints the whole app. The stylesheet does the work from one attribute. */
function applyTheme(theme) {
  state.theme = theme;
  document.documentElement.setAttribute('data-theme', theme);
  document.getElementById('themeState').textContent = THEME_NAMES[theme] || theme;

  for (const item of menuPanel.querySelectorAll('[data-action="theme"]')) {
    item.classList.toggle('is-checked', item.dataset.setTheme === theme);
  }
}

const themePanel = document.getElementById('themePanel');
const themeToggle = document.getElementById('themeToggle');

function setThemeMenuOpen(open) {
  state.themeMenuOpen = open;
  themePanel.hidden = !open;
  themeToggle.setAttribute('aria-expanded', String(open));
}

function applyPrivacy(on) {
  state.privacy = on;
  document.getElementById('privacyToggle').classList.toggle('is-on', on);
  document.getElementById('privacyState').textContent = on ? 'Hidden' : 'Shown';
}

function applyNotifications(on) {
  state.notifications = on;
  document.getElementById('notifyToggle').classList.toggle('is-on', on);
  document.getElementById('notifyState').textContent = on ? 'On' : 'Off';
}

function applyRunOnStart(on) {
  state.runOnStart = on;
  const button = document.getElementById('startupToggle');
  button.classList.toggle('is-on', on);
  button.disabled = !state.canRunOnStart;
  // "Needs admin" is about being unable to change it, so it only belongs here
  // when it is off. An autostart that is already on should say so, whether or
  // not this copy happens to be elevated enough to turn it off again.
  document.getElementById('startupState').textContent =
    on ? 'On' : (state.canRunOnStart ? 'Off' : 'Needs admin');
}

menuPanel.addEventListener('click', e => {
  const item = e.target.closest('[data-action]');
  if (!item) return;

  switch (item.dataset.action) {
    case 'themeMenu':
      setThemeMenuOpen(!state.themeMenuOpen);
      break;

    case 'theme':
      // Deliberately left open: trying all three in a row is the normal way to
      // pick one, and closing after each choice would make that tedious.
      applyTheme(item.dataset.setTheme);
      host.send('setting', { key: 'theme', value: item.dataset.setTheme });
      break;

    case 'privacy':
      applyPrivacy(!state.privacy);
      host.send('setting', { key: 'privacy', value: state.privacy });
      render();
      break;

    case 'notifications':
      applyNotifications(!state.notifications);
      host.send('setting', { key: 'notifications', value: state.notifications });
      break;

    case 'runOnStart':
      if (!state.canRunOnStart) break;
      // Says "Working" until the host reports what actually happened, rather
      // than flipping the switch and hoping the system agreed.
      document.getElementById('startupState').textContent = 'Working…';
      host.send('runOnStart', { value: !state.runOnStart });
      break;

    case 'checkUpdates':
      setMenuOpen(false);
      host.send('checkUpdates');
      break;
  }
});

// Anything marked as a button should answer the keyboard too, not just a click.
view.addEventListener('keydown', e => {
  if (e.key !== 'Enter' && e.key !== ' ') return;
  const target = e.target.closest('[role="button"][data-action]');
  if (!target) return;
  e.preventDefault();
  target.click();
});

view.addEventListener('click', e => {
  const target = e.target.closest('[data-action]');
  if (!target) return;

  const action = target.dataset.action;
  if (action === 'toggle') {
    const key = target.dataset.key;
    state.open[key] = !state.open[key];
    render();
  } else if (action === 'publicIp') {
    state.publicIpLoading = true;
    render();
    host.send('publicIp');
  } else if (action === 'diskManagement') {
    host.send('diskManagement');
  } else if (action === 'openVolume') {
    host.send('openVolume', { path: target.dataset.path });
  } else if (action === 'installDriver') {
    state.driverInstall = 'working';
    state.driverMessage = '';
    render();
    host.send('installDriver');
  } else if (action === 'openHelp') {
    host.send('openHelp', { url: target.dataset.url });
  } else if (action === 'relaunch') {
    state.relaunchFailed = false;
    host.send('relaunchElevated');
  } else if (action === 'toggleUpgrades') {
    state.showUpgrades = !state.showUpgrades;
    render();
  } else if (action === 'copySpecs') {
    copySpecs();
  }
});

/**
 * Puts the machine summary on the clipboard. The async Clipboard API is the
 * right way and works in both webviews; the textarea fallback covers the case
 * where the page doesn't have clipboard permission.
 */
async function copySpecs() {
  const text = (state.snap && state.snap.specsText) || '';
  if (!text) return;

  let ok = false;
  try {
    await navigator.clipboard.writeText(text);
    ok = true;
  } catch (_) {
    const scratch = document.createElement('textarea');
    scratch.value = text;
    // The page sets user-select: none, which would stop select() working here.
    scratch.style.cssText = 'position:fixed;top:-1000px;opacity:0;user-select:text';
    document.body.appendChild(scratch);
    scratch.select();
    try { ok = document.execCommand('copy'); } catch (_) { ok = false; }
    document.body.removeChild(scratch);
  }

  if (!ok) {
    host.send('uiError', { detail: 'Copy Specs: the clipboard refused the write.' });
    return;
  }

  state.copied = true;
  render();
  // Let the button say "copied" long enough to be noticed, then go back.
  setTimeout(() => { state.copied = false; render(); }, 2200);
}

/*
  Zoom is off.

  The webview inherits a browser's zoom gestures, and this is a fixed layout
  rather than a document: nudging it half a step leaves the pixel typeface
  blurred and the gauges off their grid, and people find it by accident with
  ctrl and a scroll wheel while trying to scroll the page. Both routes in are
  closed, and the wheel listener has to be non-passive or the browser scrolls
  first and asks later.
*/
window.addEventListener('wheel', e => {
  if (e.ctrlKey) e.preventDefault();
}, { passive: false });

window.addEventListener('keydown', e => {
  if (!e.ctrlKey && !e.metaKey) return;
  // '=' and '_' are the unshifted faces of the + and - keys.
  if (['+', '-', '=', '_', '0'].includes(e.key)) e.preventDefault();
});

// Report script errors to the host so a panel that fails to draw leaves a trail
// in the log rather than just sitting there looking empty.
window.addEventListener('error', e => {
  host.send('uiError', {
    detail: `${e.message} at ${e.filename}:${e.lineno}:${e.colno}\n${e.error && e.error.stack || ''}`
  });
});
window.addEventListener('unhandledrejection', e => {
  host.send('uiError', { detail: 'Unhandled promise rejection: ' + (e.reason && e.reason.stack || e.reason) });
});

/* ───────────────────────── opening screen chatter ────────────────────── */

/*
 * Starting up can take a few seconds while the sensor layer wakes, so the
 * opening screen says something rather than sitting there. The host still
 * tracks the real phase for the log; this is purely for the person waiting.
 */
const BABY_TALK = [
  'Warming the bottle',
  'Pampering the sensors',
  'Counting tiny cores',
  'Burping the bus',
  'Powdering the processor',
  'Swaddling the sockets',
  'Rocking the RAM',
  'Checking the diaper drive',
  'Feeding the fans',
  'Cuddling the clocks',
  'Tickling the transistors',
  'Shushing the storage',
  'Bibbing the BIOS',
  'Teething on the temps',
  'Rattling the registers',
  'Naptime for the NVMe',
  'Cooing at the cache',
  'Weighing the watts'
];

function startBabyTalk() {
  const line = document.getElementById('loadingText');
  if (!line) return;

  // Shuffled once so two launches in a row don't read identically.
  const order = BABY_TALK.slice();
  for (let i = order.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1));
    [order[i], order[j]] = [order[j], order[i]];
  }

  let at = 0;
  line.textContent = order[at] + '…';

  const timer = setInterval(() => {
    // The loading screen is gone the moment the first snapshot renders.
    if (!document.getElementById('loadingText')) { clearInterval(timer); return; }
    at = (at + 1) % order.length;
    line.textContent = order[at] + '…';
  }, 3000);
}

startBabyTalk();

host.send('ready');

// The page asks for readings rather than waiting to be pushed to: see the note
// on the host side. Asking straight away means the first panel appears as soon as
// the opening snapshot is ready instead of a second later.
host.send('poll');
setInterval(() => host.send('poll'), 1000);
