const params=new URLSearchParams(location.search),meeting=params.get('meeting')||'';
const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const spread=v=>v==null?'—':(Number.isInteger(v)?String(v):Number(v).toFixed(2).replace(/0+$/,'').replace(/\.$/,''));
fetch(`/api/meetings/racecard-spreads?meeting=${encodeURIComponent(meeting)}`).then(r=>{if(!r.ok)throw new Error(`${r.status} ${r.statusText}`);return r.json()}).then(data=>{
 document.title=`${data.meeting} meeting markets`;document.getElementById('meetingTitle').textContent=`${data.meeting} racecard markets`;document.getElementById('meetingStatus').textContent=`${data.races.length} races · aggregate is the sum of complete race quotes`;
 const markets=data.aggregate||data.races[0]?.markets||[];document.getElementById('meetingHead').innerHTML=`<tr><th>Race</th>${markets.map(m=>`<th title="${esc(m.helpText)}">${esc(m.name)}</th>`).join('')}</tr>`;
 const row=(label,list,cls='')=>`<tr class="${cls}"><td>${label}</td>${markets.map(m=>{const x=list.find(v=>v.key===m.key);return `<td title="${esc(m.helpText)}">${x?.bid==null?'—':`${spread(x.bid)}–${spread(x.offer)}`}</td>`}).join('')}</tr>`;
 document.getElementById('meetingBody').innerHTML=row('MEETING AGGREGATE',data.aggregate,'aggregate-row')+data.races.map(r=>row(`<a href="/?eventId=${r.eventId}" target="_blank">${esc(r.eventName)}</a><br><small>${esc(r.status)}</small>`,r.markets)).join('');
}).catch(e=>document.getElementById('meetingStatus').textContent=e.message);
