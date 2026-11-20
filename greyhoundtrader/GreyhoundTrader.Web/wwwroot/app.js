const runnerSortStorageKey = 'racingTrader.runnerSortMode';
const initialRunnerSortMode = (() => {
  try {
    const stored = localStorage.getItem(runnerSortStorageKey);
    return stored === 'price' ? 'price' : 'number';
  } catch {
    return 'number';
  }
})();

const state = {
  sportId: 4,
  runnerSortMode: initialRunnerSortMode,
  meetingFilter: 'all',
  meetings: [],
  chronologicalRaces: [],
  spreadexMeetingHealth: [],
  meeting: null,
  events: [],
  eventId: null,
  board: null,
  pendingAction: null,
  matchDraft: { eventId: null, runnerASelectionId: '', runnerBSelectionId: '' },
  forecastDraft: { eventId: null, runnerASelectionId: '', runnerBSelectionId: '' },
  bettingWithoutDraft: { eventId: null, excludedSelectionId: '' },
  refreshTimer: null,
  activityTimer: null,
  spreadexTimer: null,
  raceColumnTimer: null,
  rule4Activity: [],
  suspensionMonitor: null,
  missingBetsApiEvents: [],
  providerLinkIssues: [],
  feedLastUpdates: [],
  suspensionSignature: '',
  betfairStatus: null,
  betfairStatusLoadedAt: 0,
  boardLoading: false,
  activityLoading: false,
  betfredStatusLoading: false,
  betfredPricingStatusLoading: false,
  chronologicalRacesLoading: false,
  trapChallengeLoading: false,
  trapChallengeEventId: null,
  trapChallengeLoadedAt: 0
};

const el = id => document.getElementById(id);
const meetingList = el('meetingList');
const chronologicalRaceList = el('chronologicalRaceList');
const spreadexMeetingHealth = el('spreadexMeetingHealth');
const eventTabs = el('eventTabs');
const boardElement = el('board');
const emptyState = el('emptyState');
const dialog = el('actionDialog');

const escapeHtml = value => String(value ?? '')
  .replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;')
  .replaceAll('"', '&quot;').replaceAll("'", '&#039;');

const ukTimeZone = 'Europe/London';
const localTime = unix => new Date(unix * 1000).toLocaleTimeString('en-GB', { timeZone: ukTimeZone, hour: '2-digit', minute: '2-digit' });
const shortDate = unix => new Date(unix * 1000).toLocaleDateString('en-GB', { timeZone: ukTimeZone, weekday: 'short', day: '2-digit', month: 'short' });
const ukDateParts = value => Object.fromEntries(new Intl.DateTimeFormat('en-GB', {
  timeZone: ukTimeZone, year: 'numeric', month: '2-digit', day: '2-digit'
}).formatToParts(new Date(value)).filter(part => part.type !== 'literal').map(part => [part.type, Number(part.value)]));
const ukDayNumber = value => {
  const parts = ukDateParts(value);
  return Date.UTC(parts.year, parts.month - 1, parts.day) / 86400000;
};
const raceDayDetails = unix => {
  const difference = ukDayNumber(unix * 1000) - ukDayNumber(Date.now());
  if (difference === 0) return { label: 'Today', tone: 'today' };
  if (difference === 1) return { label: 'Tomorrow', tone: 'tomorrow' };
  return { label: shortDate(unix), tone: difference < 0 ? 'past' : 'future' };
};
const raceInsideTradingWindow = unix => {
  const parts = Object.fromEntries(new Intl.DateTimeFormat('en-GB', {
    timeZone: ukTimeZone, hour: '2-digit', minute: '2-digit', hourCycle: 'h23'
  }).formatToParts(new Date(unix * 1000)).filter(part => part.type !== 'literal').map(part => [part.type, Number(part.value)]));
  const minutes = parts.hour * 60 + parts.minute;
  return minutes >= 11 * 60 && minutes <= 22 * 60;
};
const raceInsideGreyhoundTradingWindow = unix => {
  const parts = Object.fromEntries(new Intl.DateTimeFormat('en-GB', {
    timeZone: ukTimeZone, hour: '2-digit', minute: '2-digit', hourCycle: 'h23'
  }).formatToParts(new Date(unix * 1000)).filter(part => part.type !== 'literal').map(part => [part.type, Number(part.value)]));
  return parts.hour * 60 + parts.minute >= 10 * 60;
};
const ordinal = value => {
  const number = Number(value);
  const mod100 = number % 100;
  if (mod100 >= 11 && mod100 <= 13) return `${number}th`;
  return `${number}${number % 10 === 1 ? 'st' : number % 10 === 2 ? 'nd' : number % 10 === 3 ? 'rd' : 'th'}`;
};
const rule4Text = value => `${Math.round((value || 0) * 100)}p / £`;

function statusDetails(value) {
  const status = String(value ?? '').trim().toLowerCase();
  if (status === '0' || status === 'pre' || status === 'pre game') return { label: 'Pre-event', tone: 'pre' };
  if (status === '1' || status === 'live' || status === 'in play') return { label: 'Live', tone: 'live' };
  if (status === '2' || status === 'suspended' || status === 'post play') return { label: 'Suspended', tone: 'suspended' };
  if (status === '3' || status === 'resulted' || status === 'post game') return { label: 'Resulted', tone: 'resulted' };
  return { label: status || 'Unknown', tone: 'pre' };
}

const isResulted = value => statusDetails(value).tone === 'resulted';

function resultDetails(value, eventStatus) {
  if (!isResulted(eventStatus) || value === null || value === undefined || String(value).trim() === '') return null;
  const result = String(value).trim().toLowerCase();
  if (result === '1' || result === 'won' || result === 'winner') return { label: 'Won', tone: 'won' };
  if (result === '-1' || result === '99' || result === 'void') return { label: 'Void', tone: 'void' };
  if (result === '0' || result === 'lost' || result === 'loser') return { label: 'Lost', tone: 'lost' };
  return { label: result, tone: 'resulted' };
}

const resultTag = details => details ? `<span class="result-tag ${details.tone}">${escapeHtml(details.label)}</span>` : '';
const stateBadge = (label, tone) => `<span class="state-badge ${tone}">${escapeHtml(label)}</span>`;

function runnerRaceNumber(runner) {
  const match = String(runner.identifier ?? '').match(/\d+/);
  return match ? Number(match[0]) : Number.MAX_SAFE_INTEGER;
}

function runnerWinnerPrice(runner) {
  const price = Number(runner.prices?.['Race Winner']?.price);
  return Number.isFinite(price) && price > 1 && price <= 1001 ? price : Number.POSITIVE_INFINITY;
}

function sortRunners(runners) {
  return [...runners].sort((a, b) => {
    if (state.runnerSortMode === 'price') {
      if (Boolean(a.withdrawn) !== Boolean(b.withdrawn)) return a.withdrawn ? 1 : -1;
      const priceA = runnerWinnerPrice(a);
      const priceB = runnerWinnerPrice(b);
      if (priceA !== priceB) return priceA - priceB;
    }
    const numberDifference = runnerRaceNumber(a) - runnerRaceNumber(b);
    if (numberDifference !== 0) return numberDifference;
    return String(a.name).localeCompare(String(b.name), 'en-GB', { sensitivity: 'base' });
  });
}

function renderSparkline(history) {
  const points = (history || []).filter(point => Number.isFinite(point.price) && point.price > 1).slice(-36);
  if (!points.length) return '<span class="sparkline-empty">Collecting history</span>';

  const prices = points.map(point => point.price);
  const min = Math.min(...prices);
  const max = Math.max(...prices);
  const spread = Math.max(max - min, 0.02);
  const width = 78;
  const height = 27;
  const coordinates = points.length === 1
    ? `1,${height / 2} ${width - 1},${height / 2}`
    : points.map((point, index) => {
        const x = 1 + (index / (points.length - 1)) * (width - 2);
        const y = 2 + ((point.price - min) / spread) * (height - 4);
        return `${x.toFixed(1)},${y.toFixed(1)}`;
      }).join(' ');
  const lastCoordinate = coordinates.split(' ').at(-1).split(',');
  const movement = prices.at(-1) < prices[0] ? 'shortened' : prices.at(-1) > prices[0] ? 'drifted' : 'level';
  const firstTime = new Date(points[0].recordedAtUtc).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
  const lastTime = new Date(points.at(-1).recordedAtUtc).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
  return `<span class="runner-history ${movement}" title="${firstTime} ${prices[0].toFixed(2)} to ${lastTime} ${prices.at(-1).toFixed(2)}">
    <svg viewBox="0 0 ${width} ${height}" role="img" aria-label="Price history from ${prices[0].toFixed(2)} to ${prices.at(-1).toFixed(2)}">
      <polyline points="${coordinates}"></polyline>
      <circle cx="${lastCoordinate[0]}" cy="${lastCoordinate[1]}" r="2"></circle>
    </svg>
    <small>${points.length} pt${points.length === 1 ? '' : 's'}</small>
  </span>`;
}

async function api(path, options = {}) {
  const response = await fetch(path, {
    cache: options.cache || 'no-store',
    headers: { 'Content-Type': 'application/json', ...(options.headers || {}) },
    ...options
  });
  if (!response.ok) {
    let detail = `${response.status} ${response.statusText}`;
    try {
      const body = await response.json();
      detail = body.detail || body.title || body || detail;
    } catch { /* response was not JSON */ }
    throw new Error(detail);
  }
  return response.status === 204 ? null : response.json();
}

function setConnection(mode, text) {
  const target = el('connectionState');
  target.className = `connection-state ${mode}`;
  target.innerHTML = `<i></i> ${escapeHtml(text)}`;
}

async function loadBetfredOffStatus() {
  if (state.betfredStatusLoading || document.hidden) return;
  state.betfredStatusLoading = true;
  const target = el('betfredOffBadge');
  try {
    const status = await api('/api/betfred-off/status');
    const last = sourceDate(status.lastObservedAtUtc);
    const ageSeconds = last ? Math.max(0, Math.round((Date.now() - last.getTime()) / 1000)) : null;
    const live = status.browserConnected && status.pageAvailable && ageSeconds !== null && ageSeconds <= 10;
    target.className = `spreadex-ticker ${live ? 'live' : 'stale'}`;
    const age = ageSeconds === null ? 'waiting' : `${ageSeconds}s`;
    const stateText = status.browserConnected && status.pageAvailable === false ? 'REGION BLOCKED' : (live ? 'LIVE' : 'WAIT');
    target.innerHTML = `<i></i> Betfred OFF ${stateText} · ${age} · ${status.raceCardsObserved} cards`;
    target.title = `Greyhound Trader managed browser ${status.managedBrowserId} · ${status.couponUrl}` +
      (status.lastError ? ` · ${status.lastError}` : '');
  } catch (error) {
    target.className = 'spreadex-ticker error';
    target.innerHTML = '<i></i> Betfred OFF unavailable';
    target.title = error.message;
  } finally {
    state.betfredStatusLoading = false;
  }
}

async function loadBetfredPricingStatus() {
  if (state.betfredPricingStatusLoading || document.hidden) return;
  state.betfredPricingStatusLoading = true;
  const target = el('betfredPricingBadge');
  try {
    const status = await api('/api/betfred/status');
    const live = status.browserConnected;
    const hasShow = Number(status.priceUpdates) > 0;
    target.className = `spreadex-ticker ${live ? (hasShow ? 'live' : 'waiting') : 'error'}`;
    target.innerHTML = `<i></i> Betfred prices ${hasShow ? 'SHOW' : (live ? 'SP ONLY' : 'OFFLINE')} · ${status.matchedRaces}/${status.discoveredEvents}`;
    target.title = `${status.pollingStrategy} · ${status.socketDeltasApplied} WebSocket updates applied` +
      (status.lastError ? ` · ${status.lastError}` : ' · BetsAPI remains backup until a complete Betfred show is available');
  } catch (error) {
    target.className = 'spreadex-ticker error';
    target.innerHTML = '<i></i> Betfred prices unavailable';
    target.title = error.message;
  } finally {
    state.betfredPricingStatusLoading = false;
  }
}

