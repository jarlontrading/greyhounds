const state = {
  rows: [],
  loading: false,
  loadedAt: null,
  meetingSignature: ''
};

const el = id => document.getElementById(id);
const html = value => String(value ?? '').replace(/[&<>"']/g, character => ({
  '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
}[character]));
const number = value => {
  if (value === null || value === undefined || value === '') return null;
  return Number.isFinite(Number(value)) ? Number(value) : null;
};
const utcDate = value => {
  if (!value) return null;
  const text = String(value);
  const parsed = new Date(/[zZ]|[+-]\d\d:\d\d$/.test(text) ? text : `${text}Z`);
  return Number.isNaN(parsed.getTime()) ? null : parsed;
};
const dateTime = unix => new Date(Number(unix) * 1000);
const time = unix => new Intl.DateTimeFormat('en-GB', {
  hour: '2-digit', minute: '2-digit', hour12: false
}).format(dateTime(unix));
const dateLabel = unix => new Intl.DateTimeFormat('en-GB', {
  weekday: 'short', day: '2-digit', month: 'short'
}).format(dateTime(unix));
const stamp = value => {
  const parsed = utcDate(value);
  return parsed ? parsed.toLocaleTimeString('en-GB', {
    hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false
  }) : '--:--:--';
};
const age = value => {
  const parsed = utcDate(value);
  if (!parsed) return 'no update';
  const seconds = Math.max(0, Math.round((Date.now() - parsed.getTime()) / 1000));
  if (seconds < 60) return `${seconds}s ago`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ${seconds % 60}s ago`;
  return `${Math.floor(seconds / 3600)}h ${Math.floor((seconds % 3600) / 60)}m ago`;
};
const odds = value => {
  const parsed = number(value);
  return parsed !== null && parsed > 1 ? parsed.toFixed(2) : '--';
};
const statusIsSettled = row => ['3', 'resulted', 'post game'].includes(String(row.eventStatus).toLowerCase());

function providerCell(price, updated, observed, className, comparison) {
  const parsed = number(price);
  const priceClass = parsed === null ? 'missing' :
    comparison === null ? '' : parsed > comparison ? 'higher' : parsed < comparison ? 'lower' : '';
  const observedText = observed ? `seen ${stamp(observed)}` : 'not observed';
  return `<td class="price ${className} ${priceClass}">
    ${odds(parsed)}
    <small>update ${stamp(updated)} &middot; ${html(age(updated))}</small>
    <small>${html(observedText)}</small>
  </td>`;
}

function spreadexCell(row) {
  const price = number(row.spreadexPrice);
  const sell = number(row.spreadexSell);
  const buy = number(row.spreadexBuy);
  const quote = sell !== null && buy !== null ? `${sell.toFixed(1)}-${buy.toFixed(1)} index` : 'no index quote';
  return `<td class="price spreadex ${price === null ? 'missing' : ''}">
    ${odds(price)}
    <small>${html(quote)}</small>
    <small>update ${stamp(row.spreadexUpdatedAtUtc)} &middot; ${html(age(row.spreadexUpdatedAtUtc))}</small>
  </td>`;
}

function visibleRows() {
  const meeting = el('meetingFilter').value;
  let rows = state.rows.filter(row => {
    if (meeting && row.meeting !== meeting) return false;
    if (!el('includeSettled').checked && statusIsSettled(row)) return false;
    const direct = number(row.bet365BrowserPrice);
    const api = number(row.betsApiPrice);
    const spreadex = number(row.spreadexPrice);
    const betfred = number(row.betfredPrice);
    if (el('differencesOnly').checked && (direct === null || api === null || direct === api)) return false;
    if (el('missingOnly').checked && direct !== null && api !== null && betfred !== null && spreadex !== null) return false;
    return true;
  });

  const sortMode = el('sortMode').value;
  rows.sort((left, right) => {
    if (sortMode === 'update-gap') {
      return Math.abs(number(right.updateLagSeconds) ?? -1) - Math.abs(number(left.updateLagSeconds) ?? -1)
        || right.startTimeUnix - left.startTimeUnix;
    }
    if (sortMode === 'latest') {
      return (utcDate(right.latestComparisonUpdateUtc)?.getTime() ?? 0)
        - (utcDate(left.latestComparisonUpdateUtc)?.getTime() ?? 0)
        || right.startTimeUnix - left.startTimeUnix;
    }
    if (sortMode === 'race') {
      return left.startTimeUnix - right.startTimeUnix
        || Number(left.identifier || 999) - Number(right.identifier || 999)
        || left.runnerName.localeCompare(right.runnerName);
    }
    const rightGap = number(right.absolutePriceDifference);
    const leftGap = number(left.absolutePriceDifference);
    return (rightGap ?? -1) - (leftGap ?? -1)
      || Math.abs(number(right.updateLagSeconds) ?? -1) - Math.abs(number(left.updateLagSeconds) ?? -1)
      || left.startTimeUnix - right.startTimeUnix;
  });
  return rows;
}

function render() {
  const rows = visibleRows();
  const body = el('comparisonRows');
  body.innerHTML = rows.length ? rows.map(row => {
    const direct = number(row.bet365BrowserPrice);
    const api = number(row.betsApiPrice);
    const difference = number(row.priceDifference);
    const probability = number(row.impliedProbabilityDifferencePoints);
    const lag = number(row.updateLagSeconds);
    const gapClass = difference === 0 ? 'same' : 'large';
    const leadClass = lag === null || lag === 0 ? '' : lag > 0 ? 'browser' : 'api';
    const leadText = lag === null ? 'No comparison' :
      lag === 0 ? 'Same timestamp' :
      `${lag > 0 ? 'Browser' : 'BetsAPI'} +${Math.abs(lag).toFixed(1)}s`;
    return `<tr class="${statusIsSettled(row) ? 'settled' : ''} ${row.withdrawn ? 'withdrawn' : ''}">
      <td class="race"><strong>${html(row.eventName)}</strong><small>${html(dateLabel(row.startTimeUnix))} &middot; ${html(time(row.startTimeUnix))} &middot; ${html(row.eventStatus)}</small></td>
      <td class="cloth">${html(row.identifier || '-')}</td>
      <td class="runner"><strong>${html(row.runnerName)}</strong><small>Event ${html(row.eventId)}${row.withdrawn ? ' &middot; withdrawn' : ''}</small></td>
      ${providerCell(direct, row.bet365BrowserUpdatedAtUtc, row.bet365BrowserObservedAtUtc, 'browser', api)}
      ${providerCell(api, row.betsApiUpdatedAtUtc, row.betsApiObservedAtUtc, 'api', direct)}
      ${providerCell(number(row.betfredPrice), row.betfredUpdatedAtUtc, row.betfredObservedAtUtc, 'betfred', direct)}
      <td class="provider">${number(row.betAhoy) === null ? '—' : html(number(row.betAhoy).toFixed(2))}</td>
      ${spreadexCell(row)}
      <td class="difference"><span class="${gapClass}">${difference === null ? '--' : `${difference > 0 ? '+' : ''}${difference.toFixed(2)}`}</span>
        <small>${probability === null ? 'no probability comparison' : `${probability > 0 ? '+' : ''}${probability.toFixed(2)} pp implied`}</small></td>
      <td class="lead ${leadClass}">${html(leadText)}
        <small>${lag === null ? 'both timestamps required' : `absolute gap ${Math.abs(lag).toFixed(1)}s`}</small></td>
      <td class="latest">${stamp(row.latestComparisonUpdateUtc)}<small>${html(age(row.latestComparisonUpdateUtc))}</small></td>
    </tr>`;
  }).join('') : '<tr><td colspan="11" class="empty">No runners match the current filters.</td></tr>';

  el('visibleCount').textContent = rows.length;
  el('differenceCount').textContent = rows.filter(row => number(row.absolutePriceDifference) > 0).length;
  el('missingCount').textContent = rows.filter(row =>
    number(row.bet365BrowserPrice) === null || number(row.betsApiPrice) === null ||
    number(row.betfredPrice) === null || number(row.spreadexPrice) === null).length;
  el('browserNewerCount').textContent = rows.filter(row => number(row.updateLagSeconds) > 0).length;
  el('apiNewerCount').textContent = rows.filter(row => number(row.updateLagSeconds) < 0).length;
}

function refreshMeetings() {
  const control = el('meetingFilter');
  const meetings = [...new Set(state.rows.map(row => row.meeting))].sort();
  const signature = meetings.join('|');
  if (signature === state.meetingSignature) return;
  state.meetingSignature = signature;
  const selected = control.value;
  control.innerHTML = '<option value="">All meetings</option>' +
    meetings.map(meeting => `<option value="${html(meeting)}">${html(meeting)}</option>`).join('');
  if (meetings.includes(selected)) control.value = selected;
}

async function load() {
  if (state.loading) return;
  state.loading = true;
  try {
    const response = await fetch(`/api/provider-price-comparison?includeTomorrow=${el('includeTomorrow').checked}`, {
      cache: 'no-store'
    });
    if (!response.ok) throw new Error(`${response.status} ${response.statusText}`);
    state.rows = await response.json();
    state.loadedAt = new Date();
    refreshMeetings();
    render();
    el('liveState').className = 'live-state live';
    el('liveState').innerHTML = '<i></i> Live &middot; 1 second refresh';
  } catch (error) {
    el('liveState').className = 'live-state error';
    el('liveState').innerHTML = `<i></i> ${html(error.message)}`;
  } finally {
    state.loading = false;
  }
}

['sortMode', 'meetingFilter', 'differencesOnly', 'missingOnly', 'includeSettled'].forEach(id =>
  el(id).addEventListener('change', render));
el('includeTomorrow').addEventListener('change', load);
el('refreshButton').addEventListener('click', load);
setInterval(() => {
  el('clock').textContent = new Date().toLocaleTimeString('en-GB', { hour12: false });
  if (state.rows.length) render();
}, 1000);
setInterval(load, 1000);
load();
