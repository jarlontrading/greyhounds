const text = (id, value) => document.getElementById(id).textContent = value;
const when = value => value ? new Date(value).toLocaleString() : '—';
function rows(id, values) {
  document.getElementById(id).replaceChildren(...values.map(values => {
    const row = document.createElement('tr');
    for (const value of values) { const cell = document.createElement('td'); cell.textContent = value; row.append(cell); }
    return row;
  }));
}
async function refresh() {
  try {
    const response = await fetch('/api/distance-review', {cache:'no-store'});
    if (!response.ok) throw new Error(`Review unavailable (${response.status})`);
    const data = await response.json();
    text('status', `${data.tableCount} track/distance price tables · Last poll: ${when(data.lastPollUtc)} · Refreshes every 15 seconds`);
    text('note', data.timingNote); text('error', data.lastError || '');
    rows('tracks', data.tracks.map(t => [t.track,t.observed,t.margins,t.pending,t.timedSamples,t.averageSeconds?.toFixed(1) ?? '—',t.maxSeconds?.toFixed(1) ?? '—']));
    rows('races', data.races.map(r => [when(r.startTimeUnix*1000),r.track,r.metres,r.prices ? '2001 · 5 selections' : 'No matching prices',r.margin ?? 'Pending',when(r.actualOffUtc),when(r.marginSeenUtc),r.timedSample ? 'Yes' : 'Excluded / pending']));
  } catch (error) { text('error', error.message); }
}
refresh(); setInterval(refresh,15000);