async function loadSuspensionSentinelStatus() {
  const target = el('suspensionSentinelBadge');
  try {
    const status = await api('/api/suspension-sentinel/status');
    const heartbeat = sourceDate(status.lastLoopUtc);
    const ageSeconds = heartbeat ? Math.max(0, Math.round((Date.now() - heartbeat.getTime()) / 1000)) : null;
    const live = ageSeconds !== null && ageSeconds <= 3;
    const backedUp = Number(status.pendingFollowUps) > 0 || Number(status.durableOutboxDepth) > 0;
    target.className = `spreadex-ticker ${live && !backedUp ? 'live' : (live ? 'waiting' : 'error')}`;
    target.innerHTML = `<i></i> Suspensions ${live ? 'LIVE' : 'STALE'} · ${ageSeconds === null ? 'waiting' : `${ageSeconds}s`} · Q${status.pendingFollowUps}/${status.durableOutboxDepth}`;
    target.title = `${status.scheduledRaces} races watched` + (status.lastError ? ` · ${status.lastError}` : '');
  } catch (error) {
    target.className = 'spreadex-ticker error';
    target.innerHTML = '<i></i> Suspensions unavailable';
    target.title = error.message;
  }
}

function toast(message, isError = false) {
  const target = el('toast');
  target.textContent = message;
  target.className = `toast show${isError ? ' error' : ''}`;
  clearTimeout(target._timer);
  target._timer = setTimeout(() => target.className = 'toast', 3500);
}

async function loadActivity() {
  if (state.activityLoading || document.hidden) return;
  state.activityLoading = true;
  try {
    [state.rule4Activity, state.suspensionMonitor, state.feedLastUpdates, state.missingBetsApiEvents,
      state.providerLinkIssues] = await Promise.all([
      api('/api/activity/rule4?limit=250'), api('/api/suspension-monitor'), api('/api/feed-last-updates'),
      api('/api/missing-betsapi-events'), api('/api/provider-link-issues')
    ]);
    renderActivity();
    renderSuspensionMonitor(true);
    renderFeedLastUpdates();
    renderMissingBetsApiEvents();
    renderProviderLinkIssues();
  } catch (error) {
    el('feedUpdateMonitorList').innerHTML = `<p class="activity-empty error">${escapeHtml(error.message)}</p>`;
  } finally {
    state.activityLoading = false;
  }
}

function renderActivity() {
  const rule4Items = state.rule4Activity;
  el('rule4MonitorList').innerHTML = rule4Items.map(item => {
    const parts = String(item.message || '').split('||');
    const time = new Date(item.createdAtUtc).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    const details = parts.slice(1).join('||');
    const race = item.eventName || item.meeting || 'Race';
    return `<article class="rule4-monitor-item"${details ? ` title="${escapeHtml(details)}"` : ''}>
      <strong>${escapeHtml(race)}</strong><time>${escapeHtml(time)}</time>
      <span>${escapeHtml(parts[0])}</span>
      ${details ? '<small>hover for all market prices</small>' : ''}
    </article>`;
  }).join('') || '<p>No Rule 4 calculations yet.</p>';
  if (false) el('activityList').innerHTML = state.activity.map(item => {
    const type = String(item.activityType || 'update').toLowerCase();
    const time = new Date(item.createdAtUtc).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    const sport = item.sportId === 2 ? 'Horse' : item.sportId === 4 ? 'Greyhound' : '';
    const context = [sport, item.meeting, item.eventName].filter(Boolean).join(' · ');
    const messageParts = String(item.message || '').split('||');
    const summary = messageParts[0];
    const details = messageParts.slice(1).join('||');
    return `<article class="activity-item ${escapeHtml(type)}"${details ? ` title="${escapeHtml(details)}"` : ''}>
      <div class="activity-item-top"><span class="activity-type"><i></i>${escapeHtml(type)}</span><time>${escapeHtml(time)}</time></div>
      <p>${escapeHtml(summary)}${details ? ' <span class="activity-hover-hint">hover for all markets</span>' : ''}</p>
      ${context ? `<small>${escapeHtml(context)}</small>` : ''}
    </article>`;
  }).join('') || '<p class="activity-empty">Waiting for subscriptions, prices and results…</p>';
}

function renderProviderLinkIssues() {
  const items = state.providerLinkIssues || [];
  el('providerLinkIssueCount').textContent = `${items.length} issue${items.length === 1 ? '' : 's'}`;
  el('providerLinkMonitorList').innerHTML = items.map(item => {
    const off = new Date(Number(item.startTimeUnix) * 1000).toLocaleString('en-GB', {
      weekday: 'short', hour: '2-digit', minute: '2-digit'
    });
    const providers = `${item.hasBet365 ? 'B365' : '—'} / ${item.hasSpreadex ? 'SDX' : '—'}`;
    const score = Number.isFinite(Number(item.matchScore)) ? ` · ${(Number(item.matchScore) * 100).toFixed(0)}%` : '';
    const counterpart = item.counterpartEventId ? ` · counterpart ${item.counterpartEventId}` : '';
    return `<button type="button" class="provider-link-monitor-item ${item.state === 'SPLIT' ? 'split' : ''}"
      data-monitor-event="${item.eventId}" data-monitor-meeting="${escapeHtml(item.meeting)}">
      <span>${escapeHtml(item.state)}</span><time>${escapeHtml(off)}</time>
      <strong>${escapeHtml(item.eventName)}</strong>
      <small>${escapeHtml(providers)} · ID ${escapeHtml(item.eventId)}${escapeHtml(score)}${escapeHtml(counterpart)}</small>
    </button>`;
  }).join('') || '<p>All current Bet365 / Spreadex event links are matched.</p>';
  el('providerLinkMonitorList').querySelectorAll('[data-monitor-event]').forEach(button =>
    button.addEventListener('click', () => openMonitorRace(
      Number(button.dataset.monitorEvent), button.dataset.monitorMeeting)));
}

function renderMissingBetsApiEvents() {
  const items = state.missingBetsApiEvents || [];
  el('missingBetsApiCount').textContent = `${items.length} event${items.length === 1 ? '' : 's'}`;
  el('missingBetsApiMonitorList').innerHTML = items.map(item => {
    const off = new Date(Number(item.startTimeUnix) * 1000);
    const offText = off.toLocaleString('en-GB', {
      weekday: 'short', hour: '2-digit', minute: '2-digit'
    });
    const supplied = [
      item.hasBet365Browser ? 'B365' : '',
      item.hasSpreadex ? 'SDX' : ''
    ].filter(Boolean).join(' + ') || 'DISCOVERY ONLY';
    return `<button type="button" class="missing-feed-monitor-item" data-monitor-event="${item.eventId}" data-monitor-meeting="${escapeHtml(item.meeting)}">
      <span>${escapeHtml(supplied)}</span><time>${escapeHtml(offText)}</time>
      <strong>${escapeHtml(item.eventName)}</strong>
      <small>ID ${escapeHtml(item.eventId)}</small>
    </button>`;
  }).join('') || '<p>No current events are missing from BetsAPI.</p>';
  el('missingBetsApiMonitorList').querySelectorAll('[data-monitor-event]').forEach(button =>
    button.addEventListener('click', () => openMonitorRace(
      Number(button.dataset.monitorEvent), button.dataset.monitorMeeting)));
}

function renderFeedLastUpdates() {
  const names = { SPREADEX: 'Spreadex', BETFRED: 'Betfred', DIRECT: 'Bet365 browser', BET365_DIRECT: 'Bet365 direct', BETSAPI: 'BetsAPI' };
  el('feedUpdateMonitorList').innerHTML = (state.feedLastUpdates || []).map(item => {
    const updated = item.updatedAtUtc ? new Date(item.updatedAtUtc) : null;
    const time = updated && !Number.isNaN(updated.valueOf())
      ? updated.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit', second: '2-digit' })
      : 'NO UPDATE';
    const clickable = item.eventId ? ` data-monitor-event="${item.eventId}"` : '';
    return `<button type="button" class="feed-update-monitor-item ${escapeHtml(String(item.source || '').toLowerCase())}"${clickable}>
      <span>${escapeHtml(names[item.source] || item.source)}</span><time>${escapeHtml(time)}</time>
      <strong>${escapeHtml(item.raceName || 'Waiting for a price')}</strong>
    </button>`;
  }).join('');
  el('feedUpdateMonitorList').querySelectorAll('[data-monitor-event]').forEach(button =>
    button.addEventListener('click', () => openMonitorRace(Number(button.dataset.monitorEvent), '')));
}

function suspensionCountdown(startTimeUnix) {
  const seconds = Math.ceil(Number(startTimeUnix) - Date.now() / 1000);
  const overdue = seconds < 0;
  const absolute = Math.abs(seconds);
  const minutes = Math.floor(absolute / 60);
  const remainder = absolute % 60;
  return `${overdue ? 'OVERDUE ' : '-'}${String(minutes).padStart(2, '0')}:${String(remainder).padStart(2, '0')}`;
}

function renderSuspensionMonitor(allowFlash = false) {
  const monitor = state.suspensionMonitor;
  if (!monitor) return;
  const signature = JSON.stringify(monitor);
  if (allowFlash && state.suspensionSignature && signature !== state.suspensionSignature) {
    const panel = el('suspensionMonitor');
    panel.classList.remove('changed');
    void panel.offsetWidth;
    panel.classList.add('changed');
  }
  state.suspensionSignature = signature;
  const rows = [];
  if (monitor.lastSuspended) {
    const race = monitor.lastSuspended;
    const time = new Date(race.suspendedAtUtc).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    rows.push(`<button type="button" class="suspension-monitor-item suspended" data-monitor-event="${race.eventId}" data-monitor-meeting="${escapeHtml(race.meeting)}">
      <span>LAST</span><time>SUSP ${escapeHtml(time)}</time><strong>${escapeHtml(race.eventName)}</strong>
    </button>`);
  }
  (monitor.nextRaces || []).forEach((race, index) => rows.push(
    `<button type="button" class="suspension-monitor-item" data-monitor-event="${race.eventId}" data-monitor-meeting="${escapeHtml(race.meeting)}">
      <span>NEXT ${index + 1}</span><time data-off-unix="${race.startTimeUnix}">${escapeHtml(suspensionCountdown(race.startTimeUnix))}</time><strong>${escapeHtml(race.eventName)}</strong>
    </button>`));
  el('suspensionMonitorList').innerHTML = rows.join('') || '<p>No races awaiting suspension.</p>';
  el('suspensionMonitorList').querySelectorAll('[data-monitor-event]').forEach(button =>
    button.addEventListener('click', () => openMonitorRace(
      Number(button.dataset.monitorEvent), button.dataset.monitorMeeting)));
}

async function openMonitorRace(eventId, meetingName) {
  try {
    if (!meetingName) {
      const preview = await api(`/api/events/${eventId}`);
      meetingName = preview.event?.meeting || '';
    }
    if (state.sportId !== 4) {
      state.sportId = 4;
      document.querySelectorAll('.sport-tab').forEach(tab => {
        const active = Number(tab.dataset.sport) === 2;
        tab.classList.toggle('active', active);
        tab.setAttribute('aria-selected', String(active));
      });
      await loadMeetings(false);
    } else if (!state.meetings.length) {
      await loadMeetings(false);
    }
    const meeting = state.meetings.find(item => item.name === meetingName);
    if (meeting) {
      state.meeting = meeting;
      state.eventId = eventId;
      renderMeetings();
      await loadEvents(true);
    }
    if (state.eventId !== eventId) state.eventId = eventId;
    await loadBoard();
    renderEvents();
  } catch (error) {
    toast(`Could not open race: ${error.message}`, true);
  }
}

window.addEventListener('message', event => {
  if (event.origin !== location.origin || event.data?.type !== 'racingtrader-open-event') return;
  const eventId = Number(event.data.eventId);
  if (!eventId) return;
  openMonitorRace(eventId, String(event.data.meeting || ''));
});

