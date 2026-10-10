'use strict';
let snapshot=null, selectedScenario=null, selectedRun=null;
const $=id=>document.getElementById(id);
const element=(tag,text)=>{const n=document.createElement(tag); if(text!==undefined)n.textContent=text;return n;};
const date=value=>value?new Date(value).toISOString():'Unknown';
const badge=state=>{const n=element('span',state);n.className='badge '+state;return n;};
const matches=(r,s)=>r.scenarioId===s.scenarioId&&r.definitionVersion===s.definitionVersion&&r.scenarioSha256===s.sha256&&r.buildId===$('build').value&&r.environmentSha256===$('environment').value;
const order=(a,b)=>(!b.startedAt)-(!a.startedAt)||(Date.parse(b.startedAt)||0)-(Date.parse(a.startedAt)||0)||a.key.localeCompare(b.key);
function selection(s){const candidates=snapshot.runs.filter(r=>matches(r,s)).sort(order);const r=candidates[0];let state=r?r.state:'Unrun';if(candidates.length>1&&(candidates.some(c=>!c.startedAt)||candidates.filter(c=>c.startedAt===r.startedAt).length>1))state='LatestUnknown';if(snapshot.truncated||snapshot.warnings.length)state='IndexIncomplete';return {r,state};}
function button(text,action){const b=element('button',text);b.type='button';b.addEventListener('click',action);return b;}
function render(){
 if(!snapshot)return;
 const ready=snapshot.registryState==='Ready';
 $('notice').textContent=ready?((snapshot.truncated?'Index limit reached. Latest results and unrun status cannot be confirmed. ':'')+(snapshot.warnings.length?'Some artifact roots could not be read. ':'')+'Snapshot loaded. Refresh explicitly to reread files.'):'Registry could not be loaded: '+snapshot.registryState;
 $('scenarios').replaceChildren();
 const target=$('build').value&&$('environment').value;
 $('scope').textContent=target?'Current content hashes matched against this build and effective Environment.':'Choose a build and Environment. Unknown identities remain in history.';
 for(const s of snapshot.scenarios){if(!s.name.toLowerCase().includes($('search').value.toLowerCase()))continue;const {r,state}=target?selection(s):{r:null,state:'SelectTarget'};if($('status').value&&$('status').value!==state)continue;
 const row=element('tr');const name=element('td',s.name);name.append(element('small',s.scenarioId+' • '+s.definitionVersion));row.append(name);
 const status=element('td');status.append(badge(state));row.append(status,element('td',date(r?.startedAt)));
 const run=element('td');if(r)run.append(button(r.runId,()=>showRun(r.key)));else run.textContent='—';row.append(run);
 const history=element('td');history.append(button('View history',()=>{selectedScenario=s.scenarioId;renderHistory();}));row.append(history);$('scenarios').append(row);}
 if(!$('scenarios').children.length){const row=element('tr'),cell=element('td','No registered Scenarios match the filters.');cell.colSpan=5;row.append(cell);$('scenarios').append(row);}
 renderHistory();renderRun();$('all-runs').replaceChildren(...snapshot.runs.slice().sort(order).map(runCard));
}
function runCard(r){const card=element('div');card.className='card';card.append(badge(r.state),element('p',(r.scenarioId||'Unknown Scenario')+' • build '+(r.buildId||'Unknown')+' • '+date(r.startedAt)),button(r.runId,()=>showRun(r.key)));return card;}
function renderHistory(){const section=$('history-section');section.hidden=!selectedScenario;if(!selectedScenario)return;$('history-heading').textContent='History • '+selectedScenario;const runs=snapshot.runs.filter(r=>r.scenarioId===selectedScenario).sort(order);$('history').replaceChildren(...(runs.length?runs.map(runCard):[element('p','No indexed history for this Scenario.')]));}
function showRun(key){selectedRun=key;renderRun();$('run-section').scrollIntoView({block:'nearest'});}
function renderRun(){const r=snapshot.runs.find(r=>r.key===selectedRun);$('run-section').hidden=!r;if(!r)return;const area=$('run');area.replaceChildren(badge(r.state));const dl=element('dl');const fields={'Run':r.runId,'Scenario':r.scenarioId,'Definition':r.definitionVersion,'Scenario SHA-256':r.scenarioSha256,'Game build':r.buildId,'Environment SHA-256':r.environmentSha256,'Started':date(r.startedAt),'Saved result':r.result?.status,'Finished':date(r.result?.finishedAt),'Reason':r.result?.reason,'Phase / origin':r.result?r.result.phase+' / '+r.result.origin:null,'Postprocessing':r.result?(r.result.postProcessing.complete?'Complete':'Incomplete')+' • '+r.result.postProcessing.reasons.join(', '):null};for(const [label,value]of Object.entries(fields))dl.append(element('dt',label),element('dd',value||'Unknown'));area.append(dl);
 for(const d of r.details){const p=element('p',d.kind+': '+d.state+' ');if(d.report){const a=element('a','Open Gua Trace Viewer / HTML report');a.href=d.report;a.target='_blank';a.rel='noopener noreferrer';p.append(a);}else if(d.kind==='Trace'&&d.state==='Saved')p.append(element('span','— no saved common HTML report in the receipt.'));area.append(p);}}
async function load(refresh=false){$('refresh').disabled=true;try{const response=await fetch(refresh?'/api/refresh':'/api/snapshot',{cache:'no-store'});if(!response.ok)throw new Error();snapshot=await response.json();selectedRun=null;
 for(const [id,values]of [['builds',snapshot.runs.map(r=>r.buildId)],['environments',snapshot.runs.map(r=>r.environmentSha256)]])$(id).replaceChildren(...[...new Set(values.filter(Boolean))].sort().map(value=>{const o=element('option');o.value=value;return o;}));render();}catch{$('notice').textContent='Dashboard read failed. Previously displayed data may be stale; refresh to retry.';}finally{$('refresh').disabled=false;}}
for(const id of ['build','environment','search','status'])$(id).addEventListener('input',render);$('refresh').addEventListener('click',()=>load(true));load();
