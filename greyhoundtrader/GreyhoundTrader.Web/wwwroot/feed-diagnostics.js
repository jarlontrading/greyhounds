const el=id=>document.getElementById(id);
const esc=value=>String(value??'').replace(/[&<>"']/g,ch=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[ch]));
const time=value=>value?new Date(value).toLocaleTimeString():'—';
async function api(url,options){const response=await fetch(url,options);if(!response.ok)throw new Error(`${response.status} ${await response.text()}`);return response.json();}
function card(label,value,kind=''){return `<div class="card"><span>${esc(label)}</span><strong class="${kind}">${esc(value)}</strong></div>`;}
async function load(){
  try{
    const d=await api('/api/feed-diagnostics');
    const stalled=d.lastCycleStartedAtUtc&&(!d.lastCycleCompletedAtUtc||new Date(d.lastCycleStartedAtUtc)>new Date(d.lastCycleCompletedAtUtc))&&Date.now()-new Date(d.lastCycleStartedAtUtc)>30000;
    el('statusGrid').innerHTML=[
      card('Stage',d.stage,stalled?'bad':'good'),card('Current race',d.currentRace||'—'),card('Event',d.currentEventId||'—'),
      card('Changed / queued',d.changedCandidates),card('Suspension checks',d.suspensionCandidates),card('Due polls',d.dueCandidates),
      card('Cycle batch',d.workBatch),card('Last cycle complete',time(d.lastCycleCompletedAtUtc),stalled?'bad':''),
      card('Last error',d.lastError||'none',d.lastError?'bad':'good')].join('');
    el('recentBody').innerHTML=(d.recent||[]).map(row=>`<tr><td>${time(row.atUtc)}</td><td>${esc(row.race||'—')}</td><td>${esc(row.eventId||'—')}</td><td>${esc(row.outcome)}</td><td class="error">${esc(row.error||'')}</td><td>${row.eventId?`<button data-reprice="${row.eventId}">Reprice</button>`:''}</td></tr>`).join('');
    document.querySelectorAll('[data-reprice]').forEach(button=>button.onclick=async()=>{await api(`/api/admin/events/${button.dataset.reprice}/reprice`,{method:'POST'});notice(`Queued ${button.dataset.reprice}`);});
  }catch(error){el('statusGrid').innerHTML=card('Diagnostics unavailable',error.message,'bad');}
}
function notice(message){el('message').textContent=message;el('message').style.display='block';setTimeout(()=>el('message').style.display='none',2500);}
el('globalRefresh').onclick=async()=>{try{const result=await api('/api/admin/refresh-feeds',{method:'POST'});notice(`Queued ${result.queuedEvents} events`);}catch(error){notice(`Refresh failed: ${error.message}`);}};
load();setInterval(load,2000);