function showMeetingSkeletons() {
  meetingList.innerHTML = '<div class="meeting-card skeleton"></div>'.repeat(5);
}

async function loadMeetings(preserveSelection = true) {
  if (!preserveSelection) showMeetingSkeletons();
  try {
    let usedStoredMeetings = false;
    try {
      state.meetings = await api('/api/betsapi/meetings');
    } catch {
      state.meetings = await api(`/api/meetings?sportId=${state.sportId}`);
      usedStoredMeetings = true;
    }
    setConnection('online', usedStoredMeetings ? 'Stored meetings' : 'BetsAPI greyhounds');
    renderMeetings();
    await loadChronologicalRaces();

    if (state.meeting) {
      const refreshed = state.meetings.find(x => x.name === state.meeting.name);
      if (refreshed) {
        state.meeting = refreshed;
        await loadEvents(true);
      } else {
        clearWorkspace();
      }
    }
  } catch (error) {
    setConnection('error', 'BetsAPI unavailable');
    meetingList.innerHTML = `<div class="empty-state"><p>${escapeHtml(error.message)}</p></div>`;
  }
}

function renderMeetings() {
  const visibleMeetings = state.meetingFilter === 'subscribed'
    ? state.meetings.filter(meeting => meeting.subscribed)
    : state.meetings;
  el('meetingCount').textContent = visibleMeetings.length;
  meetingList.innerHTML = visibleMeetings.map(meeting => {
    const active = state.meeting?.name === meeting.name ? ' active' : '';
    const subscribed = meeting.subscribed;
    return `<button class="meeting-card${active}" data-meeting="${escapeHtml(meeting.name)}">
      <span>
        <span class="meeting-name">${escapeHtml(meeting.name)}</span>
        <span class="meeting-meta">${shortDate(meeting.firstStartUnix)} · ${meeting.eventCount} events · ${meeting.subscribedEventCount} live</span>
      </span>
      <span class="subscribe-button ${subscribed ? 'on' : ''}" data-subscribe="${escapeHtml(meeting.name)}" data-value="${!subscribed}" title="${subscribed ? 'Subscribed — click to remove' : 'Subscribe to meeting'}">
        ${subscribed ? 'ON' : 'ADD'}
      </span>
    </button>`;
  }).join('') || `<p class="meeting-meta">${state.meetingFilter === 'subscribed' ? 'No subscribed competitions found.' : 'No upcoming meetings found.'}</p>`;

  meetingList.querySelectorAll('[data-meeting]').forEach(button => button.addEventListener('click', () => {
    state.meeting = state.meetings.find(x => x.name === button.dataset.meeting);
    state.eventId = null;
    renderMeetings();
    loadEvents(false);
  }));
  meetingList.querySelectorAll('[data-subscribe]').forEach(button => button.addEventListener('click', async event => {
    event.stopPropagation();
    await setSubscription(button.dataset.subscribe, button.dataset.value === 'true');
  }));
}

async function setSubscription(meeting, subscribed) {
  try {
    await api(`/api/meetings/${state.sportId}/${encodeURIComponent(meeting)}/subscription`, {
      method: 'PUT', body: JSON.stringify({ subscribed })
    });
    toast(`${meeting} ${subscribed ? 'subscribed' : 'unsubscribed'}.`);
    if (state.meeting?.name === meeting) state.meeting.subscribed = subscribed;
    await loadMeetings(true);
    if (!subscribed && state.meetingFilter === 'subscribed' && state.meeting?.name === meeting) clearWorkspace();
  } catch (error) {
    toast(error.message, true);
  }
}

async function loadEvents(preserveEvent) {
  if (!state.meeting) return;
  el('selectedMeeting').textContent = state.meeting.name;
  el('bet365WalkButton').classList.toggle('hidden', !state.meeting.subscribed);
  if (!state.meeting.subscribed) {
    state.events = [];
    state.eventId = null;
    renderEvents();
    showEmpty('Subscribe to this meeting', 'Subscription activates its events and makes their markets available here.');
    return;
  }

  try {
    state.events = await api(`/api/meetings/${state.sportId}/${encodeURIComponent(state.meeting.name)}/events?subscribedOnly=true`);
    if (!preserveEvent || !state.events.some(x => x.eventId === state.eventId)) {
      state.eventId = state.events[0]?.eventId ?? null;
    }
    renderEvents();
    renderChronologicalRaces();
    if (state.eventId) await loadBoard();
    else showEmpty('Waiting for events', 'The meeting is subscribed; events will appear when the feed publishes them.');
  } catch (error) {
    toast(error.message, true);
  }
}

function renderEvents() {
  const unpriced = state.events.filter(event => !event.betsApiPriced).length;
  const unpricedBadge = el('unpricedRaceCount');
  unpricedBadge.textContent = `${unpriced} unpriced`;
  unpricedBadge.classList.toggle('hidden', unpriced === 0);
  eventTabs.innerHTML = state.events.map(event => {
    const status = statusDetails(event.status);
    const day = raceDayDetails(event.startTimeUnix);
    const unpricedClass = event.betsApiPriced ? '' : ' unpriced';
    return `<button class="event-tab race-day-${day.tone}${unpricedClass} ${event.eventId === state.eventId ? 'active' : ''}" data-event="${event.eventId}" title="${event.betsApiPriced ? '' : 'No BetsAPI prices'}">
    <span class="event-day-badge ${day.tone}">${escapeHtml(day.label)}</span><time>${localTime(event.startTimeUnix)}</time><span>${escapeHtml(event.name)}</span><span class="event-tab-status ${status.tone}">${escapeHtml(status.label)}</span>
  </button>`;
  }).join('');
  eventTabs.querySelectorAll('[data-event]').forEach(button => button.addEventListener('click', async () => {
    state.eventId = Number(button.dataset.event);
    renderEvents();
    renderChronologicalRaces();
    await loadBoard();
  }));
}

async function loadBoard() {
  if (!state.eventId || state.boardLoading || document.hidden) return;
  const requestedEventId = state.eventId;
  state.boardLoading = true;
  try {
    const statusDue = Date.now() - state.betfairStatusLoadedAt > 30000;
    const selectedEvent = state.events.find(event => event.eventId === state.eventId);
    const spreadexPricesRequest = state.sportId === 2 && selectedEvent
      ? api(`/api/spreadex/prices?meeting=${encodeURIComponent(selectedEvent.meeting)}&startTimeUnix=${selectedEvent.startTimeUnix}`).catch(() => [])
      : Promise.resolve([]);
    const raceCardPricesRequest = state.sportId === 2 && selectedEvent
      ? api(`/api/spreadex/racecard-prices?meeting=${encodeURIComponent(selectedEvent.meeting)}&startTimeUnix=${selectedEvent.startTimeUnix}`).catch(() => [])
      : Promise.resolve([]);
    const retainedTrapChallenge = state.board?.event?.eventId === requestedEventId
      ? state.board.trapChallenge
      : null;
    const trapChallengeDue = state.sportId === 4 && selectedEvent && !state.trapChallengeLoading &&
      (state.trapChallengeEventId !== requestedEventId || Date.now() - state.trapChallengeLoadedAt >= 5000);
    let trapChallengeRequest = Promise.resolve(retainedTrapChallenge);
    if (trapChallengeDue) {
      state.trapChallengeLoading = true;
      trapChallengeRequest = api(`/api/meetings/4/${encodeURIComponent(selectedEvent.meeting)}/trap-challenge?startTimeUnix=${selectedEvent.startTimeUnix}`)
        .catch(error => ({ status: 'error', message: error.message, raceCount: 0, resultedRaceCount: 0, selections: [] }))
        .then(trapChallenge => {
          state.trapChallengeEventId = requestedEventId;
          state.trapChallengeLoadedAt = Date.now();
          return trapChallenge;
        })
        .finally(() => { state.trapChallengeLoading = false; });
    }
    const [board, spreadexPrices, spreadexRaceCardPrices] = await Promise.all([
      api(`/api/events/${requestedEventId}`),
      spreadexPricesRequest,
      raceCardPricesRequest,
      statusDue
        ? api('/api/betfair/status').then(status => {
            state.betfairStatus = status;
            state.betfairStatusLoadedAt = Date.now();
          }).catch(() => {})
        : Promise.resolve()
    ]);
    if (requestedEventId !== state.eventId) return;
    const spreadexRaceCardByKey = new Map((spreadexRaceCardPrices || []).map(price => [price.key, price]));
    board.raceCardSpreads?.forEach(market => Object.assign(market, {
      spreadexBid: spreadexRaceCardByKey.get(market.key)?.bid,
      spreadexOffer: spreadexRaceCardByKey.get(market.key)?.offer,
      spreadexUpdatedAtUtc: spreadexRaceCardByKey.get(market.key)?.updatedAtUtc
    }));
    const spreadexByName = new Map((spreadexPrices || []).map(price => [String(price.name || '').trim().toLowerCase(), price]));
    board.runners.forEach(runner => {
      const live = spreadexByName.get(String(runner.name || '').trim().toLowerCase());
      runner.spreadexIndexSuspended = Boolean(live?.indexSuspended);
    });
    state.board = board;
    board.trapChallenge = retainedTrapChallenge;
    const listedEvent = state.events.find(event => event.eventId === board.event.eventId);
    if (listedEvent) {
      const statusChanged = String(listedEvent.status) !== String(board.event.status);
      Object.assign(listedEvent, board.event);
      if (statusChanged) renderEvents();
    }
    renderBoard();
    // Meeting-level calculations query every race and can be much slower than
    // the selected event. Never hold back live race status or prices while the
    // Trap Challenge refresh completes.
    trapChallengeRequest.then(trapChallenge => {
      if (requestedEventId !== state.eventId || state.board !== board) return;
      board.trapChallenge = trapChallenge;
      renderTrapChallenge(trapChallenge, board.event.sportId);
    });
  } catch (error) {
    if (String(error.message).includes('404')) showEmpty('Markets are loading', 'The event is subscribed but its first market snapshot has not arrived yet.');
    else toast(error.message, true);
  } finally {
    state.boardLoading = false;
    if (requestedEventId !== state.eventId) loadBoard();
  }
}

