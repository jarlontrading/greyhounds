const state = { rows: [], loading: false, meetings: '' };
const el = id => document.getElementById(id);
const html = value => String(value ?? '').replace(/[&<>"']/g, character => ({
  '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
}[character]));
const utcDate = value => {
  if (!value) return null;
  const text = String(value);
  const parsed = new Date(/[zZ]|[+-]\d\d:\d\d$/.test(text) ? text : `${text}Z`);
  return Number.isNaN(parsed.getTime()) ? null : parsed;
};
const stamp = value => utcDate(value)?.toLocaleTimeString('en-GB', {
  hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false
}) ?? '--:--:--';
const raceTime = unix => new Date(Number(unix) * 1000).toLocaleTimeString('en-GB', {
  hour: '2-digit', minute: '2-digit', hour12: false
});
const quote = (bid, offer) => {
  const precision = Number.isInteger(Number(bid)) && Number.isInteger(Number(offer)) ? 0 : 1;
  return `${Number(bid).toFixed(precision)}–${Number(offer).toFixed(precision)}`;
};

function visibleRows() {
  const meeting = el('meetingFilter').value;
  return meeting ? state.rows.filter(row => row.raceName.startsWith(`${meeting} `)) : state.rows;
}

function refreshMeetings() {
  const meetings = [...new Set(state.rows.map(row => row.raceName.replace(/\s+\d{1,2}:\d{2}$/, '')))].sort();
  const signature = meetings.join('|');
  if (signature === state.meetings) return;
  state.meetings = signature;
  const control = el('meetingFilter');
  const selected = control.value;
  control.innerHTML = '<option value="">All meetings</option>' + meetings
    .map(meeting => `<option value="${html(meeting)}">${html(meeting)}</option>`).join('');
  if (meetings.includes(selected)) control.value = selected;
}

function render() {
  const rows = visibleRows();
  el('arbRows').innerHTML = rows.length ? rows.map(row => `<tr>
    <td class="race"><button type="button" class="race-button" data-event-id="${Number(row.eventId)}" data-race-name="${html(row.raceName)}">${html(row.raceName)}</button><small>${html(raceTime(row.startTimeUnix))}</small></td>
    <td class="cloth">${html(row.identifier || '-')}</td>
    <td class="runner"><span class="runner-with-silk">${row.assetUrl ? `<img class="arb-silk" src="${html(row.assetUrl)}" alt="">` : ''}<span><strong>${html(row.runnerName)}</strong><span class="market-tag">${html(row.marketName)}</span></span></span></td>
    <td class="quote">${html(quote(row.rtBid, row.rtOffer))}</td>
    <td class="quote">${html(quote(row.spreadexBid, row.spreadexOffer))}</td>
    <td class="update-time">${html(stamp(row.spreadexUpdatedAtUtc))}</td>
    <td class="arb-value ${Number(row.arbPoints) >= 0 ? 'positive' : 'negative'}"><strong>${Number(row.arbPoints) > 0 ? '+' : ''}${Number(row.arbPoints).toFixed(2)}</strong><small>points</small></td>
  </tr>`).join('') : '<tr><td colspan="7" class="empty">No RT / Spreadex index crosses.</td></tr>';
  el('arbRows').querySelectorAll('.race-button').forEach(button => button.addEventListener('click', () =>
    openRace(Number(button.dataset.eventId), button.dataset.raceName)));
  el('arbCount').textContent = rows.length;
  el('raceCount').textContent = new Set(rows.map(row => row.eventId)).size;
  el('largestArb').textContent = `${rows.length ? Math.abs(Number(rows[0].arbPoints)).toFixed(2) : '0.00'} pts`;
  const latest = rows.map(row => utcDate(row.spreadexUpdatedAtUtc)).filter(Boolean).sort((left, right) => right - left)[0];
  el('latestUpdate').textContent = latest ? stamp(latest.toISOString()) : '--:--:--';
}

function openRace(eventId, raceName) {
  const message = {
    type: 'racingtrader-open-event',
    eventId,
    meeting: String(raceName || '').replace(/\s+\d{1,2}:\d{2}$/, '')
  };
  let target = window.opener && !window.opener.closed ? window.opener : null;
  if (target) {
    target.postMessage(message, location.origin);
    target.focus();
    return;
  }
  target = window.open('/', 'racing-trader-main');
  if (target) {
    target.addEventListener('load', () => target.postMessage(message, location.origin), { once: true });
    target.focus();
  }
}

async function load() {
  if (state.loading) return;
  state.loading = true;
  try {
    const response = await fetch('/api/index-arbs', { cache: 'no-store' });
    if (!response.ok) throw new Error(`${response.status} ${response.statusText}`);
    state.rows = await response.json();
    refreshMeetings();
    render();
    el('liveState').className = 'live-state live';
    el('liveState').innerHTML = '<i></i> Live &middot; 1 second refresh';
  } catch (error) {
    el('liveState').className = 'live-state error';
    el('liveState').innerHTML = `<i></i> ${html(error.message)}`;
  } finally { state.loading = false; }
}

el('meetingFilter').addEventListener('change', render);
el('refreshButton').addEventListener('click', load);
setInterval(() => { el('clock').textContent = new Date().toLocaleTimeString('en-GB', { hour12: false }); }, 1000);
setInterval(load, 1000);
load();
