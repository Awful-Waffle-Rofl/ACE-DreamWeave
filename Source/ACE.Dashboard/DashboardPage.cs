namespace ACE.Dashboard;

/// <summary>Self-contained single-page UI served at "/". Fetches the /api/* JSON endpoints.</summary>
public static class DashboardPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>ACE Analytics</title>
<style>
  :root{
    --bg:#f6f7f9; --panel:#fff; --ink:#1a1d21; --muted:#6b7280; --line:#e5e7eb;
    --accent:#3b6ea5; --bar:#9db8d6; --flag:#b4342b; --flagbg:#fbeceb;
  }
  @media (prefers-color-scheme:dark){
    :root{ --bg:#14171a; --panel:#1c2024; --ink:#e8eaed; --muted:#9aa2ad; --line:#2b3138;
      --accent:#7aa8d6; --bar:#37506b; --flag:#e8756b; --flagbg:#3a221f; }
  }
  *{box-sizing:border-box}
  body{margin:0;background:var(--bg);color:var(--ink);font:14px/1.5 system-ui,-apple-system,Segoe UI,Roboto,sans-serif}
  header{display:flex;align-items:center;gap:16px;padding:14px 20px;border-bottom:1px solid var(--line);position:sticky;top:0;background:var(--bg)}
  header h1{font-size:16px;margin:0;font-weight:650}
  header .sp{flex:1}
  select,button{font:inherit;color:var(--ink);background:var(--panel);border:1px solid var(--line);border-radius:8px;padding:6px 10px}
  main{padding:20px;max-width:1200px;margin:0 auto;display:grid;gap:20px}
  .tiles{display:flex;gap:14px;flex-wrap:wrap}
  .tile{background:var(--panel);border:1px solid var(--line);border-radius:12px;padding:14px 18px;min-width:150px}
  .tile .n{font-size:26px;font-weight:680;font-variant-numeric:tabular-nums}
  .tile .l{color:var(--muted);font-size:12px;text-transform:uppercase;letter-spacing:.04em}
  section{background:var(--panel);border:1px solid var(--line);border-radius:12px;overflow:hidden}
  section h2{font-size:13px;margin:0;padding:12px 16px;border-bottom:1px solid var(--line);text-transform:uppercase;letter-spacing:.04em;color:var(--muted)}
  .grid2{display:grid;grid-template-columns:1fr 1fr;gap:20px}
  @media (max-width:820px){.grid2{grid-template-columns:1fr}}
  table{width:100%;border-collapse:collapse;font-variant-numeric:tabular-nums}
  th,td{text-align:left;padding:7px 16px;border-bottom:1px solid var(--line);white-space:nowrap}
  th{color:var(--muted);font-weight:600;font-size:12px}
  td.n,th.n{text-align:right}
  tbody tr:last-child td{border-bottom:0}
  .bar{position:relative}
  .bar .fill{position:absolute;inset:0;background:var(--bar);opacity:.35;border-radius:3px}
  .bar span{position:relative}
  tr.flag td{background:var(--flagbg)}
  .pill{display:inline-block;font-size:11px;font-weight:600;color:var(--flag);border:1px solid var(--flag);border-radius:999px;padding:1px 7px}
  .muted{color:var(--muted)}
  .scroll{overflow-x:auto}
  .empty{padding:16px;color:var(--muted)}
</style>
</head>
<body>
<header>
  <h1>ACE Analytics</h1>
  <span class="pill" id="live">live</span>
  <span class="sp"></span>
  <label class="muted">window
    <select id="win">
      <option value="1">1h</option>
      <option value="6">6h</option>
      <option value="24" selected>24h</option>
      <option value="168">7d</option>
    </select>
  </label>
  <label class="muted" title="Drops characters with less than this much accumulated earning time. Without a floor, one large grant over a single 60s interval reads as an astronomical hourly rate and takes the top slot.">min earning
    <select id="minearn">
      <option value="0">off</option>
      <option value="5">5m</option>
      <option value="10" selected>10m</option>
      <option value="30">30m</option>
      <option value="60">60m</option>
    </select>
  </label>
  <button id="refresh">Refresh</button>
</header>
<main>
  <div class="tiles">
    <div class="tile"><div class="n" id="t-online">–</div><div class="l">Players online</div></div>
    <div class="tile"><div class="n" id="t-blocks">–</div><div class="l">Active landblocks</div></div>
    <div class="tile"><div class="n" id="t-flags">–</div><div class="l">One-way flow flags</div></div>
  </div>

  <div class="grid2">
    <section><h2>Top XP / hr</h2><div class="scroll"><table id="xp"></table></div></section>
    <section><h2>Top Luminance / hr</h2><div class="scroll"><table id="lum"></table></div></section>
  </div>

  <section><h2>Item flows &mdash; directed edges by value (one-way = mule signature)</h2><div class="scroll"><table id="flows"></table></div></section>

  <div class="grid2">
    <section><h2>Recent bank transfers</h2><div class="scroll"><table id="bank"></table></div></section>
    <section><h2>Populated landblocks</h2><div class="scroll"><table id="blocks"></table></div></section>
  </div>

  <section><h2>Online roster</h2><div class="scroll"><table id="roster"></table></div></section>
</main>

<script>
const $ = id => document.getElementById(id);
const win = () => $("win").value;
const fmt = n => Number(n).toLocaleString();
const block = b => "0x" + Number(b).toString(16).toUpperCase().padStart(4,"0");

async function j(u){ const r = await fetch(u); if(!r.ok) throw new Error(u+" "+r.status); return r.json(); }

function rows(el, cols, data, rowClass){
  if(!data || !data.length){ el.innerHTML = '<tbody><tr><td class="empty">No data</td></tr></tbody>'; return; }
  const head = "<thead><tr>" + cols.map(c=>`<th class="${c.n?'n':''}">${c.h}</th>`).join("") + "</tr></thead>";
  const max = cols.map(c => c.bar ? Math.max(...data.map(d=>+c.bar(d)||0)) : 0);
  const body = "<tbody>" + data.map(d=>{
    const cls = rowClass && rowClass(d) ? ' class="flag"' : '';
    const tds = cols.map((c,i)=>{
      let v = c.f(d);
      if(c.bar){ const w = max[i]>0 ? (100*(+c.bar(d)||0)/max[i]) : 0;
        return `<td class="n bar"><span class="fill" style="width:${w}%"></span><span>${v}</span></td>`; }
      return `<td class="${c.n?'n':''}">${v}</td>`;
    }).join("");
    return `<tr${cls}>${tds}</tr>`;
  }).join("") + "</tbody>";
  el.innerHTML = head + body;
}

async function loadLive(){
  const d = await j("/api/live");
  $("t-online").textContent = d.roster.length;
  $("t-blocks").textContent = d.blocks.length;
  rows($("roster"), [
    {h:"Character",f:x=>x.name},
    {h:"Level",n:1,f:x=>x.level},
    {h:"Landblock",n:1,f:x=>block(x.landblock)},
  ], d.roster);
  rows($("blocks"), [
    {h:"Landblock",f:x=>block(x.landblock)},
    {h:"Players",n:1,f:x=>x.players,bar:x=>x.players},
  ], d.blocks);
}

async function loadBoards(){
  // Both boards are ordered by RATE, not by total. "Earning hrs" is shown alongside because the
  // rate denominator is earning time, not time online - a high rate over minutes and a high rate
  // over hours are different findings, and without the column they look identical.
  const q = "hours="+win()+"&limit=10&minEarnMinutes="+$("minearn").value;
  const xp = await j("/api/leaderboard/xp?"+q);
  rows($("xp"), [
    {h:"Character",f:x=>x.name},
    {h:"XP/hr",n:1,f:x=>fmt(Math.round(x.perHour)),bar:x=>x.perHour},
    {h:"Total",n:1,f:x=>fmt(x.total)},
    {h:"Earning hrs",n:1,f:x=>(x.secs/3600).toFixed(2)},
  ], xp);
  const lum = await j("/api/leaderboard/lum?"+q);
  rows($("lum"), [
    {h:"Character",f:x=>x.name},
    {h:"Lum/hr",n:1,f:x=>fmt(Math.round(x.perHour)),bar:x=>x.perHour},
    {h:"Total",n:1,f:x=>fmt(x.total)},
    {h:"Earning hrs",n:1,f:x=>(x.secs/3600).toFixed(2)},
  ], lum);
}

async function loadFlows(){
  const h = win();
  const f = await j("/api/flows/items?hours="+h+"&limit=25");
  $("t-flags").textContent = f.filter(x=>x.oneDirectional).length;
  rows($("flows"), [
    {h:"From",f:x=>x.fromName},
    {h:"To",f:x=>x.toName},
    {h:"Value moved",n:1,f:x=>fmt(x.totalValue),bar:x=>x.totalValue},
    {h:"Items",n:1,f:x=>fmt(x.totalItems)},
    {h:"Transfers",n:1,f:x=>x.transfers},
    {h:"",f:x=>x.oneDirectional?'<span class="pill">one-way</span>':''},
  ], f, x=>x.oneDirectional);
  const b = await j("/api/flows/bank?limit=30");
  rows($("bank"), [
    {h:"When (UTC)",f:x=>x.ts.replace("T"," ").slice(0,19)},
    {h:"From",f:x=>x.fromName},
    {h:"To",f:x=>x.toName},
    {h:"Currency",f:x=>x.currency},
    {h:"Amount",n:1,f:x=>fmt(x.amount),bar:x=>x.amount},
  ], b);
}

async function loadAll(){
  try{ await Promise.all([loadLive(), loadBoards(), loadFlows()]); }
  catch(e){ console.error(e); }
}
$("refresh").onclick = loadAll;
$("win").onchange = ()=>{ loadBoards(); loadFlows(); };
$("minearn").onchange = loadBoards;   // only the rate boards take the floor
loadAll();
setInterval(loadLive, 20000);
</script>
</body>
</html>
""";
}