function renderBoard() {
  const data = state.board;
  const coreMarkets = data.markets.filter(market => !isInsureMarket(market.name));
  const insureMarkets = data.markets.filter(market => isInsureMarket(market.name));
  emptyState.classList.add('hidden');
  boardElement.classList.remove('hidden');
  boardElement.classList.toggle('settled-event', isResulted(data.event.status));
  el('eventTime').textContent = localTime(data.event.startTimeUnix);
  el('eventName').textContent = data.event.name;
  el('eventFeedLink').href = `/api/events/${data.event.eventId}/feed/direct`;
  el('boardMeta').textContent = `${data.event.meeting} · ${shortDate(data.event.startTimeUnix)} · Event ${data.event.eventId}`;
  el('totalRule4').textContent = rule4Text(data.totalRule4Deduction);
  const betfairNote = el('betfairNote');
  if (betfairNote) betfairNote.textContent = betfairStateText();
  const eventStatus = statusDetails(data.event.status);
  el('eventStateBadges').innerHTML = [
    stateBadge(eventStatus.label, eventStatus.tone),
    stateBadge(data.event.active ? 'Active' : 'Suspended', data.event.active ? 'good' : 'bad'),
    stateBadge(data.event.display ? 'Displayed' : 'Hidden', data.event.display ? 'good' : 'bad'),
    data.event.inProgress ? stateBadge('Updating', 'live') : ''
  ].join('');
  renderTrapChallenge(data.trapChallenge, data.event.sportId);

  el('priceHead').innerHTML = `<tr>
    <th class="runner-col">Runner</th>
    <th class="betfair-col"><span class="market-head-name">Betfair <span class="betfair-delay-label">delayed</span></span></th>
    <th class="diff-col">Price diff<br><small>Our 100% - BF</small></th>
    <th class="source-compare-col">BetsAPI / Spreadex / Betfred / BetAhoy / Timeform<br><small>Timeform: Trap Challenge only</small></th>
    ${coreMarkets.map(market => renderMarketHeader(market, data.runners)).join('')}
    <th class="rule-col">Rule 4 / action</th>
    ${insureMarkets.map(market => renderMarketHeader(market, data.runners)).join('')}
  </tr>`;

  el('priceBody').innerHTML = sortRunners(data.runners).map(runner => {
    const runnerResult = resultDetails(runner.result, data.event.status);
    const placing = runner.finishingPosition ? `${ordinal(runner.finishingPosition)} place` : runner.resultStatus && runner.resultStatus !== 'Finished' ? runner.resultStatus : '';
    const status = runner.withdrawn ? 'Withdrawn' : placing || runnerResult?.label || (runner.active ? 'Active' : 'Suspended');
    const actionLocked = isResulted(data.event.status) || !data.event.active;
    const action = data.event.sportId === 2
      ? `<button class="runner-action" data-action="withdraw" data-selection="${runner.selectionId}" ${runner.withdrawn || actionLocked ? 'disabled' : ''}>${runner.withdrawn ? 'WITHDRAWN' : actionLocked ? 'CLOSED' : 'WITHDRAW'}</button>`
      : `<button class="runner-action replace" data-action="replace" data-selection="${runner.selectionId}" ${actionLocked ? 'disabled' : ''}>${actionLocked ? 'CLOSED' : 'REPLACE DOG'}</button>`;
    const ruleTime = runner.rule4TimeUtc ? formatRuleTime(runner.rule4TimeUtc) : '';
    const silkUrl = /^https?:\/\//i.test(runner.assetUrl || '') ? runner.assetUrl : null;
    const visual = `<span class="runner-visual">${silkUrl ? `<img class="runner-silk" src="${escapeHtml(silkUrl)}" alt="${escapeHtml(runner.name)} silk">` : ''}</span>`;
    return `<tr class="${runner.withdrawn ? 'withdrawn-row' : ''}">
      <td class="runner-col"><div class="runner-cell">
        ${visual}
        <span class="runner-number" title="Cloth number">${escapeHtml(runner.identifier || '–')}</span>
        <span class="runner-details"><span class="runner-name">${escapeHtml(runner.name)}</span><span class="runner-status ${runner.withdrawn ? 'withdrawn' : ''}">${escapeHtml(status)}${runner.display ? '' : ' · Hidden'}</span></span>
        ${renderSparkline(runner.priceHistory)}
      </div></td>
      ${renderBetfairPrice(runner.betfair)}
      ${renderBetfairDifference(runner)}
      ${renderSourcePrices(runner, runner.ladderMoves)}
      ${coreMarkets.map(market => renderGridPrice(
        runner.prices[market.name], runner.withdrawn, data.event.status, runner, market.name
      )).join('')}
      <td class="rule-col">
        ${runner.withdrawn && runner.rule4TimeUtc ? `<span class="rule-value">R4 ${rule4Text(runner.rule4Deduction)}</span><span class="rule-time">${escapeHtml(ruleTime)}</span>` : ''}
        ${action}
      </td>
      ${insureMarkets.map(market => renderGridPrice(
        runner.prices[market.name], runner.withdrawn, data.event.status, runner, market.name
      )).join('')}
    </tr>`;
  }).join('') || `<tr><td class="runner-col">No runners received</td><td colspan="${data.markets.length + 4}">Waiting for a market snapshot.</td></tr>`;

  el('priceBody').querySelectorAll('[data-action]').forEach(button => button.addEventListener('click', () => openAction(button.dataset.action, Number(button.dataset.selection))));
  el('priceBody').querySelectorAll('.runner-silk').forEach(image => image.addEventListener('error', () => image.classList.add('hidden')));

  renderBettingWithoutControls(data);
  renderMatchBetControls(data);
  renderForecastControls(data);
  renderMarketControls(data);
  renderRaceCardSpreads(data);
  renderDerivedMarkets(data);
}

function renderTrapChallenge(challenge, sportId) {
  const target = el('trapChallengePanel');
  if (!target) return;
  if (sportId !== 4 || !challenge) {
    target.classList.add('hidden');
    target.innerHTML = '';
    return;
  }
  target.classList.remove('hidden');
  const status = String(challenge.status || 'waiting').toLowerCase();
  const selections = challenge.selections?.length === 6
    ? challenge.selections
    : Array.from({ length: 6 }, (_, index) => ({ trap: index + 1, winners: 0, fairPrice: null, probability: null }));
  target.innerHTML = `<div class="trap-challenge-heading">
      <span>Trap Challenge · ${Number(challenge.resultedRaceCount || 0)}/${Number(challenge.raceCount || 0)} results</span>
      <strong class="trap-challenge-status ${escapeHtml(status)}">${escapeHtml(status)}</strong>
    </div>
    <div class="trap-challenge-grid">${selections.map(selection => {
      const trap = Number(selection.trap);
      const price = Number(selection.fairPrice);
      const chance = Number(selection.probability);
      const winners = Number(selection.winners || 0);
      return `<div class="trap-challenge-card" title="${Number.isFinite(chance) ? `${(chance * 100).toFixed(2)}% fair chance` : escapeHtml(challenge.message || 'Waiting')}">
        <span class="trap-icon trap-${trap}">${trap}</span>
        <span class="trap-challenge-values"><strong>${Number.isFinite(price) && price >= 1 ? price.toFixed(2) : '—'}</strong><small>${winners} win${winners === 1 ? '' : 's'}</small></span>
      </div>`;
    }).join('')}</div>
    <small class="trap-challenge-message">${escapeHtml(challenge.message || '')}</small>`;
}

function renderRaceCardSpreads(data) {
  const target = el('raceCardSpreads');
  if (!target) return;
  // Greyhound Multi-Traps is a fixed-odds Over/Under market shown below in
  // Additional markets. Do not also show the legacy horse-racing spread card,
  // whose RT bid/offer notation (for example 1-3) is easily mistaken for odds.
  const markets = (data.raceCardSpreads || []).filter(market =>
    data.event.sportId !== 4 || market.key !== 'race-multi-mules');
  target.innerHTML = `<div class="racecard-spread-heading"><div><span class="eyebrow">Calculated race spreads</span><h3>Racecard markets</h3></div><a href="/meeting-markets.html?meeting=${encodeURIComponent(data.event.meeting)}" target="_blank">Meeting aggregate ↗</a></div>
    <div class="racecard-spread-grid">${markets.map(market => `<div class="racecard-spread-card" title="${escapeHtml(market.helpText)}"><span>${escapeHtml(market.name)}</span><div class="racecard-price-stack"><span class="index-source"><span class="book-logo our">RT</span><strong>${market.bid == null ? '—' : `${formatSpread(market.bid)}–${formatSpread(market.offer)}`}</strong></span><span class="index-source"><span class="book-logo spreadex">S</span><strong>${market.spreadexBid == null ? '—' : `${formatSpread(market.spreadexBid)}–${formatSpread(market.spreadexOffer)}`}</strong></span></div></div>`).join('')}</div>`;
}

function formatSpread(value) { return Number.isInteger(value) ? String(value) : Number(value).toFixed(2).replace(/0+$/, '').replace(/\.$/, ''); }

function minimumRunnerCount(marketName) {
  if (marketName === 'Race Winner') return 2;
  if (marketName === 'Top 2' || marketName === 'Insure Bet 2 Places' || marketName.startsWith('Betting Without')) return 4;
  if (marketName === 'Top 3' || marketName === 'Insure Bet 3 Places') return 5;
  if (marketName === 'Top 4' || marketName === 'Insure Bet 4 Places' || marketName === '50-30-20-10 Index') return 6;
  if (marketName === '50-25-10 Index' || marketName.startsWith('Match Betting:')) return 4;
  return null;
}

function isInsureMarket(marketName) {
  return marketName.startsWith('Insure Bet ');
}

function marketColumnClass(marketName) {
  if (isInsureMarket(marketName)) return 'market-col market-insure-col';
  if (marketName.startsWith('Betting Without')) return 'market-col market-custom-col market-without-col';
  if (marketName.startsWith('Match Betting:')) return 'market-col market-custom-col market-match-col';
  if (marketName.startsWith('Forecast:')) return 'market-col market-custom-col market-forecast-col';
  if (marketName.endsWith(' Index')) return 'market-col market-index-col';
  return 'market-col';
}

function renderGridPrice(cell, withdrawn, eventStatus, runner, marketName) {
  return renderPrice(cell, withdrawn, eventStatus, runner, marketName)
    .replace('<td>', `<td class="${marketColumnClass(marketName)}">`);
}

function renderMarketHeader(market, runners) {
  const minimum = minimumRunnerCount(market.name);
  const indexMarket = market.name.endsWith(' Index');
  const indexBooks = indexMarket ? marketIndexBooksFromRunners(runners, market.name) : null;
  const metric = !market.active && minimum
    ? `OFF · min ${minimum}`
    : indexMarket ? marketIndexSumFromRunners(runners, market.name) : marketBookFromRunners(runners, market.name);
  const title = !market.active && minimum ? `Disabled: requires ${minimum} active runners` : '';
  return `<th class="${marketColumnClass(market.name)} ${market.active ? '' : 'market-disabled'}" title="${escapeHtml(title)}">
    <span class="market-head-name"><span>${escapeHtml(market.name)}</span><span class="market-book">${escapeHtml(metric)}</span>
      ${indexBooks ? `<span class="index-book-totals"><span><b>SDX</b> ${escapeHtml(indexBooks.spreadex)}</span><span><b>RT</b> ${escapeHtml(indexBooks.racingTrader)}</span><span><b>BF</b> ${escapeHtml(indexBooks.betfred)}</span></span>` : ''}
    </span>
  </th>`;
}

function renderBettingWithoutControls(data) {
  const config = data.marketConfiguration;
  const target = el('bettingWithoutControlBody');
  if (!config || !target) return;
  const locked = isResulted(data.event.status);
  if (state.bettingWithoutDraft.eventId !== data.event.eventId)
    state.bettingWithoutDraft = { eventId: data.event.eventId, excludedSelectionId: '' };

  const existing = new Set((config.bettingWithoutMarkets || []).map(market => String(market.excludedSelectionId)));
  const available = sortRunners(data.runners).filter(runner => !runner.withdrawn && !existing.has(String(runner.selectionId)));
  if (!available.some(runner => String(runner.selectionId) === state.bettingWithoutDraft.excludedSelectionId))
    state.bettingWithoutDraft.excludedSelectionId = '';
  const options = available.map(runner =>
    `<option value="${runner.selectionId}" ${String(runner.selectionId) === state.bettingWithoutDraft.excludedSelectionId ? 'selected' : ''}>${escapeHtml(`${runner.identifier || '–'} ${runner.name}`)}</option>`
  ).join('');

  target.innerHTML = `<div class="quick-market-row">
  <div class="quick-market-name">Betting Without</div>
  <div class="without-builder">
    <select id="bettingWithoutRunner" aria-label="Runner excluded from Betting Without" ${locked ? 'disabled' : ''}>
      <option value="">Choose runner to remove</option>${options}
    </select>
    <button id="addBettingWithout" class="button secondary" type="button" ${locked || !state.bettingWithoutDraft.excludedSelectionId ? 'disabled' : ''}>Add market</button>
  </div>
  <div class="without-market-list quick-market-list">${config.bettingWithoutMarkets?.length ? config.bettingWithoutMarkets.map(market => `<div class="without-market ${market.voided ? 'voided' : ''}">
    <span><strong>Without ${escapeHtml(market.excludedRunnerName)}</strong><small>#${market.displayOrder}${market.voided ? ` · Void: ${escapeHtml(market.voidReason || 'non-runner')}` : ''}</small></span>
    <button type="button" data-delete-without="${market.id}" ${locked ? 'disabled' : ''}>Remove</button>
  </div>`).join('') : '<span class="match-empty">No markets added.</span>'}</div></div>`;

  el('bettingWithoutRunner').addEventListener('change', event => {
    state.bettingWithoutDraft = { eventId: data.event.eventId, excludedSelectionId: event.target.value };
    el('addBettingWithout').disabled = locked || !event.target.value;
  });
  el('addBettingWithout').addEventListener('click', addBettingWithout);
  target.querySelectorAll('[data-delete-without]').forEach(button =>
    button.addEventListener('click', () => deleteBettingWithout(Number(button.dataset.deleteWithout))));
}

