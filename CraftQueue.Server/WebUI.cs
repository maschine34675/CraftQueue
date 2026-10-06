using System;
using System.Text.Json;

namespace CraftQueue.Server
{
    public static class WebUI
    {
        public static string BuildHtml(string token)
        {
            return html.Replace("__AUTH_TOKEN__", JsonSerializer.Serialize(token), StringComparison.Ordinal);
        }

        private const string html = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>CraftQueue</title>
<style>
  :root {
    color-scheme: dark;
    --bg:#171816; --surface:#232421; --surface2:#2d2f2a; --border:#41433c;
    --text:#d0cdbd; --muted:#918e7e; --accent:#c2ad6d; --green:#72ba80;
    --red:#c06f6f; --blue:#7595c2; --orange:#c5965d;
  }
  * { box-sizing:border-box; }
  body { margin:0 auto; max-width:1120px; padding:22px; background:var(--bg); color:var(--text); font-family:"Segoe UI",system-ui,sans-serif; }
  h1 { display:flex; align-items:baseline; gap:12px; margin:0 0 16px; color:var(--accent); font-size:1.55rem; }
  h1 span { color:var(--muted); font-size:.76rem; font-weight:400; }
  h2 { margin:22px 0 9px; padding-bottom:5px; border-bottom:1px solid var(--border); color:var(--accent); font-size:1.08rem; }
  .toolbar { display:flex; align-items:center; flex-wrap:wrap; gap:9px; margin-bottom:12px; }
  select,input[type=search] { border:1px solid var(--border); border-radius:5px; outline:none; background:var(--surface); color:var(--text); padding:8px 11px; }
  select:focus,input[type=search]:focus { border-color:var(--accent); }
  input[type=search] { width:100%; margin:4px 0 10px; }
  .muted,.status { color:var(--muted); font-size:.76rem; }
  .status { margin-left:auto; }
  .banner { margin:12px 0; padding:12px 14px; border:1px solid var(--orange); border-radius:6px; background:rgba(197,150,93,.12); line-height:1.4; }
  .banner.error { border-color:var(--red); background:rgba(192,111,111,.13); }
  .banner.raid { border-color:var(--blue); background:rgba(117,149,194,.13); }
  .banner[hidden] { display:none; }
  .btn { border:0; border-radius:4px; padding:6px 13px; cursor:pointer; font-size:.77rem; font-weight:650; transition:filter .15s,opacity .15s; }
  .btn:hover:not(:disabled) { filter:brightness(1.12); }
  .btn:disabled { cursor:not-allowed; opacity:.35; }
  .btn-craft { background:var(--accent); color:#191a17; }
  .btn-cancel { background:var(--red); color:white; }
  .btn-retry { background:var(--blue); color:white; }
  .btn-refresh { border:1px solid var(--border); background:var(--surface2); color:var(--text); }
  .entry { display:flex; align-items:center; gap:11px; margin:5px 0; padding:9px 12px; border-radius:6px; background:var(--surface); border-left:3px solid transparent; }
  .entry:hover { background:var(--surface2); }
  .entry.active { border-left-color:var(--green); }
  .entry.queued { border-left-color:var(--blue); }
  .entry.review { border-left-color:var(--orange); }
  .entry.command { border-left-color:var(--muted); }
  .entry-info { min-width:0; flex:1; }
  .entry-name { display:flex; align-items:baseline; gap:6px; min-width:0; font-size:.9rem; font-weight:550; }
  .entry-name > .name { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
  .entry-name > .badge,.entry-yield { flex:0 0 auto; }
  .entry-yield { color:var(--muted); font-size:.8rem; font-weight:600; }
  .entry-meta { margin-top:3px; color:var(--muted); font-size:.74rem; line-height:1.35; }
  .entry-error { margin-top:4px; color:#e1a3a3; font-size:.75rem; }
  .entry-time { min-width:82px; color:var(--blue); font-size:.84rem; text-align:right; }
  .active .entry-name,.active .entry-time { color:var(--green); }
  .item-icon { width:42px; height:42px; flex:0 0 42px; border-radius:4px; background:var(--surface2); object-fit:contain; }
  .item-icon.small { width:22px; height:22px; flex-basis:22px; }
  .category { display:flex; align-items:center; gap:7px; margin:9px 0 4px; padding:6px 9px; border-radius:5px; color:var(--muted); font-size:.8rem; font-weight:700; letter-spacing:.04em; text-transform:uppercase; cursor:pointer; user-select:none; }
  .category:hover { background:var(--surface); color:var(--text); }
  .category .caret { width:13px; color:var(--accent); font-size:.72rem; }
  .category .count { color:var(--muted); font-weight:400; }
  .recipes-toolbar { margin:4px 0 2px; gap:16px; }
  .recipes-toolbar label { display:flex; align-items:center; gap:7px; }
  .recipes-toolbar select { padding:5px 9px; font-size:.8rem; }
  .reqs { display:flex; flex-wrap:wrap; gap:5px; margin-top:5px; }
  .econ { display:flex; flex-wrap:wrap; gap:10px; margin-top:4px; font-size:.75rem; color:#918e7e; }
  .econ .gain { color:#72ba80; }
  .econ .loss { color:#c96a5f; }
  .req { display:flex; align-items:center; gap:4px; padding:2px 6px; border-radius:4px; background:var(--bg); color:var(--muted); font-size:.69rem; }
  .req.tool { border:1px dashed var(--border); }
  .req.missing { color:var(--red); border:1px solid rgba(192,111,111,.55); }
  .entry.unavailable { opacity:.45; }
  .entry.unavailable:hover { opacity:.75; }
  .badge { display:inline-block; margin-left:5px; padding:1px 5px; border-radius:3px; background:var(--surface2); color:var(--muted); font-size:.67rem; text-transform:uppercase; }
  .empty { padding:13px; color:var(--muted); font-style:italic; }
  .recipe-loading { display:flex; align-items:center; gap:17px; margin:12px 0; padding:20px 22px; border:2px solid var(--accent); border-radius:8px; background:linear-gradient(135deg,rgba(194,173,109,.18),var(--surface)); box-shadow:0 0 24px rgba(194,173,109,.12); }
  .recipe-loading[hidden] { display:none; }
  .recipe-loading.error { border-color:var(--red); background:linear-gradient(135deg,rgba(192,111,111,.18),var(--surface)); }
  .spinner { width:35px; height:35px; flex:0 0 35px; border:4px solid rgba(194,173,109,.24); border-top-color:var(--accent); border-radius:50%; animation:spin .9s linear infinite; }
  .recipe-loading.error .spinner { border:0; border-radius:0; animation:none; color:var(--red); font-size:30px; text-align:center; }
  .loading-title { margin-bottom:5px; color:var(--accent); font-size:1rem; font-weight:750; }
  .loading-detail { font-size:.82rem; line-height:1.45; }
  @keyframes spin { to { transform:rotate(360deg); } }
  @media (max-width:650px) {
    body { padding:13px; }
    .entry { align-items:flex-start; flex-wrap:wrap; }
    .entry-info { min-width:calc(100% - 58px); }
    .entry-time { margin-left:53px; text-align:left; }
    .status { width:100%; margin-left:0; }
  }
</style>
</head>
<body>
<h1>CraftQueue <span>Hideout Craft Manager</span></h1>

<div class="toolbar">
  <select id="profileSelect" aria-label="Profile"></select>
  <button id="refreshButton" class="btn btn-refresh" type="button">Refresh</button>
  <label class="muted"><input type="checkbox" id="autoRefresh" checked> Auto-refresh (5s)</label>
  <span id="statusText" class="status"></span>
</div>

<div id="clientBanner" class="banner" hidden></div>
<div id="actionBanner" class="banner" hidden></div>
<div id="activeSection"></div>
<div id="queueSection"></div>
<div id="commandSection"></div>

<h2>Available Recipes</h2>
<div class="toolbar recipes-toolbar">
  <label class="muted">Group by
    <select id="groupMode" aria-label="Group recipes by">
      <option value="category">Category</option>
      <option value="station">Station</option>
    </select>
  </label>
  <label class="muted">Sort by
    <select id="sortMode" aria-label="Sort recipes by">
      <option value="name">Name</option>
      <option value="time">Craft time</option>
      <option value="profit">Profit</option>
      <option value="profitPerHour">Profit per hour</option>
    </select>
  </label>
</div>
<input id="search" type="search" placeholder="Search by product, requirement, category or station…" disabled>
<div id="recipeLoading" class="recipe-loading" role="status" aria-live="polite">
  <div id="loadingSpinner" class="spinner" aria-hidden="true"></div>
  <div>
    <div id="loadingTitle" class="loading-title">Preparing the crafting list for the first time…</div>
    <div id="loadingDetail" class="loading-detail"><strong>This can take noticeably longer with many mods and custom items.</strong><br>The server is already building the list in the background. Later loads will be much faster.</div>
  </div>
</div>
<div id="recipesSection" hidden></div>

<script>
const TOKEN=__AUTH_TOKEN__;
const ICON=id=>`https://assets.tarkov.dev/${encodeURIComponent(id)}-icon.webp`;
const AREAS={0:'Vents',1:'Security',2:'Lavatory',3:'Stash',4:'Generator',5:'Heating',6:'Water Collector',7:'Medstation',8:'Nutrition Unit',9:'Rest Space',10:'Workbench',11:'Intelligence Center',12:'Shooting Range',13:'Library',14:'Scav Case',15:'Illumination',16:'Hall of Fame',17:'Air Filtering Unit',18:'Solar Power',19:'Booze Generator',20:'Bitcoin Farm',21:'Christmas Tree',22:'Defective Wall',23:'Gym',24:'Weapon Rack',25:'Secondary Weapon Rack',26:'Equipment Presets',27:'Cultist Circle'};
const brokenIcons=new Set();
let state={clientOnline:false,raidAutomationActive:false,profileView:false,recipeListAvailable:false,activeProductions:[],availableRecipeIds:[],queueEntries:[],commands:[]};
let recipes={},profiles=[],recipesLoaded=false,refreshPromise=null,refreshAgain=false,stateReceivedAt=Date.now();
let pendingActionCommandId='',actionClearTimer=null;

const $=id=>document.getElementById(id);
const esc=value=>String(value??'').replace(/[&<>"']/g,ch=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[ch]));
const areaName=id=>AREAS[id]||`Area ${id}`;
function fmt(value) {
  const s=Math.max(0,Math.ceil(Number(value)||0));
  if(s<=0)return 'Done';
  const h=Math.floor(s/3600),m=Math.floor((s%3600)/60),sec=s%60;
  return h?`${h}h ${String(m).padStart(2,'0')}m`:m?`${m}m ${String(sec).padStart(2,'0')}s`:`${sec}s`;
}
function profileId(){return $('profileSelect').value||'';}

async function api(path,method='GET',body=null){
  const options={method,headers:{'X-CraftQueue-Token':TOKEN}};
  if(body!==null){options.headers['Content-Type']='application/json';options.body=JSON.stringify(body);}
  const response=await fetch(path,options);
  const text=await response.text();
  if(!response.ok)throw new Error(text||`HTTP ${response.status}`);
  return text?JSON.parse(text):null;
}

function showAction(message,isError=false,clearAfter=0){
  const banner=$('actionBanner');
  if(actionClearTimer!==null){
    clearTimeout(actionClearTimer);
    actionClearTimer=null;
  }
  banner.textContent=message;
  banner.classList.toggle('error',isError);
  banner.hidden=!message;
  if(message&&clearAfter>0){
    actionClearTimer=setTimeout(()=>showAction(''),clearAfter);
  }
}

function reconcilePendingAction(){
  if(!pendingActionCommandId)return;
  const command=(state.commands||[]).find(candidate=>candidate.commandId===pendingActionCommandId);
  if(!command||command.status!=='Completed')return;

  pendingActionCommandId='';
  if(command.success===false){
    showAction(command.error||'The EFT client rejected the command.',true,10000);
  }else if(command.kind==='Enqueue'){
    showAction('Craft added to the queue.',false,5000);
  }else{
    showAction('Production cancelled.',false,5000);
  }
}

async function loadProfiles(){
  profiles=await api('/api/profiles');
  const previous=localStorage.getItem('craftqueue-profile')||'';
  $('profileSelect').innerHTML=profiles.map(profile=>`<option value="${esc(profile.id)}">${esc(profile.name)}</option>`).join('');
  if(profiles.some(profile=>profile.id===previous))$('profileSelect').value=previous;
}

function setRecipeLoading(mode,error=''){
  const box=$('recipeLoading');
  if(mode==='done'){
    box.hidden=true;$('recipesSection').hidden=false;$('search').disabled=false;return;
  }
  box.hidden=false;$('recipesSection').hidden=true;$('search').disabled=true;
  box.classList.toggle('error',mode==='error');
  $('loadingSpinner').textContent=mode==='error'?'!':'';
  $('loadingTitle').textContent=mode==='error'?'Crafting list could not be loaded':'Preparing the crafting list for the first time…';
  $('loadingDetail').innerHTML=mode==='error'
    ? `${esc(error)}<br>The page will retry automatically.`
    : '<strong>This can take noticeably longer with many mods and custom items.</strong><br>The server is already building the list in the background. Later loads will be much faster.';
}

async function runRefresh(){
  $('statusText').textContent='Loading…';
  try{
    if(!profiles.length)await loadProfiles();
    if(!profileId()){
      $('statusText').textContent='No profile found';
      return;
    }
    localStorage.setItem('craftqueue-profile',profileId());
    const stateRequest=api('/api/state?profile='+encodeURIComponent(profileId()));
    if(!recipesLoaded)setRecipeLoading('loading');
    const recipeRequest=recipesLoaded?Promise.resolve(recipes):api('/api/recipes');
    [state,recipes]=await Promise.all([stateRequest,recipeRequest]);
    stateReceivedAt=Date.now();
    if(!recipesLoaded){recipesLoaded=true;setRecipeLoading('done');}
    reconcilePendingAction();
    renderAll();
    $('statusText').textContent=`Updated ${new Date().toLocaleTimeString()}`;
  }catch(error){
    $('statusText').textContent=`Error: ${error.message}`;
    if(!recipesLoaded)setRecipeLoading('error',error.message);
  }
}

async function refresh(){
  if(refreshPromise){refreshAgain=true;return refreshPromise;}
  refreshPromise=runRefresh();
  try{await refreshPromise;}
  finally{
    refreshPromise=null;
    if(refreshAgain){refreshAgain=false;void refresh();}
  }
}
function yieldTag(recipe){
  const count=recipe&&recipe.productCount;
  return count>1?`<span class="entry-yield" title="One craft yields ${count} items">×${count}</span>`:'';
}

function recipeById(id){
  for(const list of Object.values(recipes))for(const recipe of list)if(recipe.recipeId===id)return recipe;
  return null;
}

function iconHtml(templateId,cls='item-icon'){
  if(!templateId||brokenIcons.has(templateId))return `<span class="${cls}"></span>`;
  return `<img class="${cls}" data-icon="${esc(templateId)}" src="${ICON(templateId)}" alt="" loading="lazy">`;
}

function activateIcons(root=document){
  root.querySelectorAll('img[data-icon]').forEach(img=>{
    img.addEventListener('error',()=>{
      brokenIcons.add(img.dataset.icon);
      const replacement=document.createElement('span');
      replacement.className=img.className;
      img.replaceWith(replacement);
    },{once:true});
  });
}
function needOf(recipe,index){
  const needs=state.requirementNeeds?.[recipe.recipeId];
  return Array.isArray(needs)&&typeof needs[index]==='number'?needs[index]:recipe.requirements[index].count;
}

function requirementsHtml(recipe,have){
  const requirements=recipe.requirements;
  if(!requirements?.length)return '';
  return `<div class="reqs">${requirements.map((req,index)=>{
    const need=needOf(recipe,index);
    const known=Array.isArray(have)&&typeof have[index]==='number';
    const short=known&&have[index]<need;
    const amount=known?`${have[index]}/${need}`:`×${need}`;
    return `<span class="req${req.isTool?' tool':''}${short?' missing':''}">${iconHtml(req.templateId,'item-icon small')}${req.isTool?'🔧 ':''}${esc(req.name)} ${amount}</span>`;
  }).join('')}</div>`;
}
function money(value){
  const rounded=Math.round(Math.abs(value));
  const text=rounded>=1000000?(rounded/1000000).toFixed(rounded>=10000000?0:1)+'M'
    :rounded>=1000?Math.round(rounded/1000)+'k'
    :String(rounded);
  return (value<0?'-':'')+text+' ₽';
}

function economicsHtml(recipe){
  const cost=recipe.materialCost||0,value=recipe.productValue||0;
  if(recipe.isScavCase)return `<div class="econ">
    <span title="What one run costs (before an elite Charisma discount)">${money(cost)} per run</span>
    <span title="The scav case rolls its loot when you collect it">random loot</span>
  </div>`;
  if(!cost&&!value)return '';
  if(!value)return `<div class="econ">
    <span title="Estimated material cost, tools excluded">${money(cost)} spent</span>
    <span title="This product has no flea or handbook price, so no profit can be estimated">no market price</span>
  </div>`;
  const profit=recipe.profit||0;
  const perHour=recipe.profitPerHour||0;
  const cls=profit<0?'loss':'gain';
  return `<div class="econ">
    <span title="Estimated material cost, tools excluded">${money(cost)} → ${money(value)}</span>
    <span class="${cls}" title="Estimated profit for one craft">${profit>=0?'+':''}${money(profit)}</span>
    <span class="${cls}" title="Estimated profit per hour of craft time">${perHour>=0?'+':''}${money(perHour)}/h</span>
  </div>`;
}

function isCraftable(recipe,have){
  if(!Array.isArray(have))return true;
  return (recipe.requirements||[]).every((req,index)=>(have[index]??0)>=needOf(recipe,index));
}

function renderClientBanner(){
  const banner=$('clientBanner');
  banner.classList.toggle('raid',state.raidAutomationActive);
  banner.classList.toggle('error',false);
  if(state.raidAutomationActive){
    banner.hidden=false;
    banner.textContent=state.recipeListAvailable
      ? 'EFT is currently in a raid. The server is processing this queue and will synchronize stash changes when the raid ends. New crafts may be queued; aborting an active craft remains disabled until EFT returns.'
      : 'EFT is currently in a raid. The server is processing the existing queue, but new crafts require a profile recipe list loaded before entering the raid. Active crafts cannot be aborted until EFT returns.';
  }else if(state.clientOnline){
    banner.hidden=true;
    banner.textContent='';
  }else{
    banner.hidden=false;
    banner.textContent='EFT is not reporting for this profile, so these crafts are shown from the saved profile and the timers are estimated. Queueing and aborting resume once EFT is back in the main menu or hideout.';
  }
}

function renderActive(){
  const root=$('activeSection');
  const active=state.activeProductions||[];
  if(!active.length){root.innerHTML='';return;}
  let html='<h2>Active Crafts</h2>';
  for(const production of active){
    const recipe=recipeById(production.recipeId);
    const abortDisabled=recipe?.isScavCase
      ? ' disabled title="A running scav case cannot be aborted."'
      : state.raidAutomationActive
      ? ' disabled title="Active crafts cannot be aborted safely during a raid."'
      : (state.clientOnline?'':' disabled title="EFT must be connected to abort a craft."');
    const rate=Number(production.progressRate??1);
    const paused=rate>0?'':' · paused, the generator is off';
    const source=state.profileView?' · estimated from the saved profile':'';
    html+=`<div class="entry active">
      ${iconHtml(recipe?.endProduct)}
      <div class="entry-info">
        <div class="entry-name"><span class="name">${esc(production.productName)}</span>${yieldTag(recipe)}</div>
        <div class="entry-meta">${esc(areaName(production.areaType))}${esc(paused)}${esc(source)}</div>
      </div>
      <div class="entry-time" data-seconds="${Number(production.timeLeft)||0}" data-rate="${rate}">${rate>0?fmt(production.timeLeft):'Paused'}</div>
      <button class="btn btn-cancel" type="button" data-action="cancel-active" data-id="${esc(production.recipeId)}" data-instance="${esc(production.instanceId)}"${abortDisabled}>Abort</button>
    </div>`;
  }
  root.innerHTML=html;
  activateIcons(root);
}

function renderQueue(){
  const root=$('queueSection');
  const entries=state.queueEntries||[];
  if(!entries.length){root.innerHTML='';return;}
  const areaCounts={};
  let html='<h2>Queued Crafts</h2>';
  for(const entry of entries){
    const recipe=recipeById(entry.recipeId);
    areaCounts[entry.areaType]=(areaCounts[entry.areaType]||0)+1;
    const review=entry.status==='NeedsReview';
    const status=entry.status.replace(/([a-z])([A-Z])/g,'$1 $2');
    const retry=entry.retryAt?` · retry ${new Date(entry.retryAt*1000).toLocaleTimeString()}`:'';
    const locked=entry.status==='Claimed'||entry.status==='Starting';
    html+=`<div class="entry ${review?'review':'queued'}">
      ${iconHtml(recipe?.endProduct)}
      <div class="entry-info">
        <div class="entry-name"><span class="name">${areaCounts[entry.areaType]}. ${esc(recipe?.productName||entry.recipeId)}</span>${yieldTag(recipe)}<span class="badge">${esc(status)}</span></div>
        <div class="entry-meta">${esc(areaName(entry.areaType))} · attempts ${entry.attemptCount||0}${esc(retry)}</div>
        ${entry.lastError?`<div class="entry-error">${esc(entry.lastError)}</div>`:''}
      </div>
      ${review?`<button class="btn btn-retry" type="button" data-action="retry-queued" data-id="${esc(entry.queueId)}">Retry</button>`:''}
      <button class="btn btn-cancel" type="button" data-action="cancel-queued" data-id="${esc(entry.queueId)}"${locked?' disabled':''}>Cancel</button>
    </div>`;
  }
  root.innerHTML=html;
  activateIcons(root);
}

function renderCommands(){
  const root=$('commandSection');
  const commands=(state.commands||[]).filter(command=>command.status!=='Completed'||command.success===false);
  if(!commands.length){root.innerHTML='';return;}
  let html='<h2>Recent Client Commands</h2>';
  for(const command of commands){
    const recipe=recipeById(command.recipeId);
    const title=command.kind==='Enqueue'?'Validate and enqueue':'Cancel active craft';
    html+=`<div class="entry command">
      ${iconHtml(recipe?.endProduct)}
      <div class="entry-info">
        <div class="entry-name"><span class="name">${esc(title)}: ${esc(recipe?.productName||command.recipeId)}</span>${yieldTag(recipe)}<span class="badge">${esc(command.status)}</span></div>
        <div class="entry-meta">Delivery attempts ${command.deliveryCount||0}</div>
        ${command.error?`<div class="entry-error">${esc(command.error)}</div>`:''}
      </div>
      ${command.status==='Completed'?`<button class="btn btn-refresh" type="button" data-action="dismiss-command" data-id="${esc(command.commandId)}">Dismiss</button>`:''}
    </div>`;
  }
  root.innerHTML=html;
  activateIcons(root);
}

let groupMode=localStorage.getItem('craftqueue-group')||'category';
let sortMode=localStorage.getItem('craftqueue-sort')||'name';
let expandedGroups=new Set(JSON.parse(localStorage.getItem('craftqueue-expanded')||'[]'));

function toggleGroup(name){
  if($('search').value.trim())return;
  if(expandedGroups.has(name))expandedGroups.delete(name);
  else expandedGroups.add(name);
  localStorage.setItem('craftqueue-expanded',JSON.stringify([...expandedGroups]));
  renderRecipes();
}

function matchesFilter(recipe,category,filter){
  return recipe.productName.toLowerCase().includes(filter)||
    category.toLowerCase().includes(filter)||
    areaName(recipe.areaType).toLowerCase().includes(filter)||
    (recipe.requirements||[]).some(req=>req.name.toLowerCase().includes(filter));
}

function renderRecipes(){
  if(!recipesLoaded)return;
  const root=$('recipesSection');
  if(!state.recipeListAvailable){
    root.innerHTML='<div class="empty">Connect EFT in the main menu or hideout once so CraftQueue learns which recipes this profile has unlocked.</div>';
    return;
  }
  const available=new Set(state.availableRecipeIds||[]);
  const availability=state.materialAvailability||{};
  const filter=$('search').value.trim().toLowerCase();
  const searching=filter.length>0;
  const craftDisabled=(state.clientOnline||state.raidAutomationActive)
    ?''
    :' disabled title="EFT must be connected to queue a craft."';

  const groups=new Map();
  for(const [category,list] of Object.entries(recipes)){
    for(const recipe of list){
      if(!available.has(recipe.recipeId))continue;
      if(searching&&!matchesFilter(recipe,category,filter))continue;
      const name=groupMode==='station'?areaName(recipe.areaType):category;
      if(!groups.has(name))groups.set(name,[]);
      groups.get(name).push(recipe);
    }
  }

  let html='';
  for(const [name,items] of [...groups.entries()].sort((a,b)=>a[0].localeCompare(b[0]))){
    items.sort(
      sortMode==='time'?(a,b)=>(a.productionTime-b.productionTime)||a.productName.localeCompare(b.productName)
      :sortMode==='profit'?(a,b)=>((b.profit||0)-(a.profit||0))||a.productName.localeCompare(b.productName)
      :sortMode==='profitPerHour'?(a,b)=>((b.profitPerHour||0)-(a.profitPerHour||0))||a.productName.localeCompare(b.productName)
      :(a,b)=>a.productName.localeCompare(b.productName));
    const craftableCount=items.filter(recipe=>isCraftable(recipe,availability[recipe.recipeId])).length;
    const countLabel=state.materialAvailability
      ?`(${craftableCount}/${items.length} craftable)`
      :`(${items.length})`;
    const open=searching||expandedGroups.has(name);
    html+=`<div class="category" data-toggle-group="${esc(name)}" role="button" tabindex="0" aria-expanded="${open}">
      <span class="caret" aria-hidden="true">${open?'▾':'▸'}</span>${esc(name)} <span class="count">${countLabel}</span>
    </div>`;
    if(!open)continue;
    for(const recipe of items){
      const have=availability[recipe.recipeId];
      const craftable=isCraftable(recipe,have);
      const disabled=craftDisabled||(craftable?'':' disabled title="Not enough materials in the stash."');
      html+=`<div class="entry${craftable?'':' unavailable'}">
        ${iconHtml(recipe.endProduct)}
        <div class="entry-info">
          <div class="entry-name"><span class="name">${esc(recipe.productName)}</span>${yieldTag(recipe)}</div>
          <div class="entry-meta">${esc(areaName(recipe.areaType))} · ${fmt(recipe.productionTime)}</div>
          ${economicsHtml(recipe)}
          ${requirementsHtml(recipe,have)}
        </div>
        <button class="btn btn-craft" type="button" data-action="enqueue" data-id="${esc(recipe.recipeId)}"${disabled}>Craft</button>
      </div>`;
    }
  }
  root.innerHTML=html||'<div class="empty">No available recipes match your search.</div>';
  activateIcons(root);
}

function renderAll(){
  renderClientBanner();
  renderActive();
  renderQueue();
  renderCommands();
  renderRecipes();
}

async function performAction(button){
  const action=button.dataset.action;
  button.disabled=true;
  try{
    let result;
    if(action==='enqueue'){
      result=await api('/api/commands/enqueue','POST',{profileId:profileId(),recipeId:button.dataset.id});
      pendingActionCommandId=result.success?(result.commandId||''):'';
      showAction(result.success?(result.handledByServer?'Craft validated and added by the raid executor.':'Waiting for EFT to validate the craft…'):result.error,!result.success);
    }else if(action==='cancel-active'){
      result=await api('/api/commands/cancel-active','POST',{profileId:profileId(),recipeId:button.dataset.id,instanceId:button.dataset.instance});
      pendingActionCommandId=result.success?(result.commandId||''):'';
      showAction(result.success?'Waiting for EFT to cancel the production…':result.error,!result.success);
    }else if(action==='cancel-queued'){
      pendingActionCommandId='';
      result=await api('/api/queue/cancel','POST',{profileId:profileId(),queueId:button.dataset.id});
      showAction(result.success?'Queue entry removed.':result.error,!result.success,result.success?5000:0);
    }else if(action==='retry-queued'){
      pendingActionCommandId='';
      result=await api('/api/queue/retry','POST',{profileId:profileId(),queueId:button.dataset.id});
      showAction(result.success?'Queue entry is ready to retry.':result.error,!result.success,result.success?5000:0);
    }else if(action==='dismiss-command'){
      if(pendingActionCommandId===button.dataset.id)pendingActionCommandId='';
      result=await api('/api/commands/dismiss','POST',{profileId:profileId(),commandId:button.dataset.id});
      showAction(result.success?'':result.error,!result.success);
    }
    await new Promise(resolve=>setTimeout(resolve,250));
    await refresh();
  }catch(error){
    showAction(error.message,true);
  }finally{
    button.disabled=false;
  }
}

document.addEventListener('click',event=>{
  const group=event.target.closest('[data-toggle-group]');
  if(group){toggleGroup(group.dataset.toggleGroup);return;}
  const button=event.target.closest('button[data-action]');
  if(button)void performAction(button);
});
document.addEventListener('keydown',event=>{
  if(event.key!=='Enter'&&event.key!==' ')return;
  const group=event.target.closest?event.target.closest('[data-toggle-group]'):null;
  if(group){event.preventDefault();toggleGroup(group.dataset.toggleGroup);}
});
$('refreshButton').addEventListener('click',()=>void refresh());
$('profileSelect').addEventListener('change',()=>{pendingActionCommandId='';showAction('');void refresh();});
$('search').addEventListener('input',renderRecipes);
$('groupMode').value=groupMode;
$('groupMode').addEventListener('change',()=>{
  groupMode=$('groupMode').value;
  localStorage.setItem('craftqueue-group',groupMode);
  renderRecipes();
});
$('sortMode').value=sortMode;
$('sortMode').addEventListener('change',()=>{
  sortMode=$('sortMode').value;
  localStorage.setItem('craftqueue-sort',sortMode);
  renderRecipes();
});

setInterval(()=>{
  const elapsed=Math.floor((Date.now()-stateReceivedAt)/1000);
  document.querySelectorAll('[data-seconds]').forEach(element=>{
    if(Number(element.dataset.rate)<=0)return;
    element.textContent=fmt(Number(element.dataset.seconds)-elapsed);
  });
},1000);
setInterval(()=>{if($('autoRefresh').checked)void refresh();},5000);
void refresh();
</script>
</body>
</html>
""";
    }
}