function renderMatchBetControls(data) {
  const config = data.marketConfiguration;
  const target = el('matchBetControlBody');
  if (!config) { target.innerHTML = ''; return; }
  const locked = isResulted(data.event.status);
  if (state.matchDraft.eventId !== data.event.eventId) {
    state.matchDraft = { eventId: data.event.eventId, runnerASelectionId: '', runnerBSelectionId: '' };
  }
  const availableRunnerIds = new Set(data.runners.filter(runner => !runner.withdrawn).map(runner => String(runner.selectionId)));
  if (!availableRunnerIds.has(state.matchDraft.runnerASelectionId)) state.matchDraft.runnerASelectionId = '';
  if (!availableRunnerIds.has(state.matchDraft.runnerBSelectionId)) state.matchDraft.runnerBSelectionId = '';
  const runnerOptions = selected => sortRunners(data.runners)
    .filter(runner => !runner.withdrawn)
    .map(runner => `<option value="${runner.selectionId}" ${String(runner.selectionId) === selected ? 'selected' : ''}>${escapeHtml(`${runner.identifier || '–'} ${runner.name}`)}</option>`)
    .join('');
  target.innerHTML = `<div class="quick-market-row">
    <div class="quick-market-name">Match Bets</div>
    <div class="match-pair-builder">
      <select id="matchRunnerA" aria-label="Match Bet runner A" ${locked ? 'disabled' : ''}><option value="">Runner A</option>${runnerOptions(state.matchDraft.runnerASelectionId)}</select>
      <span>v</span>
      <select id="matchRunnerB" aria-label="Match Bet runner B" ${locked ? 'disabled' : ''}><option value="">Runner B</option>${runnerOptions(state.matchDraft.runnerBSelectionId)}</select>
      <button id="addMatchBet" class="button secondary" type="button">Add pair</button>
    </div>
    <div class="match-pair-list quick-market-list">${config.matchBets.length ? config.matchBets.map(pair => `<div class="match-pair ${pair.voided ? 'voided' : ''}">
      <span><strong>${escapeHtml(pair.runnerAName)}</strong> v <strong>${escapeHtml(pair.runnerBName)}</strong><small>#${pair.displayOrder}${pair.voided ? ` · Void: ${escapeHtml(pair.voidReason || 'non-runner')}` : ''}</small></span>
      <button type="button" data-delete-match="${pair.id}" ${locked ? 'disabled' : ''}>Remove</button>
    </div>`).join('') : '<span class="match-empty">No markets added.</span>'}</div>
  </div>`;

  el('matchRunnerA').addEventListener('change', updateMatchBetDraft);
  el('matchRunnerB').addEventListener('change', updateMatchBetDraft);
  el('addMatchBet').addEventListener('click', addMatchBet);
  target.querySelectorAll('[data-delete-match]').forEach(button => button.addEventListener('click', () => deleteMatchBet(Number(button.dataset.deleteMatch))));
  updateAddMatchBetState(locked);
}

async function loadChronologicalRaces() {
  if (state.chronologicalRacesLoading || document.hidden) return;
  state.chronologicalRacesLoading = true;
  try {
    const [events, meetingHealth] = await Promise.all([
      api(`/api/events/chronological?sportId=${state.sportId}&subscribedOnly=true`),
      state.sportId === 2 ? api('/api/spreadex/meeting-health').catch(() => []) : Promise.resolve([])
    ]);
    state.chronologicalRaces = events.filter(event => state.sportId === 4
      ? raceInsideGreyhoundTradingWindow(event.startTimeUnix)
      : raceInsideTradingWindow(event.startTimeUnix)).sort((left, right) =>
      left.startTimeUnix - right.startTimeUnix || left.eventId - right.eventId);
    state.spreadexMeetingHealth = meetingHealth;
    renderSpreadexMeetingHealth();
    renderChronologicalRaces();
  } catch (error) {
    chronologicalRaceList.innerHTML = `<p class="meeting-meta">${escapeHtml(error.message)}</p>`;
  } finally {
    state.chronologicalRacesLoading = false;
  }
}

function renderSpreadexMeetingHealth() {
  if (state.sportId !== 2 || !state.spreadexMeetingHealth.length) {
    spreadexMeetingHealth.innerHTML = '';
    return;
  }
  spreadexMeetingHealth.innerHTML = `<div class="spreadex-health-title">Spreadex books</div>${state.spreadexMeetingHealth.map(item => {
    const stale = (item.staleSelectionModels || 0) > 0;
    const ready = !stale && item.fixedPricedRaces === item.expectedRaces && item.expectedRaces > 0;
    const partial = item.fixedPricedRaces > 0 && !ready;
    return `<div class="spreadex-health-row ${ready ? 'ready' : partial ? 'partial' : 'missing'}"
                 title="${escapeHtml(item.state)}; ${item.initializedSelectionModels}/${item.selectionModels} price models initialised${item.alertRaces?.length ? `; ALERT: ${escapeHtml(item.alertRaces.join(', '))}` : ''}">
      <span>${escapeHtml(item.meeting)}</span>
      <strong>${stale ? `ALERT ${item.staleSelectionModels}` : `${item.fixedPricedRaces}/${item.expectedRaces}`}</strong>
      <small>I ${item.indexPricedRaces}/${item.expectedRaces}</small>
    </div>`;
  }).join('')}`;
}

function renderChronologicalRaces() {
  const unpriced = state.chronologicalRaces.filter(event => !event.betsApiPriced).length;
  el('raceColumnCount').textContent = unpriced
    ? `${state.chronologicalRaces.length} / ${unpriced} unpriced`
    : state.chronologicalRaces.length;
  chronologicalRaceList.innerHTML = state.chronologicalRaces.map(event => {
    const status = statusDetails(event.status);
    const day = raceDayDetails(event.startTimeUnix);
    const unpricedClass = event.betsApiPriced ? '' : ' unpriced';
    return `<div class="chronological-race-wrap"><a class="meeting-market-link" href="/meeting-markets.html?meeting=${encodeURIComponent(event.meeting)}" target="_blank" title="${escapeHtml(event.meeting)} meeting markets">Σ</a><button class="chronological-race${unpricedClass}${event.eventId === state.eventId ? ' active' : ''}"
                    data-chronological-event="${event.eventId}" data-chronological-meeting="${escapeHtml(event.meeting)}">
      <span class="chronological-race-time">${localTime(event.startTimeUnix)}</span>
      <span class="chronological-race-detail ${status.tone}">
        <strong>${escapeHtml(event.meeting)}</strong>
        <small>${escapeHtml(day.label)} / ${escapeHtml(status.label)}</small>
      </span>
    </button></div>`;
  }).join('') || '<p class="meeting-meta">No subscribed races found.</p>';

  chronologicalRaceList.querySelectorAll('[data-chronological-event]').forEach(button =>
    button.addEventListener('click', async () => {
      const meeting = state.meetings.find(item => item.name === button.dataset.chronologicalMeeting);
      if (!meeting) return;
      state.meeting = meeting;
      state.eventId = Number(button.dataset.chronologicalEvent);
      renderMeetings();
      renderChronologicalRaces();
      await loadEvents(true);
    }));
}

function renderForecastControls(data) {
  const config = data.marketConfiguration;
  const target = el('forecastControlBody');
  if (!config) { target.innerHTML = ''; return; }
  const locked = isResulted(data.event.status);
  if (state.forecastDraft.eventId !== data.event.eventId)
    state.forecastDraft = { eventId: data.event.eventId, runnerASelectionId: '', runnerBSelectionId: '' };
  const runnerOptions = selected => sortRunners(data.runners)
    .filter(runner => !runner.withdrawn)
    .map(runner => `<option value="${runner.selectionId}" ${String(runner.selectionId) === selected ? 'selected' : ''}>${escapeHtml(`${runner.identifier || '–'} ${runner.name}`)}</option>`)
    .join('');
  target.innerHTML = `<div class="quick-market-row">
    <div class="quick-market-name">Forecast</div>
    <div class="match-pair-builder">
      <select id="forecastRunnerA" aria-label="Forecast runner A" ${locked ? 'disabled' : ''}><option value="">Runner A</option>${runnerOptions(state.forecastDraft.runnerASelectionId)}</select>
      <span>v</span>
      <select id="forecastRunnerB" aria-label="Forecast runner B" ${locked ? 'disabled' : ''}><option value="">Runner B</option>${runnerOptions(state.forecastDraft.runnerBSelectionId)}</select>
      <button id="addForecast" class="button secondary" type="button">Add forecast</button>
    </div>
    <div class="match-pair-list quick-market-list">${config.forecasts?.length ? config.forecasts.map(pair => `<div class="match-pair ${pair.voided ? 'voided' : ''}">
      <span><strong>${escapeHtml(pair.runnerAName)}</strong> / <strong>${escapeHtml(pair.runnerBName)}</strong><small>#${pair.displayOrder}${pair.voided ? ` · Void: ${escapeHtml(pair.voidReason || 'non-runner')}` : ''}</small></span>
      <button type="button" data-delete-forecast="${pair.id}" ${locked ? 'disabled' : ''}>Remove</button>
    </div>`).join('') : '<span class="match-empty">No markets added.</span>'}</div>
  </div>`;
  el('forecastRunnerA').addEventListener('change', updateForecastDraft);
  el('forecastRunnerB').addEventListener('change', updateForecastDraft);
  el('addForecast').addEventListener('click', addForecast);
  target.querySelectorAll('[data-delete-forecast]').forEach(button =>
    button.addEventListener('click', () => deleteForecast(Number(button.dataset.deleteForecast))));
  updateAddForecastState(locked);
}

function renderMarketControls(data) {
  const config = data.marketConfiguration;
  const target = el('marketControlBody');
  if (!config) { target.innerHTML = ''; return; }
  const locked = isResulted(data.event.status);
  target.innerHTML = `<div class="market-toggle-grid">
    ${marketToggle('top3Enabled', 'Top 3', config.top3Enabled, locked)}
    ${marketToggle('insure2Enabled', 'Insure Bet 2', config.insure2Enabled, locked)}
    ${marketToggle('insure3Enabled', 'Insure Bet 3', config.insure3Enabled, locked)}
    ${marketToggle('insure4Enabled', 'Insure Bet 4', config.insure4Enabled, locked)}
  </div>`;
  target.querySelectorAll('[data-market-toggle]').forEach(input => input.addEventListener('change', saveMarketConfiguration));
}

function updateMatchBetDraft() {
  state.matchDraft = {
    eventId: state.eventId,
    runnerASelectionId: el('matchRunnerA').value,
    runnerBSelectionId: el('matchRunnerB').value
  };
  updateAddMatchBetState(false);
}

function updateAddMatchBetState(locked) {
  const button = el('addMatchBet');
  if (!button) return;
  const { runnerASelectionId, runnerBSelectionId } = state.matchDraft;
  button.disabled = locked || !runnerASelectionId || !runnerBSelectionId || runnerASelectionId === runnerBSelectionId;
}

function marketToggle(field, label, checked, disabled) {
  return `<label class="market-toggle"><input type="checkbox" data-market-toggle="${field}" ${checked ? 'checked' : ''} ${disabled ? 'disabled' : ''}><span>${escapeHtml(label)}</span></label>`;
}

async function saveMarketConfiguration() {
  const config = state.board.marketConfiguration;
  const readToggle = field => el('marketControlBody').querySelector(`[data-market-toggle="${field}"]`).checked;
  try {
    state.board.marketConfiguration = await api(`/api/events/${state.eventId}/market-configuration`, {
      method: 'PUT',
      body: JSON.stringify({
        top3Enabled: readToggle('top3Enabled'),
        insure2Enabled: readToggle('insure2Enabled'),
        insure3Enabled: readToggle('insure3Enabled'),
        insure4Enabled: readToggle('insure4Enabled')
      })
    });
    toast('Market configuration saved. The next price cycle will publish it to PROD.');
    renderMarketControls(state.board);
  } catch (error) {
    state.board.marketConfiguration = config;
    toast(error.message, true);
    renderMarketControls(state.board);
  }
}

async function addBettingWithout() {
  const excludedSelectionId = Number(el('bettingWithoutRunner')?.value);
  if (!excludedSelectionId) { toast('Choose the runner to remove.', true); return; }
  try {
    await api(`/api/events/${state.eventId}/betting-without`, {
      method: 'POST', body: JSON.stringify({ excludedSelectionId })
    });
    state.bettingWithoutDraft = { eventId: state.eventId, excludedSelectionId: '' };
    toast('Betting Without market added for the next PROD price cycle.');
    await loadBoard();
  } catch (error) { toast(error.message, true); }
}

async function deleteBettingWithout(id) {
  try {
    await api(`/api/events/${state.eventId}/betting-without/${id}`, { method: 'DELETE' });
    toast('Betting Without market removed.');
    await loadBoard();
  } catch (error) { toast(error.message, true); }
}

async function addMatchBet() {
  const runnerASelectionId = Number(el('matchRunnerA').value);
  const runnerBSelectionId = Number(el('matchRunnerB').value);
  if (!runnerASelectionId || !runnerBSelectionId) { toast('Choose both Match Bet runners.', true); return; }
  if (runnerASelectionId === runnerBSelectionId) { toast('Choose two different runners.', true); return; }
  try {
    await api(`/api/events/${state.eventId}/match-bets`, {
      method: 'POST', body: JSON.stringify({ runnerASelectionId, runnerBSelectionId })
    });
    state.matchDraft = { eventId: state.eventId, runnerASelectionId: '', runnerBSelectionId: '' };
    toast('Match Bet pair added for the next PROD price cycle.');
    await loadBoard();
  } catch (error) { toast(error.message, true); }
}

async function deleteMatchBet(pairId) {
  try {
    await api(`/api/events/${state.eventId}/match-bets/${pairId}`, { method: 'DELETE' });
    toast('Match Bet pair removed.');
    await loadBoard();
  } catch (error) { toast(error.message, true); }
}

async function addForecast() {
  const runnerASelectionId = Number(el('forecastRunnerA').value);
  const runnerBSelectionId = Number(el('forecastRunnerB').value);
  if (!runnerASelectionId || !runnerBSelectionId) { toast('Choose both Forecast runners.', true); return; }
  if (runnerASelectionId === runnerBSelectionId) { toast('Choose two different runners.', true); return; }
  try {
    await api(`/api/events/${state.eventId}/forecasts`, {
      method: 'POST', body: JSON.stringify({ runnerASelectionId, runnerBSelectionId })
    });
    state.forecastDraft = { eventId: state.eventId, runnerASelectionId: '', runnerBSelectionId: '' };
    toast('Forecast market added and published to PROD.');
    await loadBoard();
  } catch (error) { toast(error.message, true); }
}

async function deleteForecast(pairId) {
  try {
    await api(`/api/events/${state.eventId}/forecasts/${pairId}`, { method: 'DELETE' });
    toast('Forecast market removed.');
    await loadBoard();
  } catch (error) { toast(error.message, true); }
}

function renderPrice(cell, withdrawn, eventStatus, runner = null, marketName = '') {
  const rule4 = withdrawn && cell?.rule4TimeUtc
    ? `<span class="cell-rule4"><strong>R4 ${rule4Text(cell.rule4Deduction)}</strong><small>${escapeHtml(formatRuleTime(cell.rule4TimeUtc))}</small></span>`
    : '';
  if (withdrawn) {
    const result = resultDetails(cell?.result, eventStatus);
    return `<td><span class="price off">VOID</span>${rule4}${resultTag(result)}</td>`;
  }
  if (marketName === '50-25-10 Index' || marketName === '50-30-20-10 Index') {
    const spreadexQuote = runner?.spreadexIndexMarketName === marketName
      ? makeIndexQuote(runner?.spreadexIndex502510Sell, runner?.spreadexIndex502510Buy) : null;
    const betfredValue = marketName === '50-25-10 Index'
      ? Number(runner?.betfredIndex502510)
      : Number(runner?.betfredIndex50302010);
    const betfredQuote = betfredValue > 0
      ? makeCentredIndexQuote(betfredValue, spreadexQuote?.width ?? 2, spreadexQuote?.tick ?? indexTick(betfredValue))
      : null;
    const ourQuote = Number(cell?.price) > 0
      ? makeCentredIndexQuote(Number(cell.price), spreadexQuote?.width ?? 2, spreadexQuote?.tick ?? 1)
      : null;
    const values = [
      ourQuote ? { logo: 'RT', label: 'RacingTrader', quote: ourQuote, value: ourQuote.midpoint, cls: 'our', time: null } : null,
      spreadexQuote ? { logo: 'S', label: 'Spreadex', quote: spreadexQuote, value: spreadexQuote.midpoint, cls: 'spreadex', time: runner.spreadexIndex502510UpdatedAtUtc, suspended: runner.spreadexIndexSuspended } : null,
      betfredQuote ? { logo: 'BF', label: 'Betfred calculated', quote: betfredQuote, value: betfredQuote.midpoint, cls: 'betfred', time: runner.betfredUpdatedAtUtc } : null
    ].filter(Boolean).sort((a, b) => b.value - a.value);
    if (!values.length) return `<td><span class="price off">—</span>${rule4}</td>`;
    const stack = values.map(item => `<span class="index-source ${item.suspended ? 'suspended' : ''}" title="${item.suspended ? 'Spreadex market suspended · ' : ''}${item.time ? escapeHtml(formatSourceDateTime(item.time)) : 'RacingTrader calculated using Spreadex width and tick'}"><span class="book-logo ${item.cls}">${item.logo}</span>${formatIndexQuote(item.quote)}${item.suspended ? '<em>(S)</em>' : ''}${item.time ? ` <small>${escapeHtml(formatSourceTime(item.time))}</small>` : ''}</span>`).join('');
    return `<td><span class="index-price-stack">${stack}</span>${rule4}</td>`;
  }
  if (!cell || !cell.price || cell.price > 1001) return `<td><span class="price off">—</span>${rule4}</td>`;
  let move = '';
  if (cell.marketName !== 'Race Winner' && cell.previousPrice > 1 && cell.previousPrice !== cell.price) {
    const shortened = cell.price < cell.previousPrice;
    move = `<span class="price-move ${shortened ? 'down' : 'up'}">${shortened ? '▼' : '▲'} ${cell.previousPrice.toFixed(2)}</span>`;
  }
  const result = resultDetails(cell.result, eventStatus);
  return `<td><span class="price ${withdrawn || !cell.active || !cell.display ? 'off' : ''}">${cell.price.toFixed(2)}</span>${rule4}${move}${resultTag(result)}</td>`;
}

function indexTick(...values) {
  const decimals = values.reduce((maximum, value) => {
    const text = String(Number(value));
    return Math.max(maximum, text.includes('.') ? text.split('.')[1].length : 0);
  }, 0);
  return decimals === 0 ? 1 : decimals === 1 ? .1 : .01;
}

function makeIndexQuote(lowerValue, upperValue) {
  const first = Number(lowerValue);
  const second = Number(upperValue);
  if (!(first >= 0) || !(second >= 0) || first === second) return null;
  const lower = Math.min(first, second);
  const upper = Math.max(first, second);
  const tick = indexTick(lower, upper);
  return { lower, upper, width: upper - lower, midpoint: (lower + upper) / 2, tick };
}

function makeCentredIndexQuote(value, width, tick) {
  // RacingTrader index values must snap to Spreadex's quote ladder; the
  // width of another runner's live quote is not necessarily valid here.
  const candidates = [
    { lower: .1, upper: 1, tick: .1 },
    { lower: .5, upper: 2, tick: .1 },
    { lower: 1, upper: 3, tick: 1 }
  ];
  for (let lower = 2; lower <= 7; lower += 1)
    candidates.push({ lower, upper: lower + 2, tick: 1 });
  for (let lower = 8; lower <= 100; lower += 1)
    candidates.push({ lower, upper: lower + 3, tick: 1 });

  const quote = candidates.reduce((best, candidate) => {
    const distance = Math.abs(value - (candidate.lower + candidate.upper) / 2);
    return !best || distance < best.distance ? { ...candidate, distance } : best;
  }, null);
  return {
    lower: quote.lower,
    upper: quote.upper,
    width: quote.upper - quote.lower,
    midpoint: (quote.lower + quote.upper) / 2,
    tick: quote.tick
  };
}

function formatIndexQuote(quote) {
  const precision = quote.tick >= 1 ? 0 : quote.tick >= .1 ? 1 : 2;
  return `${quote.lower.toFixed(precision)}–${quote.upper.toFixed(precision)}`;
}

function betfairStateText() {
  const status = state.betfairStatus;
  if (!status) return 'Betfair status unavailable';
  if (!status.enabled) return 'Betfair delayed feed: disabled';
  if (!status.configured) return 'Betfair delayed feed: credentials required';
  if (status.lastError) return `Betfair delayed feed: ${status.lastError}`;
  return `Betfair delayed feed: ${status.matchedEvents} matched event${status.matchedEvents === 1 ? '' : 's'}`;
}

function betfairReferencePrice(price) {
  if (!price) return null;
  const back = Number(price.bestBackPrice);
  const lay = Number(price.bestLayPrice);
  const traded = Number(price.lastTradedPrice);
  if (back > 1 && lay > 1) return 2 / ((1 / back) + (1 / lay));
  if (traded > 1) return traded;
  if (back > 1) return back;
  if (lay > 1) return lay;
  return null;
}

function betfairPriceState(price) {
  if (!price) return { stale: false, ageSeconds: null };
  const captured = sourceDate(price.capturedAtUtc);
  if (!captured) return { stale: false, ageSeconds: null };
  const ageSeconds = Math.max(0, Math.round((Date.now() - captured.getTime()) / 1000));
  const maximumAge = state.betfairStatus?.maximumPriceAgeSeconds ?? 240;
  return { stale: ageSeconds > maximumAge, ageSeconds };
}

function renderBetfairPrice(price) {
  if (!price) {
    const status = state.betfairStatus;
    const label = !status?.enabled ? 'Disabled' : !status?.configured ? 'Credentials required' : 'Awaiting match';
    return `<td class="betfair-col"><span class="betfair-empty">${escapeHtml(label)}</span></td>`;
  }
  const freshness = betfairPriceState(price);
  const back = Number(price.bestBackPrice) > 1 ? Number(price.bestBackPrice).toFixed(2) : '\u2014';
  const lay = Number(price.bestLayPrice) > 1 ? Number(price.bestLayPrice).toFixed(2) : '\u2014';
  const traded = Number(price.lastTradedPrice) > 1 ? Number(price.lastTradedPrice).toFixed(2) : '\u2014';
  const meta = [
    price.inPlay ? 'IN-PLAY' : price.marketStatus,
    price.isDelayed ? 'DELAYED' : 'LIVE',
    freshness.stale ? 'STALE' : formatSourceTime(price.capturedAtUtc)
  ].filter(Boolean).join(' \u00b7 ');
  return `<td class="betfair-col ${freshness.stale ? 'stale' : ''}" title="Captured ${escapeHtml(formatSourceDateTime(price.capturedAtUtc))}">
    <span class="betfair-quotes"><span class="back">B ${back}</span><span class="lay">L ${lay}</span></span>
    <span class="betfair-ltp">LTP ${traded}</span>
    <span class="betfair-meta">${escapeHtml(meta)}</span>
  </td>`;
}

function renderBetfairDifference(runner) {
  const ourPrice = Number(runner.prices?.['Race Winner']?.price);
  const exchangePrice = betfairReferencePrice(runner.betfair);
  const freshness = betfairPriceState(runner.betfair);
  if (!(ourPrice > 1) || !(exchangePrice > 1) || freshness.stale)
    return '<td class="diff-col"><span class="price off">\u2014</span></td>';

  const oddsDifference = ourPrice - exchangePrice;
  const probabilityDifference = ((1 / ourPrice) - (1 / exchangePrice)) * 100;
  const absoluteProbabilityDifference = Math.abs(probabilityDifference);
  const tone = absoluteProbabilityDifference < .25 ? 'aligned' : absoluteProbabilityDifference < 1 ? 'watch' : 'out';
  const sign = value => value > 0 ? '+' : '';
  return `<td class="diff-col"><span class="price-diff ${tone}" title="Betfair reference ${exchangePrice.toFixed(2)}">
    <strong>${sign(oddsDifference)}${oddsDifference.toFixed(2)}</strong>
    <small>${sign(probabilityDifference)}${probabilityDifference.toFixed(2)} pp</small>
  </span></td>`;
}

function renderSourcePrices(runner, ladderMoves) {
  if (runner.withdrawn) return '<td class="source-compare-col"><span class="price off">VOID</span></td>';
  const rows = [
    { key: 'SPREADEX', label: 'Spreadex', logo: 'S', price: runner.spreadexPrice, updated: runner.spreadexUpdatedAtUtc },
    { key: 'BETAHOY', label: 'BetAhoy', logo: 'BA', price: runner.betAhoyPrice, updated: runner.betAhoyUpdatedAtUtc },
    { key: 'BETFRED', label: 'Betfred', logo: 'BF', price: runner.betfredPrice, updated: runner.betfredUpdatedAtUtc },
    { key: 'BET365_DIRECT', label: 'Bet365 walkthrough', logo: '365', price: runner.bet365DirectPrice, updated: runner.bet365DirectUpdatedAtUtc },
    { key: 'BETSAPI', label: 'BetsAPI', logo: 'API', price: runner.betsApiPrice, updated: runner.betsApiUpdatedAtUtc },
    { key: 'TIMEFORM_FORECAST', label: 'Timeform forecast · Trap Challenge only', logo: 'TF', price: runner.timeformForecastPrice, updated: runner.timeformForecastUpdatedAtUtc }
  ].filter(source => Number.isFinite(source.price) && source.price > 1).sort((a, b) => b.price - a.price);
  if (!rows.length) return '<td class="source-compare-col"><span class="price off">\u2014</span></td>';
  return `<td class="source-compare-col"><span class="source-prices">${rows.map(source => {
    const selected = source.key !== 'TIMEFORM_FORECAST' && source.key === runner.ladderSource;
    const moves = selected && Number.isInteger(ladderMoves) ? `${ladderMoves >= 0 ? '↑' : '↓'}${Math.abs(ladderMoves)}` : '';
    const hover = `${source.label} ${source.price.toFixed(2)} · updated ${formatSourceDateTime(source.updated)}${moves ? ` · ladder ${moves.trim()}` : ''}${selected ? ' · used for derived markets' : ''}`;
    return `<span class="source-price ${selected ? 'selected' : ''}" title="${escapeHtml(hover)}"><span class="book-logo ${source.key.toLowerCase()}">${source.logo}</span><strong>${source.price.toFixed(2)}</strong>${moves ? `<small class="ladder-moves">${moves}</small>` : ''}</span>`;
    /* legacy verbose rendering retained below for source compatibility */
    return `<span class="source-price ${selected ? 'selected' : ''}" title="${escapeHtml(formatSourceDateTime(source.updated))}"><span class="book-logo ${source.key.toLowerCase()}">${source.logo}</span>${source.label} ${source.price.toFixed(2)}${moves} · ${escapeHtml(formatSourceTime(source.updated))}${selected ? ' · used' : ''}</span>`;
  }).join('')}</span></td>`;
}

function sourceDate(value) {
  if (!value) return null;
  const text = String(value);
  const normalized = /(?:Z|[+-]\d\d:\d\d)$/i.test(text) ? text : `${text}Z`;
  const parsed = new Date(normalized);
  return Number.isNaN(parsed.getTime()) ? null : parsed;
}

function formatSourceTime(value) {
  const date = sourceDate(value);
  return date ? date.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit', second: '2-digit' }) : 'time unknown';
}

function formatSourceDateTime(value) {
  const date = sourceDate(value);
  return date ? `${date.toLocaleDateString('en-GB')} ${date.toLocaleTimeString('en-GB')}` : 'Update time unavailable';
}

function formatBookPercentage(prices) {
  const valid = prices.filter(price => Number.isFinite(price) && price > 1 && price <= 1001);
  if (!valid.length) return '—';
  return `${valid.reduce((book, price) => book + (100 / price), 0).toFixed(2)}%`;
}

function marketBookFromRunners(runners, marketName) {
  return formatBookPercentage(runners
    .filter(runner => !runner.withdrawn)
    .map(runner => runner.prices?.[marketName])
    .filter(cell => cell?.display)
    .map(cell => Number(cell.price)));
}

function marketBookFromSelections(selections) {
  return formatBookPercentage(selections
    .filter(selection => selection.display)
    .map(selection => Number(selection.price)));
}

function formatIndexSum(values) {
  const valid = values.filter(value => Number.isFinite(value) && value >= 0);
  if (!valid.length) return '—';
  return `Σ ${valid.reduce((total, value) => total + value, 0).toFixed(2)}`;
}

function marketIndexSumFromRunners(runners, marketName) {
  return formatIndexSum(runners
    .filter(runner => !runner.withdrawn)
    .map(runner => runner.prices?.[marketName])
    .filter(cell => cell?.display)
    .map(cell => Number(cell.price)));
}

function updateForecastDraft() {
  state.forecastDraft = {
    eventId: state.eventId,
    runnerASelectionId: el('forecastRunnerA').value,
    runnerBSelectionId: el('forecastRunnerB').value
  };
  updateAddForecastState(false);
}

function updateAddForecastState(locked) {
  const button = el('addForecast');
  if (!button) return;
  const { runnerASelectionId, runnerBSelectionId } = state.forecastDraft;
  button.disabled = locked || !runnerASelectionId || !runnerBSelectionId ||
    runnerASelectionId === runnerBSelectionId;
}

function formatIndexBookTotal(value) {
  if (!Number.isFinite(value)) return '—';
  return Number(value.toFixed(2)).toString();
}

function marketIndexBooksFromRunners(runners, marketName) {
  const spreadexQuotes = [];
  const racingTraderQuotes = [];
  const betfredQuotes = [];
  runners.filter(runner => !runner.withdrawn).forEach(runner => {
    const cell = runner.prices?.[marketName];
    if (!cell?.display || !Number.isFinite(Number(cell.price))) return;
    const spreadexQuote = runner.spreadexIndexMarketName === marketName
      ? makeIndexQuote(runner.spreadexIndex502510Sell, runner.spreadexIndex502510Buy)
      : null;
    if (spreadexQuote) spreadexQuotes.push(spreadexQuote);
    const midpoint = Number(cell.price);
    const ownQuote = spreadexQuote
      ? makeCentredIndexQuote(midpoint, spreadexQuote.width, spreadexQuote.tick)
      : makeCentredIndexQuote(midpoint, 2, indexTick(midpoint));
    if (ownQuote) racingTraderQuotes.push(ownQuote);
    const betfredValue = marketName === '50-25-10 Index'
      ? Number(runner.betfredIndex502510)
      : Number(runner.betfredIndex50302010);
    const betfredQuote = betfredValue > 0
      ? makeCentredIndexQuote(betfredValue, spreadexQuote?.width ?? 2, spreadexQuote?.tick ?? indexTick(betfredValue))
      : null;
    if (betfredQuote) betfredQuotes.push(betfredQuote);
  });
  const range = quotes => quotes.length
    ? `${formatIndexBookTotal(quotes.reduce((sum, quote) => sum + quote.lower, 0))}-${formatIndexBookTotal(quotes.reduce((sum, quote) => sum + quote.upper, 0))}`
    : '—';
  return { spreadex: range(spreadexQuotes), racingTrader: range(racingTraderQuotes), betfred: range(betfredQuotes) };
}

function marketMetricFromSelections(market) {
  return market.name.endsWith(' Index')
    ? formatIndexSum(market.selections.filter(selection => selection.display).map(selection => Number(selection.price)))
    : marketBookFromSelections(market.selections);
}

function renderDerivedMarkets(data) {
  const section = el('derivedMarkets');
  const grid = el('derivedMarketGrid');
  if (!data.derivedMarkets?.length) {
    section.classList.add('hidden');
    grid.innerHTML = '';
    return;
  }

  section.classList.remove('hidden');
  grid.innerHTML = data.derivedMarkets.map(market => {
    const adjustments = (data.marketAdjustments || []).filter(item => item.marketId === market.marketId && item.deduction > 0);
    return `<article class="derived-market-card">
    <header class="derived-market-header">
      <div>
        <h4>${escapeHtml(market.name)} <span class="market-book">${marketMetricFromSelections(market)}</span></h4>
        <div class="derived-market-states">
          ${stateBadge(market.active ? 'Active' : 'Suspended', market.active ? 'good' : 'bad')}
          ${stateBadge(market.display ? 'Displayed' : 'Hidden', market.display ? 'good' : 'bad')}
        </div>
      </div>
      <span class="derived-market-order">#${market.displayOrder}${adjustments.length ? `<small>R4 ${rule4Text(Math.min(.9, adjustments.reduce((sum, item) => sum + item.deduction, 0)))}</small>` : ''}</span>
    </header>
    ${adjustments.length ? `<div class="market-adjustments">${adjustments.map(item => `${escapeHtml(item.runnerName)} · ${rule4Text(item.deduction)} · ${escapeHtml(formatRuleTime(item.appliedAtUtc))}`).join('<br>')}</div>` : ''}
    <div>${market.selections.map(selection => {
      const result = resultDetails(selection.result, data.event.status);
      const price = selection.price > 1 && selection.price <= 1001 ? selection.price.toFixed(2) : '—';
      const isIndexMarket = market.name.endsWith(' Index');
      const sourceRunner = data.runners.find(runner => runner.name.toLowerCase() === selection.name.toLowerCase());
      const spreadexIndexQuote = isIndexMarket && sourceRunner?.spreadexIndexMarketName === market.name
        ? makeIndexQuote(sourceRunner?.spreadexIndex502510Sell, sourceRunner?.spreadexIndex502510Buy)
        : null;
      const ourIndexQuote = isIndexMarket && Number(selection.price) > 0
        ? makeCentredIndexQuote(Number(selection.price), spreadexIndexQuote?.width ?? 2, spreadexIndexQuote?.tick ?? indexTick(Number(selection.price)))
        : null;
      const betfredValue = market.name === '50-25-10 Index'
        ? Number(sourceRunner?.betfredIndex502510)
        : Number(sourceRunner?.betfredIndex50302010);
      const betfredIndexQuote = isIndexMarket && betfredValue > 0
        ? makeCentredIndexQuote(betfredValue, spreadexIndexQuote?.width ?? 2, spreadexIndexQuote?.tick ?? indexTick(betfredValue))
        : null;
      const indexSources = [
        ourIndexQuote ? { logo: 'RT', quote: ourIndexQuote, value: ourIndexQuote.midpoint, cls: 'our', time: null } : null,
        spreadexIndexQuote ? { logo: 'S', quote: spreadexIndexQuote, value: spreadexIndexQuote.midpoint, cls: 'spreadex', time: sourceRunner.spreadexIndex502510UpdatedAtUtc, suspended: sourceRunner.spreadexIndexSuspended } : null,
        betfredIndexQuote ? { logo: 'BF', quote: betfredIndexQuote, value: betfredIndexQuote.midpoint, cls: 'betfred', time: sourceRunner?.betfredUpdatedAtUtc } : null
      ].filter(Boolean);
      const comparison = isIndexMarket && indexSources.length
        ? indexSources
            .sort((a, b) => b.value - a.value)
            .map(item => `<span class="index-source ${item.suspended ? 'suspended' : ''}" title="${item.suspended ? 'Spreadex market suspended · ' : ''}${item.time ? escapeHtml(formatSourceDateTime(item.time)) : 'RacingTrader calculated using Spreadex width and tick'}"><span class="book-logo ${item.cls}">${item.logo}</span>${formatIndexQuote(item.quote)}${item.suspended ? '<em>(S)</em>' : ''}${item.time ? ` <small>${escapeHtml(formatSourceTime(item.time))}</small>` : ''}</span>`).join('')
        : price;
      return `<div class="derived-selection ${selection.display ? '' : 'hidden-selection'}">
        <span class="runner-number">${escapeHtml(selection.identifier || '–')}</span>
        <span><span class="derived-selection-name">${escapeHtml(selection.name)}</span><span class="derived-selection-meta">${selection.active ? 'Active' : 'Suspended'}${selection.display ? '' : ' · Hidden'}</span></span>
        <span class="derived-selection-price">${comparison}${resultTag(result)}</span>
      </div>`;
    }).join('')}</div>
  </article>`;
  }).join('');
}

function formatRuleTime(value) {
  const normalized = value.endsWith('Z') ? value : `${value}Z`;
  const date = new Date(normalized);
  return Number.isNaN(date.getTime()) ? value : `${date.toLocaleDateString('en-GB')} ${date.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit', second: '2-digit' })}`;
}

function openAction(action, selectionId) {
  const runner = state.board.runners.find(x => x.selectionId === selectionId);
  state.pendingAction = { action, runner };
  const replacing = action === 'replace';
  el('dialogEyebrow').textContent = replacing ? 'Greyhound replacement' : 'Horse withdrawal';
  el('dialogTitle').textContent = replacing ? `Replace ${runner.name}` : `Withdraw ${runner.name}?`;
  el('dialogDescription').textContent = replacing
    ? `The replacement keeps trap ${runner.identifier || '—'} active and records the old and new dog names in the audit trail.`
    : 'The runner will be marked withdrawn. Rule 4 is calculated from its last valid market price and timestamped in UTC.';
  el('replacementFields').classList.toggle('hidden', !replacing);
  el('reasonField').classList.toggle('hidden', replacing);
  el('confirmAction').textContent = replacing ? 'Save replacement' : 'Withdraw & apply Rule 4';
  el('confirmAction').className = `button ${replacing ? 'replace' : 'danger'}`;
  el('replacementName').value = '';
  el('replacementPrice').value = '';
  el('withdrawalReason').value = '';
  dialog.showModal();
  if (replacing) setTimeout(() => el('replacementName').focus(), 50);
}

el('actionForm').addEventListener('submit', async event => {
  event.preventDefault();
  const pending = state.pendingAction;
  if (!pending) return;
  try {
    let receipt;
    if (pending.action === 'withdraw') {
      receipt = await api(`/api/events/${state.eventId}/runners/${pending.runner.selectionId}/withdraw`, {
        method: 'POST', body: JSON.stringify({ reason: el('withdrawalReason').value || null })
      });
      toast(`${receipt.runnerName} withdrawn · Rule 4 ${rule4Text(receipt.rule4Deduction)}`);
    } else {
      const name = el('replacementName').value.trim();
      if (!name) { el('replacementName').focus(); return; }
      const priceValue = el('replacementPrice').value;
      receipt = await api(`/api/events/${state.eventId}/runners/${pending.runner.selectionId}/replace`, {
        method: 'POST', body: JSON.stringify({ name, price: priceValue ? Number(priceValue) : null })
      });
      toast(`${receipt.runnerName} replaced by ${receipt.replacementName}.`);
    }
    dialog.close();
    await loadBoard();
  } catch (error) {
    toast(error.message, true);
  }
});

el('closeDialog').addEventListener('click', () => dialog.close());
el('cancelDialog').addEventListener('click', () => dialog.close());

function showEmpty(title, copy) {
  boardElement.classList.add('hidden');
  emptyState.classList.remove('hidden');
  emptyState.querySelector('h3').textContent = title;
  emptyState.querySelector('p').textContent = copy;
}

function clearWorkspace() {
  state.meeting = null;
  state.events = [];
  state.eventId = null;
  state.matchDraft = { eventId: null, runnerASelectionId: '', runnerBSelectionId: '' };
  el('selectedMeeting').textContent = 'Select a meeting';
  el('bet365WalkButton').classList.add('hidden');
  eventTabs.innerHTML = '';
  showEmpty('Choose a subscribed meeting', 'Subscribe from the meetings list, then select an event to see every runner and market price.');
}

document.querySelectorAll('.sport-tab').forEach(button => button.addEventListener('click', async () => {
  state.sportId = Number(button.dataset.sport);
  el('preEventFeedLink').href = `/api/feed/pre-event?sportId=${state.sportId}&page=1`;
  document.querySelectorAll('.sport-tab').forEach(tab => {
    const active = tab === button;
    tab.classList.toggle('active', active);
    tab.setAttribute('aria-selected', String(active));
  });
  clearWorkspace();
  await loadMeetings(false);
}));

document.querySelectorAll('[data-meeting-filter]').forEach(button => button.addEventListener('click', async () => {
  state.meetingFilter = button.dataset.meetingFilter;
  document.querySelectorAll('[data-meeting-filter]').forEach(item => item.classList.toggle('active', item === button));
  await loadMeetings(true);
}));

el('runnerSortMode').value = state.runnerSortMode;
el('runnerSortMode').addEventListener('change', event => {
  state.runnerSortMode = event.target.value === 'price' ? 'price' : 'number';
  try { localStorage.setItem(runnerSortStorageKey, state.runnerSortMode); } catch { /* storage unavailable */ }
  if (state.board) renderBoard();
});

el('refreshButton').addEventListener('click', async () => {
  el('refreshButton').classList.add('loading');
  await loadMeetings(true);
  if (state.eventId) await loadBoard();
  await loadActivity();
  el('refreshButton').classList.remove('loading');
});

el('feedRefreshButton').addEventListener('click', async () => {
  const button = el('feedRefreshButton');
  button.classList.add('loading');
  button.textContent = 'Refreshing…';
  try {
    const result = await api('/api/admin/refresh-feeds', { method: 'POST' });
    toast(`Feed refresh queued: ${result.spreadexModels} Spreadex models, ${result.queuedEvents} events.`);
    await loadBetfredOffStatus();
    await loadActivity();
  } catch (error) {
    toast(`Feed refresh failed: ${error.message}`, true);
  } finally {
    button.classList.remove('loading');
    button.textContent = 'Refresh feeds';
  }
});

el('bet365WalkButton').addEventListener('click', async () => {
  if (!state.meeting) return;
  const button = el('bet365WalkButton');
  const original = button.textContent;
  button.disabled = true;
  button.classList.add('loading');
  button.textContent = 'Walking Bet365…';
  try {
    const result = await api(`/api/admin/bet365/walk-meeting/${encodeURIComponent(state.meeting.name)}`, { method: 'POST' });
    const capture = result.capture || {};
    toast(`Bet365 walk complete: ${capture.pricedRaceCount ?? 0}/${capture.raceCount ?? 0} races priced; ${result.queuedEvents} queued.`);
    await new Promise(resolve => setTimeout(resolve, 3000));
    await loadEvents(true);
    if (state.eventId) await loadBoard();
  } catch (error) {
    toast(`Bet365 walk failed: ${error.message}`, true);
  } finally {
    button.disabled = false;
    button.classList.remove('loading');
    button.textContent = original;
  }
});

el('eventRepriceButton').addEventListener('click', async () => {
  if (!state.eventId) return;
  const button = el('eventRepriceButton');
  button.classList.add('loading');
  button.textContent = 'Queued…';
  try {
    await api(`/api/admin/events/${state.eventId}/reprice`, { method: 'POST' });
    toast(`Fresh provider prices queued for event ${state.eventId}.`);
  } catch (error) {
    toast(`Race reprice failed: ${error.message}`, true);
  } finally {
    window.setTimeout(() => {
      button.classList.remove('loading');
      button.textContent = 'Reprice race';
    }, 1000);
  }
});

async function loadDiskSpace() {
  const badge = el('diskSpaceBadge');
  if (!badge) return;
  try {
    const disk = await api('/api/disk-space');
    if (!Number.isFinite(disk.freeBytes) || disk.level === 'unavailable') throw new Error('Unavailable');
    const level = ['ok', 'warning', 'critical'].includes(disk.level) ? disk.level : 'unavailable';
    badge.className = `disk-space-badge ${level}`;
    const prefix = level === 'critical' ? 'CRITICAL · ' : level === 'warning' ? 'LOW SPACE · ' : '';
    badge.textContent = `${prefix}C: ${(disk.freeBytes / 1073741824).toFixed(1)} GiB free (${disk.freePercent.toFixed(1)}%)`;
    badge.title = `Trader server C: drive. ${(disk.totalBytes / 1073741824).toFixed(1)} GiB total. Warning below 20 GiB or 10%; critical below 5 GiB or 5%. Checked ${new Date(disk.checkedAtUtc).toLocaleTimeString()}. Refreshes every 5 minutes.`;
  } catch {
    badge.className = 'disk-space-badge unavailable';
    badge.textContent = 'C: space unavailable';
    badge.title = 'Unable to read the trader server disk space. Retrying every 5 minutes.';
  }
}

async function start() {
  loadDiskSpace();
  setInterval(loadDiskSpace, 300000);
  try {
    const status = await api('/api/status');
    const environment = String(status.environment || 'UNKNOWN').toUpperCase();
    el('environmentBadge').innerHTML = `<i></i> ${escapeHtml(environment)} ${status.productionPublishing ? 'LIVE' : 'SAFE'}`;
    setConnection('online', status.productionPublishing ? 'PROD connected' : 'Live database');
  } catch (error) {
    setConnection('error', error.message);
  }
  await loadMeetings(false);
  // Populate meetings and races first. Monitor queries are useful but must
  // never hold the trading navigation blank when a diagnostic query is slow.
  await Promise.allSettled([loadActivity(), loadBetfredOffStatus(), loadBetfredPricingStatus(), loadSuspensionSentinelStatus()]);
  state.refreshTimer = setInterval(async () => {
    // Re-rendering replaces native select elements and closes an open menu.
    // Pause the live board refresh while any configuration control has focus.
    const active = document.activeElement;
    const editingConfiguration = active?.matches?.(
      '#bettingWithoutRunner, #matchRunnerA, #matchRunnerB, #forecastRunnerA, #forecastRunnerB, #marketControlBody input, #bettingWithoutControlBody button'
    );
    if (state.eventId && !editingConfiguration) await loadBoard();
  }, 1000);
  state.activityTimer = setInterval(loadActivity, 10000);
  state.raceColumnTimer = setInterval(loadChronologicalRaces, 30000);
  setInterval(() => document.querySelectorAll('[data-off-unix]').forEach(node => {
    node.textContent = suspensionCountdown(node.dataset.offUnix);
  }), 1000);
  state.spreadexTimer = setInterval(loadBetfredOffStatus, 2000);
  setInterval(loadSuspensionSentinelStatus, 2000);
  setInterval(loadBetfredPricingStatus, 5000);
  document.addEventListener('visibilitychange', () => {
    if (!document.hidden) {
      loadBoard();
      loadActivity();
      loadBetfredOffStatus();
      loadSuspensionSentinelStatus();
      loadBetfredPricingStatus();
      loadChronologicalRaces();
    }
  });
}

start();
