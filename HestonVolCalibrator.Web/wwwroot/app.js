// Heston Vol Calibration — Single-Page Frontend
// Vanilla JS, no build tooling. Talks to the .NET backend on the same origin.

const API = ""; // same origin (server serves wwwroot)

// ───────────────── State ─────────────────
const state = {
  surface: null,           // last surface response
  calibration: null,       // last calibration result
  convergence: { iters: [], rmses: [], stages: [] },
  abortController: null,
  smiles: { denseStrikes: null, denseIv: null }, // cached dense Heston smile grid: iv[i_expiry][k_dense]
  smileDetail: {
    cache: new Map(),   // key: `${expiryIdx}|${strikeCount}|${kmin}|${kmax}|${calibStamp}` -> { strikes, iv }
    rendered: false,    // whether the user has clicked Plot at least once with current surface
    lastKey: null,      // last cache key used (for auto-rerender after calibration)
  },
  // Last response from /api/heston-surface-with-greeks. Holds Greek 2D grids keyed by name
  // so the tab can swap visualisation between delta/gamma/vega/theta/rho without refetching.
  greeks: null,
};

// Color palette shared across tabs.
const COLORS = {
  market: "#4f9dff",
  heston: "#f0883e",
  iv: "#4f9dff",
  call: "#f0a36a",
  put: "#e25c5c",
};

// Market tab quantity descriptors.
const MARKET_QTY = {
  iv:   { titleSuffix: "implied vol",     zLabel: "IV",    colorscale: "Viridis", colorbar: "IV"    },
  call: { titleSuffix: "call mid prices", zLabel: "Price", colorscale: "Cividis", colorbar: "Price" },
  put:  { titleSuffix: "put mid prices",  zLabel: "Price", colorscale: "Magma",   colorbar: "Price" },
};

// ───────────────── DOM helpers ─────────────────
const $ = (id) => document.getElementById(id);
const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));

function showError(msg) {
  const b = $("errorBanner");
  b.textContent = msg;
  b.classList.remove("hidden");
}
function clearError() {
  const b = $("errorBanner");
  b.textContent = "";
  b.classList.add("hidden");
}
function setStatus(pillId, text, cls) {
  const el = $(pillId);
  el.textContent = text || "";
  el.classList.remove("busy", "ok", "err", "warn");
  if (cls) el.classList.add(cls);
}

// ───────────────── Activity log ─────────────────
// Visible bottom-docked panel. Captures SSE events, API calls, and errors so
// the user can see what is happening without opening DevTools.
const logState = { count: 0, errorCount: 0, warnCount: 0, lastProgressIter: -1, progressBatched: 0 };
const MAX_LOG_ENTRIES = 500;

function log(level, tag, msg) {
  const body = $("logBody");
  if (!body) return; // panel not yet in DOM
  const row = document.createElement("div");
  row.className = `log-entry ${level}`;
  const ts = new Date();
  const hh = String(ts.getHours()).padStart(2, "0");
  const mm = String(ts.getMinutes()).padStart(2, "0");
  const ss = String(ts.getSeconds()).padStart(2, "0");
  const ms = String(ts.getMilliseconds()).padStart(3, "0");
  row.innerHTML =
    `<span class="log-ts">${hh}:${mm}:${ss}.${ms}</span>` +
    `<span class="log-tag">${tag}</span>` +
    `<span class="log-msg"></span>`;
  row.querySelector(".log-msg").textContent = msg;
  body.appendChild(row);
  while (body.childElementCount > MAX_LOG_ENTRIES) body.removeChild(body.firstChild);
  body.scrollTop = body.scrollHeight;

  logState.count++;
  if (level === "err") logState.errorCount++;
  else if (level === "warn") logState.warnCount++;
  updateLogBadge();
}

function updateLogBadge() {
  const badge = $("logBadge");
  if (!badge) return;
  badge.textContent = String(logState.count);
  badge.classList.remove("has-error", "has-warn");
  if (logState.errorCount > 0) badge.classList.add("has-error");
  else if (logState.warnCount > 0) badge.classList.add("has-warn");
}

function clearLog() {
  const body = $("logBody");
  if (body) body.innerHTML = "";
  logState.count = 0;
  logState.errorCount = 0;
  logState.warnCount = 0;
  logState.lastProgressIter = -1;
  logState.progressBatched = 0;
  updateLogBadge();
}

function setupLogPanel() {
  const panel = $("logPanel");
  const header = panel?.querySelector(".log-header");
  const toggleBtn = $("logToggleBtn");
  const clearBtn = $("logClearBtn");
  if (!panel) return;
  const toggle = () => {
    panel.classList.toggle("collapsed");
    if (toggleBtn) toggleBtn.textContent = panel.classList.contains("collapsed") ? "Expand" : "Collapse";
  };
  header?.addEventListener("click", (e) => {
    if (e.target.closest("button")) return;
    toggle();
  });
  toggleBtn?.addEventListener("click", (e) => { e.stopPropagation(); toggle(); });
  clearBtn?.addEventListener("click", (e) => { e.stopPropagation(); clearLog(); });
  // Start expanded the first time so the user sees it.
  panel.classList.remove("collapsed");
  if (toggleBtn) toggleBtn.textContent = "Collapse";
  // Capture uncaught errors.
  window.addEventListener("error", (e) => log("err", "js", `${e.message} @ ${e.filename}:${e.lineno}`));
  window.addEventListener("unhandledrejection", (e) =>
    log("err", "js", `unhandled: ${e.reason?.message || e.reason}`));
  log("info", "init", "Activity log ready.");
}

function fmt(n, digits = 6) {
  if (n === null || n === undefined || Number.isNaN(n)) return "—";
  if (!Number.isFinite(n)) return String(n);
  if (Math.abs(n) >= 1000) return n.toFixed(2);
  return n.toFixed(digits);
}

// ───────────────── API ─────────────────
async function apiJson(path, body) {
  log("info", "api", `POST ${path}`);
  const t0 = performance.now();
  const res = await fetch(API + path, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  if (!res.ok) {
    const txt = await res.text().catch(() => "");
    // Extract clean message from ProblemDetails JSON if present; else use raw text.
    let msg = txt;
    try { const p = JSON.parse(txt); if (p.detail || p.title) msg = p.detail || p.title; } catch { /**/ }
    log("err", "api", `${path} → ${res.status}: ${msg.slice(0, 200)}`);
    throw new Error(msg || `${res.status} ${res.statusText}`);
  }
  const json = await res.json();
  log("ok", "api", `${path} → 200 in ${(performance.now() - t0).toFixed(0)}ms`);
  return json;
}

// Generic GET / DELETE helpers for the snapshot endpoints.
async function apiGet(path) {
  log("info", "api", `GET ${path}`);
  const res = await fetch(API + path);
  if (!res.ok) {
    const txt = await res.text().catch(() => "");
    log("err", "api", `${path} → ${res.status} ${res.statusText}: ${txt.slice(0, 200)}`);
    throw new Error(`${res.status} ${res.statusText}: ${txt}`);
  }
  return res.json();
}
async function apiDelete(path) {
  log("info", "api", `DELETE ${path}`);
  const res = await fetch(API + path, { method: "DELETE" });
  if (!res.ok) {
    const txt = await res.text().catch(() => "");
    log("err", "api", `${path} → ${res.status} ${res.statusText}: ${txt.slice(0, 200)}`);
    throw new Error(`${res.status} ${res.statusText}: ${txt}`);
  }
  return res.json().catch(() => ({}));
}

// SSE-over-POST stream parser. Calls onProgress(point) and returns the final result.
async function apiStreamCalibrate(body, signal, onProgress) {
  log("info", "sse", "POST /api/calibrate/stream — opening stream");
  const t0 = performance.now();
  const res = await fetch(API + "/api/calibrate/stream", {
    method: "POST",
    headers: { "Content-Type": "application/json", "Accept": "text/event-stream" },
    body: JSON.stringify(body),
    signal,
  });
  if (!res.ok || !res.body) {
    const txt = await res.text().catch(() => "");
    log("err", "sse", `${res.status} ${res.statusText}: ${txt.slice(0, 200)}`);
    throw new Error(`${res.status} ${res.statusText}: ${txt}`);
  }
  log("ok", "sse", `connected (status=${res.status})`);
  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  let buf = "";
  let finalResult = null;
  let progressCount = 0;

  while (true) {
    const { value, done } = await reader.read();
    if (done) break;
    buf += decoder.decode(value, { stream: true });

    // Split into SSE events (blank line separated).
    let idx;
    while ((idx = buf.indexOf("\n\n")) !== -1) {
      const raw = buf.slice(0, idx);
      buf = buf.slice(idx + 2);

      let event = "message";
      const dataLines = [];
      for (const line of raw.split("\n")) {
        if (line.startsWith("event:")) event = line.slice(6).trim();
        else if (line.startsWith("data:")) dataLines.push(line.slice(5).trim());
      }
      if (dataLines.length === 0) continue;
      let payload;
      try {
        payload = JSON.parse(dataLines.join("\n"));
      } catch (e) {
        log("err", "sse", `JSON parse failed: ${e.message}`);
        continue;
      }
      if (event === "started") {
        log("info", "started",
          `source=${payload.source} spot=${payload.spot} grid=${payload.expiries}×${payload.strikes} ` +
          `pipeline=${payload.globalMethod}/${payload.gradientMethod}`);
      } else if (event === "progress") {
        progressCount++;
        // Log every frame at low rate, then batch above 50.
        if (progressCount <= 20 || progressCount % 25 === 0) {
          log("event", `iter ${payload.iter}`,
            `${payload.stage} rmse=${payload.rmse?.toExponential ? payload.rmse.toExponential(4) : payload.rmse}`);
        }
        onProgress(payload);
      } else if (event === "done") {
        finalResult = payload;
        log("ok", "sse", `done event received (${progressCount} progress frames, ${(performance.now() - t0).toFixed(0)}ms)`);
      } else if (event === "error") {
        const msg = payload?.message || "Calibration failed.";
        const type = payload?.type ? ` (${payload.type})` : "";
        log("err", "sse", `server error: ${msg}${type}`);
        throw new Error(msg + type);
      } else {
        log("warn", "sse", `unknown event "${event}"`);
      }
    }
  }
  log("info", "sse", `stream closed (final=${finalResult ? "yes" : "no"})`);
  return finalResult;
}

// ───────────────── Surface loading ─────────────────
async function loadSurface() {
  clearError();
  const ticker = $("ticker").value.trim() || "^SPX";
  // 0 = unlimited (within the maturity window); preserve it instead of falling back to a default.
  const maxExpiriesRaw = parseInt($("maxExpiries").value, 10);
  const maxExpiries = Number.isFinite(maxExpiriesRaw) && maxExpiriesRaw >= 0 ? maxExpiriesRaw : 6;
  const forceSynthetic = $("forceSynthetic").checked;
  const clean = $("cleanQuotes")?.checked ?? true;

  const readOptionalFloat = (id) => {
    const v = $(id)?.value;
    if (v === undefined || v === "") return null;
    const n = parseFloat(v);
    return Number.isFinite(n) ? n : null;
  };
  const minMaturity = readOptionalFloat("minMaturity");
  const maxMaturity = readOptionalFloat("maxMaturity");
  const minMoneyness = readOptionalFloat("minMoneyness");
  const maxMoneyness = readOptionalFloat("maxMoneyness");

  if (minMaturity !== null && maxMaturity !== null && maxMaturity <= minMaturity) {
    showError("Max T must be greater than Min T.");
    return;
  }
  if (minMoneyness !== null && maxMoneyness !== null && maxMoneyness <= minMoneyness) {
    showError("Max K/S must be greater than Min K/S.");
    return;
  }

  $("loadSurfaceBtn").disabled = true;
  setStatus("surfaceStatus", "Loading…", "busy");
  try {
    const data = await apiJson("/api/surface", {
      ticker, maxExpiries, forceSynthetic, clean,
      minMaturity, maxMaturity, minMoneyness, maxMoneyness,
    });
    state.surface = data;
    let msg = `Loaded ${data.source} surface (spot=${fmt(data.spot, 2)}, ` +
      `${data.expiries.length} expiries × ${data.strikes.length} strikes)`;
    let tooltip = "";
    if (data.cleanStats) {
      const cs = data.cleanStats;
      msg += ` · cleaned ${cs.kept}/${cs.input} quotes`;
      tooltip = `dropped: bidAsk=${cs.droppedBidAsk}, bounds=${cs.droppedBounds}, ` +
        `convex=${cs.droppedConvexity}, ivOutlier=${cs.droppedIvOutlier}`;
    }
    setStatus("surfaceStatus", msg, "ok");
    const pill = $("surfaceStatus");
    if (pill) { if (tooltip) pill.title = tooltip; else pill.removeAttribute("title"); }
    $("calibPanel").classList.remove("hidden");
    $("tabsSection").classList.remove("hidden");
    // Reset any prior calibration-derived smile cache so the Smiles tab shows market-only.
    state.calibration = null;
    state.smiles = { denseStrikes: null, denseIv: null };
    state.smileDetail = { cache: new Map(), rendered: false, lastKey: null };
    refreshMarketSourcePill();
    refreshMarketCutControls();
    renderMarketSurface(getSelectedMarketQty());
    renderMarketCut();
    renderSmiles();
    refreshSmileDetailControls();
    clearSmileDetailPlot();
    switchTab("market");
  } catch (e) {
    setStatus("surfaceStatus", "Failed", "err");
    showError("Surface load failed: " + e.message);
  } finally {
    $("loadSurfaceBtn").disabled = false;
  }
}

function saveMarketCsv() {
  const surf = state.surface;
  if (!surf) { showError("Load a surface first."); return; }

  const ticker = surf.ticker || "";
  const spot = surf.spot ?? "";
  const source = surf.source || "";
  const exps = surf.expiries || [];
  const strikes = surf.strikes || [];
  const iv = surf.iv || [];
  const cp = surf.callPrice || [];
  const pp = surf.putPrice || [];

  const lines = ["ticker,spot,source,expiry_years,strike,iv,call_price,put_price"];
  const cell = (g, i, j) => {
    const row = g[i];
    if (!row) return "";
    const v = row[j];
    return v === null || v === undefined || Number.isNaN(v) ? "" : String(v);
  };

  for (let i = 0; i < exps.length; i++) {
    for (let j = 0; j < strikes.length; j++) {
      lines.push([ticker, spot, source, exps[i], strikes[j],
                  cell(iv, i, j), cell(cp, i, j), cell(pp, i, j)].join(","));
    }
  }

  const blob = new Blob([lines.join("\n") + "\n"], { type: "text/csv;charset=utf-8" });
  const url = URL.createObjectURL(blob);
  const stamp = new Date().toISOString().replace(/[:.]/g, "-").slice(0, 19);
  const safeTicker = (ticker || "ticker").replace(/[^\w.^-]+/g, "_");
  const a = document.createElement("a");
  a.href = url;
  a.download = `market_${safeTicker}_${stamp}.csv`;
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
}

// ───────────────── Calibration ─────────────────
function readBounds() {
  const out = {};
  for (const row of $$(".bounds-row")) {
    const key = row.dataset.key;
    out[key] = {
      lower: parseFloat(row.querySelector(".lower").value),
      upper: parseFloat(row.querySelector(".upper").value),
    };
  }
  return out;
}

function buildCalibRequest() {
  const ticker = $("ticker").value.trim() || "^SPX";
  const b = readBounds();
  return {
    ticker,
    globalMethod: $("globalMethod").value,
    gradientMethod: $("gradientMethod").value,
    chain: $("chain").checked,
    nelderMeadRestarts: parseInt($("nmRestarts").value, 10) || 0,
    globalMaxIterations: parseInt($("globalMaxIter").value, 10) || 1,
    gradientMaxIterations: parseInt($("gradMaxIter").value, 10) || 1,
    kappa: b.kappa,
    theta: b.theta,
    sigma: b.sigma,
    rho: b.rho,
    v0: b.v0,
  };
}

function resetConvergencePlot() {
  state.convergence = { iters: [], rmses: [], stages: [] };
  const layout = {
    margin: { t: 24, l: 60, r: 20, b: 50 },
    paper_bgcolor: "#1e2630",
    plot_bgcolor: "#1e2630",
    font: { color: "#e7ecf2" },
    xaxis: { title: "Iteration", gridcolor: "#2a3340" },
    yaxis: { title: "RMSE", type: "log", gridcolor: "#2a3340" },
    showlegend: true,
    legend: { x: 1, xanchor: "right", y: 1 },
  };
  const traces = [
    { x: [], y: [], mode: "lines+markers", name: "global",   line: { color: "#4f9dff" }, marker: { size: 4 } },
    { x: [], y: [], mode: "lines+markers", name: "gradient", line: { color: "#4cc28e" }, marker: { size: 4 } },
  ];
  Plotly.react("plot-convergence", traces, layout, { responsive: true, displaylogo: false });
}

function appendConvergencePoint(pt) {
  const idx = pt.stage === "gradient" ? 1 : 0;
  const y = (pt.rmse > 0 && Number.isFinite(pt.rmse)) ? pt.rmse : null;
  if (y === null) return;
  Plotly.extendTraces("plot-convergence", { x: [[pt.iter]], y: [[y]] }, [idx]);
  state.convergence.iters.push(pt.iter);
  state.convergence.rmses.push(y);
  state.convergence.stages.push(pt.stage);
}

function plotConvergenceFromHistory(history) {
  resetConvergencePlot();
  const groups = { global: { x: [], y: [] }, gradient: { x: [], y: [] } };
  for (const pt of history) {
    if (!(pt.rmse > 0) || !Number.isFinite(pt.rmse)) continue;
    const g = pt.stage === "gradient" ? "gradient" : "global";
    groups[g].x.push(pt.iter);
    groups[g].y.push(pt.rmse);
  }
  Plotly.extendTraces("plot-convergence",
    { x: [groups.global.x, groups.gradient.x], y: [groups.global.y, groups.gradient.y] },
    [0, 1]);
}

async function runCalibration() {
  clearError();
  if (!state.surface) { showError("Load a surface first."); return; }
  const mode = document.querySelector('input[name="runMode"]:checked').value;
  const req = buildCalibRequest();

  $("runBtn").disabled = true;
  $("loadSurfaceBtn").disabled = true;
  setStatus("runStatus", mode === "stream" ? "Streaming…" : "Running…", "busy");
  resetConvergencePlot();
  switchTab("convergence");

  try {
    let result;
    if (mode === "stream") {
      state.abortController = new AbortController();
      $("cancelBtn").classList.remove("hidden");
      result = await apiStreamCalibrate(req, state.abortController.signal, appendConvergencePoint);
      $("cancelBtn").classList.add("hidden");
      state.abortController = null;
    } else {
      result = await apiJson("/api/calibrate", req);
      plotConvergenceFromHistory(result.history || []);
    }
    if (!result) throw new Error("No final result returned.");
    state.calibration = result;
    applyCalibRangeToGreekInputs();
    const cappedStages = (result.stages || []).filter(s => s && s.converged === false);
    const convergedFlag = result.converged === false || cappedStages.length > 0;
    const baseMsg = `RMSE=${fmt(result.finalRmse, 6)} · iter=${result.totalIterations} · ${result.elapsedMs?.toFixed(0)}ms`;
    if (convergedFlag) {
      const names = cappedStages.map(s => s.stage || s.method || "?").join(", ") || "unknown stage";
      setStatus("runStatus", `Done (cap hit: ${names}). ${baseMsg}`, "warn");
    } else {
      setStatus("runStatus", `Done. ${baseMsg}`, "ok");
    }

    renderHestonSurface(result);
    renderComparison(result);
    renderResults(result);
    // Invalidate per-expiry detail cache (Heston traces depend on new params).
    state.smileDetail.cache = new Map();
    // Fetch dense Heston smile grid (one batched call) then re-render Smiles tab.
    fetchDenseHestonSmiles(result)
      .then(() => {
        renderSmiles();
        if (state.smileDetail.rendered) renderSmileDetail();
      })
      .catch((err) => {
        showError("Smile grid fetch failed: " + err.message);
        renderSmiles(); // fall back to market-grid Heston points
        if (state.smileDetail.rendered) renderSmileDetail();
      });
    switchTab("results");
  } catch (e) {
    if (e.name === "AbortError") {
      setStatus("runStatus", "Cancelled", "err");
    } else {
      setStatus("runStatus", "Failed", "err");
      showError("Calibration failed: " + e.message);
    }
  } finally {
    $("runBtn").disabled = false;
    $("loadSurfaceBtn").disabled = false;
    $("cancelBtn").classList.add("hidden");
  }
}

function cancelCalibration() {
  if (state.abortController) {
    state.abortController.abort();
  }
}

// ───────────────── Plot helpers ─────────────────
const baseLayout3D = (title) => ({
  title: { text: title, font: { size: 14 } },
  margin: { t: 40, l: 0, r: 0, b: 0 },
  paper_bgcolor: "#1e2630",
  plot_bgcolor: "#1e2630",
  font: { color: "#e7ecf2" },
  scene: {
    xaxis: { title: "Strike", gridcolor: "#2a3340", zerolinecolor: "#2a3340" },
    yaxis: { title: "Maturity (yrs)", gridcolor: "#2a3340", zerolinecolor: "#2a3340" },
    zaxis: { title: "IV", gridcolor: "#2a3340", zerolinecolor: "#2a3340" },
    bgcolor: "#1e2630",
  },
});

// Plotly surface expects z[y][x], i.e. rows = y (maturities), cols = x (strikes).
function getSelectedMarketQty() {
  const el = document.querySelector('input[name="marketQty"]:checked');
  return el ? el.value : "iv";
}

function marketZFor(surf, quantity) {
  if (quantity === "call") return surf.callPrice || null;
  if (quantity === "put")  return surf.putPrice || null;
  return surf.iv || null;
}

function renderMarketSurface(quantity) {
  const surf = state.surface;
  if (!surf) return;
  const cfg = MARKET_QTY[quantity] || MARKET_QTY.iv;
  const z = marketZFor(surf, quantity);
  if (!z) return;
  const title = `${surf.ticker} ${cfg.titleSuffix}`;
  const layout = baseLayout3D(title);
  layout.scene.zaxis = { title: cfg.zLabel, gridcolor: "#2a3340", zerolinecolor: "#2a3340" };
  const data = [{
    type: "surface",
    x: surf.strikes,
    y: surf.expiries,
    z,
    connectgaps: false,
    colorscale: cfg.colorscale,
    colorbar: { title: cfg.colorbar, thickness: 12 },
    showscale: true,
  }];
  Plotly.react("plot-market", data, layout, { responsive: true, displaylogo: false });
}

function refreshMarketSourcePill() {
  const pill = $("marketSourcePill");
  if (!pill) return;
  const src = state.surface && state.surface.source ? String(state.surface.source) : "";
  if (!src) {
    pill.textContent = "";
    pill.classList.remove("ok", "busy", "err");
    return;
  }
  pill.textContent = src;
  pill.classList.remove("err");
  pill.classList.toggle("ok", /^yahoo/i.test(src));
  pill.classList.toggle("busy", /synth/i.test(src));
}

function refreshMarketCutControls() {
  const sel = $("marketCutExpiry");
  if (!sel) return;
  const surf = state.surface;
  if (!surf || !surf.expiries || surf.expiries.length === 0) {
    sel.innerHTML = "";
    sel.disabled = true;
    return;
  }
  sel.innerHTML = "";
  surf.expiries.forEach((t, i) => {
    const opt = document.createElement("option");
    opt.value = String(i);
    opt.textContent = `T = ${t.toFixed(3)}y`;
    sel.appendChild(opt);
  });
  sel.disabled = false;
  sel.value = "0";
}

// Reads the global Strike / Delta toggle that drives all smile-style x-axes
// (market cut, Smiles tab tiles, single-expiry detail). Defaults to "strike"
// if the radio group isn't yet in the DOM.
function getSmileXAxis() {
  return document.querySelector('input[name="smileXAxis"]:checked')?.value || "strike";
}

// ───────────────── Heston delta-axis cache ─────────────────
// The strike→delta transformation is model-derived: for each (T, K) we ask Heston for
// a price, invert to BS IV, then compute BS delta from that IV. We cache the result in
// memory keyed by (calibration stamp + strike list + maturity list) so toggling between
// strike and delta axes — and switching expiries — doesn't hit the server repeatedly.
state.hestonDelta = state.hestonDelta || { stamp: null, byKey: new Map() };

function calibrationStamp() {
  const c = state.calibration;
  if (!c) return null;
  const p = c.hestonParams || c.params;
  if (!p) return null;
  // Anything that changes the model fingerprint goes here. RMSE pins the same iteration
  // count to a deterministic stamp; spot/rate/q are shared across requests but included
  // for safety (a snapshot load can carry different ones).
  return [
    p.kappa, p.theta, p.sigma, p.rho, p.v0,
    state.surface?.spot, surfaceRiskFreeRate(), surfaceDividendYield(),
    c.finalRmse, c.totalIterations
  ].join("|");
}
function hashArr(arr) {
  // Tiny deterministic fingerprint — first/last/length/sum — avoids serialising long arrays.
  if (!Array.isArray(arr) || arr.length === 0) return "0";
  let s = 0;
  for (const v of arr) s += v;
  return `${arr.length}:${arr[0]}:${arr[arr.length - 1]}:${s}`;
}

// Pulls (iv, delta) for the requested (maturity[], strikes[]) grid. Returns from cache when
// possible; otherwise POSTs /api/heston-delta. Caller receives { iv: [[..]], delta: [[..]] }
// shape (nMat × nStrikes). Returns null if there's no calibration to base it on.
async function fetchHestonDelta(maturities, strikes) {
  const stamp = calibrationStamp();
  if (!stamp) return null;
  if (state.hestonDelta.stamp !== stamp) {
    // Calibration changed — drop all cached deltas. Heston params shifted; old deltas are stale.
    state.hestonDelta = { stamp, byKey: new Map() };
  }
  const key = `${hashArr(maturities)}|${hashArr(strikes)}`;
  if (state.hestonDelta.byKey.has(key)) return state.hestonDelta.byKey.get(key);

  const c = state.calibration;
  const cparams = c.hestonParams || c.params;
  const body = {
    params: cparams,
    spot: state.surface.spot,
    riskFreeRate: surfaceRiskFreeRate(),
    dividendYield: surfaceDividendYield(),
    strikes,
    maturities,
  };
  const res = await apiJson("/api/heston-delta", body);
  state.hestonDelta.byKey.set(key, res);
  return res;
}

// Last successful delta grid for the *market* surface's (expiries, strikes) — used by the
// market-cut and Smiles tab tiles. Pre-fetched on toggle / on calibration so that successive
// expiry switches don't refetch.
state.marketDeltaGrid = state.marketDeltaGrid || { stamp: null, delta: null, iv: null };
async function ensureMarketSurfaceDeltaGrid() {
  if (!state.calibration || !state.surface) return null;
  const stamp = calibrationStamp();
  if (state.marketDeltaGrid.stamp === stamp && state.marketDeltaGrid.delta) return state.marketDeltaGrid;
  const r = await fetchHestonDelta(state.surface.expiries, state.surface.strikes);
  if (!r) return null;
  state.marketDeltaGrid = { stamp, delta: r.delta, iv: r.iv };
  return state.marketDeltaGrid;
}
function marketDeltaRow(idx) {
  const g = state.marketDeltaGrid;
  if (!g || !Array.isArray(g.delta) || idx < 0 || idx >= g.delta.length) return null;
  const row = g.delta[idx];
  return Array.isArray(row) ? row.map(v => (v === null || v === undefined || !Number.isFinite(v)) ? null : v) : null;
}

async function renderMarketCut() {
  const surf = state.surface;
  if (!surf) return;
  const sel = $("marketCutExpiry");
  if (!sel || sel.disabled) return;
  if (!Array.isArray(surf.expiries) || !Array.isArray(surf.strikes)
      || surf.expiries.length === 0 || surf.strikes.length === 0) {
    console.warn("renderMarketCut: empty surface grid");
    Plotly.purge("plot-market-cut");
    return;
  }
  const idx = Math.max(0, Math.min(surf.expiries.length - 1, parseInt(sel.value, 10) || 0));
  const T = surf.expiries[idx];

  const cleanRow = (row) => (row || []).map((v) =>
    (v === null || v === undefined || !Number.isFinite(v)) ? null : v
  );
  const ivY   = cleanRow(surf.iv && surf.iv[idx]);
  const callY = cleanRow(surf.callPrice && surf.callPrice[idx]);
  const putY  = cleanRow(surf.putPrice && surf.putPrice[idx]);

  // Delta axis is Heston-derived: ask /api/heston-delta for BS delta at each market strike
  // using the calibrated model. Falls back to strike when no calibration exists yet.
  const wantDelta = getSmileXAxis() === "delta";
  let deltaRow = null;
  if (wantDelta) {
    if (!state.calibration) {
      log("warn", "smile", "delta axis requires a calibration — falling back to strike");
    } else {
      try {
        await ensureMarketSurfaceDeltaGrid();
        deltaRow = marketDeltaRow(idx);
      } catch (e) {
        log("err", "smile", `delta fetch failed: ${e.message}`);
      }
    }
  }
  const useDelta = wantDelta && Array.isArray(deltaRow);
  const xs = useDelta ? deltaRow : surf.strikes.slice();
  const xTitle = useDelta ? "Delta (call, Heston)" : "Strike";

  const traces = [
    {
      x: xs, y: ivY,
      mode: "lines+markers",
      type: "scatter",
      name: "Implied Vol",
      yaxis: "y",
      connectgaps: false,
      marker: { color: COLORS.iv, size: 5 },
      line: { color: COLORS.iv, width: 1 },
    },
  ];
  // Price traces only make sense against strike. Hide them when the user selects delta —
  // call/put price as a function of delta is monotonic but information-poor, and overlaying
  // makes the IV-smile shape harder to read.
  if (!useDelta) {
    traces.push(
      {
        x: xs, y: callY,
        mode: "lines+markers", type: "scatter", name: "Call Price", yaxis: "y2",
        connectgaps: false, marker: { color: COLORS.call, size: 5 }, line: { color: COLORS.call, width: 1 },
      },
      {
        x: xs, y: putY,
        mode: "lines+markers", type: "scatter", name: "Put Price", yaxis: "y2",
        connectgaps: false, marker: { color: COLORS.put, size: 5 }, line: { color: COLORS.put, width: 1 },
      },
    );
  }

  const layout = {
    title: { text: `${surf.ticker} cut @ T = ${T.toFixed(3)}y`, font: { size: 14 } },
    margin: { t: 44, l: 60, r: 60, b: 50 },
    paper_bgcolor: "#1e2630",
    plot_bgcolor: "#1e2630",
    font: { color: "#e7ecf2" },
    xaxis: { title: xTitle, gridcolor: "#2a3340", autorange: useDelta ? "reversed" : true },
    yaxis: { title: "IV", gridcolor: "#2a3340", side: "left" },
    yaxis2: useDelta ? undefined : {
      title: "Price", gridcolor: "#2a3340", overlaying: "y", side: "right", showgrid: false,
    },
    showlegend: true,
    legend: { x: 1, xanchor: "right", y: 1 },
  };
  Plotly.react("plot-market-cut", traces, layout, { responsive: true, displaylogo: false });
}

// Coerce a possibly-jagged grid to a true 2D number-or-null array of shape [nRows][nCols].
// Guards Plotly 3D from null rows / shape mismatch / non-finite entries.
function sanitize2D(z, nRows, nCols) {
  const out = new Array(nRows);
  for (let i = 0; i < nRows; i++) {
    const src = (z && z[i]) ? z[i] : null;
    const row = new Array(nCols);
    for (let j = 0; j < nCols; j++) {
      const v = src ? src[j] : null;
      row[j] = (v === null || v === undefined || !Number.isFinite(v)) ? null : v;
    }
    out[i] = row;
  }
  return out;
}

function renderHestonSurface(res) {
  if (!res || !Array.isArray(res.expiries) || !Array.isArray(res.strikes)
      || res.expiries.length === 0 || res.strikes.length === 0) {
    console.warn("renderHestonSurface: empty grid", res);
    Plotly.purge("plot-heston");
    return;
  }
  const z = sanitize2D(res.hestonIv, res.expiries.length, res.strikes.length);
  const title = `Heston IV (calibrated) — RMSE=${fmt(res.finalRmse, 6)}`;
  const data = [{
    type: "surface",
    x: res.strikes,
    y: res.expiries,
    z,
    connectgaps: false,
    colorscale: "Cividis",
    colorbar: { title: "IV", thickness: 12 },
    showscale: true,
  }];
  Plotly.react("plot-heston", data, baseLayout3D(title), { responsive: true, displaylogo: false });
}

function renderComparison(res) {
  if (!res || !Array.isArray(res.expiries) || !Array.isArray(res.strikes)
      || res.expiries.length === 0 || res.strikes.length === 0) {
    console.warn("renderComparison: empty grid", res);
    Plotly.purge("plot-comparison");
    return;
  }
  const nT = res.expiries.length, nK = res.strikes.length;
  const market = res.marketIv || [];
  // Scatter3d points for market (drop nulls / non-finite).
  const xs = [], ys = [], zs = [];
  for (let i = 0; i < nT; i++) {
    const row = market[i];
    if (!row) continue;
    for (let j = 0; j < nK; j++) {
      const v = row[j];
      if (v === null || v === undefined || !Number.isFinite(v)) continue;
      const x = res.strikes[j], y = res.expiries[i];
      if (!Number.isFinite(x) || !Number.isFinite(y)) continue;
      xs.push(x);
      ys.push(y);
      zs.push(v);
    }
  }
  const z = sanitize2D(res.hestonIv, nT, nK);
  const traces = [
    {
      type: "surface",
      name: "Heston",
      x: res.strikes,
      y: res.expiries,
      z,
      opacity: 0.7,
      connectgaps: false,
      colorscale: "Cividis",
      showscale: false,
    },
  ];
  // Only add scatter3d trace if there's at least one valid market point — Plotly's 3D scene
  // transform throws "t[0]=(s*C-l*E+c*S)*L" on empty/null coordinate arrays.
  if (xs.length > 0) {
    traces.push({
      type: "scatter3d",
      mode: "markers",
      name: "Market",
      x: xs, y: ys, z: zs,
      marker: { size: 3, color: "#4f9dff" },
    });
  }
  Plotly.react("plot-comparison", traces, baseLayout3D("Market vs Heston"), { responsive: true, displaylogo: false });
}

function renderResults(res) {
  const p = (res && res.hestonParams) || {};
  const rows = [
    ["κ (kappa)", p.kappa],
    ["θ (theta)", p.theta],
    ["σ (sigma)", p.sigma],
    ["ρ (rho)", p.rho],
    ["v₀ (v0)", p.v0],
    ["finalRmse", res.finalRmse],
    ["totalIterations", res.totalIterations],
    ["elapsedMs", res.elapsedMs],
  ];
  const tbody = $("resultsTable").querySelector("tbody");
  tbody.innerHTML = "";
  for (const [k, v] of rows) {
    const tr = document.createElement("tr");
    tr.innerHTML = `<td>${k}</td><td class="value">${typeof v === "number" ? fmt(v, 6) : v}</td>`;
    tbody.appendChild(tr);
  }
}

// ───────────────── Smiles tab ─────────────────
async function fetchDenseHestonSmiles(res) {
  const cparams = res.hestonParams || res.params;
  if (!cparams || !state.surface) return;
  const kmin = Math.min(...res.strikes);
  const kmax = Math.max(...res.strikes);
  const denseStrikes = linspace(kmin, kmax, 80);
  const payload = {
    params: cparams,
    spot: state.surface.spot,
    riskFreeRate: surfaceRiskFreeRate(),
    dividendYield: surfaceDividendYield(),
    strikes: denseStrikes,
    maturities: res.expiries,
  };
  const out = await apiJson("/api/heston-surface", payload);
  // out.iv expected shape: [nMaturities][nStrikes]
  state.smiles = { denseStrikes, denseIv: out.iv };
}

async function renderSmiles() {
  const grid = $("smilesGrid");
  const status = $("smilesStatus");
  if (!grid) return;
  if (!state.surface) {
    grid.innerHTML = "";
    if (status) status.textContent = "Load a surface to see smiles.";
    return;
  }
  const surf = state.surface;
  const cal = state.calibration;
  const dense = state.smiles;
  if (status) {
    status.textContent = cal
      ? "Market dots vs Heston smooth fit."
      : "Market IV only — run a calibration to overlay Heston.";
  }

  // Reuse / create per-expiry tiles in place. Re-key by index for now.
  grid.innerHTML = "";
  if (!Array.isArray(surf.expiries) || !Array.isArray(surf.strikes)
      || surf.expiries.length === 0 || surf.strikes.length === 0) {
    console.warn("renderSmiles: empty surface grid");
    return;
  }
  const wantDelta = getSmileXAxis() === "delta";
  const useDeltaPossible = wantDelta && !!cal;
  if (wantDelta && !cal) log("warn", "smile", "delta axis requires a calibration — falling back to strike");

  // Pre-fetch market-strike delta grid + (optionally) dense-strike delta grid in one go
  // so per-tile rendering stays synchronous below.
  let marketDelta = null;
  let denseDelta = null;
  if (useDeltaPossible) {
    try {
      const m = await ensureMarketSurfaceDeltaGrid();
      marketDelta = m?.delta || null;
      if (dense && dense.denseStrikes) {
        const r = await fetchHestonDelta(surf.expiries, dense.denseStrikes);
        denseDelta = r?.delta || null;
      }
    } catch (e) {
      log("err", "smile", `Heston delta fetch failed: ${e.message}`);
    }
  }
  const xTitle = (useDeltaPossible && marketDelta) ? "Delta (Heston)" : "Strike";

  surf.expiries.forEach((t, i) => {
    const cellId = `smile-tile-${i}`;
    const div = document.createElement("div");
    div.className = "smile-tile";
    div.id = cellId;
    grid.appendChild(div);

    // Market trace x-array. With delta selected and a calibration in place, use the
    // pre-fetched Heston delta at the market strikes; otherwise stay on strike.
    const ivRow = (surf.iv && surf.iv[i]) ? surf.iv[i] : [];
    const mktDeltaRow = (useDeltaPossible && Array.isArray(marketDelta) && Array.isArray(marketDelta[i]))
      ? marketDelta[i].map(v => (v === null || v === undefined || !Number.isFinite(v)) ? null : v)
      : null;
    const useDelta = useDeltaPossible && Array.isArray(mktDeltaRow);
    const mktX = useDelta ? mktDeltaRow : surf.strikes.slice();
    const mktY = (Array.isArray(ivRow) ? ivRow : []).map((v) =>
      (v === null || v === undefined || !Number.isFinite(v)) ? null : v);

    const traces = [
      {
        x: mktX, y: mktY,
        mode: "lines+markers",
        type: "scatter",
        name: "Market",
        connectgaps: false,
        marker: { color: COLORS.market, size: 5 },
        line: { color: COLORS.market, width: 1 },
        showlegend: i === 0,
      },
    ];

    if (cal) {
      let hesX = null, hesY = null;
      if (useDelta) {
        // Heston curve in delta-IV space: server returned (iv, delta) for the dense or
        // market strike set in this batch, so we read both from the cached response.
        if (dense && dense.denseStrikes && dense.denseIv && dense.denseIv[i] && Array.isArray(denseDelta) && Array.isArray(denseDelta[i])) {
          hesY = dense.denseIv[i].map((v) => (v === null || v === undefined || !Number.isFinite(v)) ? null : v);
          hesX = denseDelta[i].map((v) => (v === null || v === undefined || !Number.isFinite(v)) ? null : v);
        } else if (cal.strikes && cal.hestonIv && cal.hestonIv[i] && Array.isArray(marketDelta) && Array.isArray(marketDelta[i])) {
          // Fallback when the dense grid hasn't been fetched yet (e.g. before the Smiles
          // tab has run its dense pre-fetch). Reuse the market-grid Heston IV from the
          // calibration result and the corresponding deltas we pulled into marketDelta.
          hesY = cal.hestonIv[i].map((v) => (v === null || v === undefined || !Number.isFinite(v)) ? null : v);
          hesX = marketDelta[i].map((v) => (v === null || v === undefined || !Number.isFinite(v)) ? null : v);
        }
      } else {
        if (dense && dense.denseStrikes && dense.denseIv && dense.denseIv[i]) {
          hesX = dense.denseStrikes;
          hesY = dense.denseIv[i].map((v) => (v === null || v === undefined || !Number.isFinite(v)) ? null : v);
        } else if (cal.strikes && cal.hestonIv && cal.hestonIv[i]) {
          // Fallback: use market-grid Heston IV from the calibration result.
          hesX = cal.strikes;
          hesY = cal.hestonIv[i].map((v) => (v === null || v === undefined || !Number.isFinite(v)) ? null : v);
        }
      }
      if (hesX && hesY) traces.push({
        x: hesX, y: hesY,
        mode: "lines",
        type: "scatter",
        name: "Heston",
        connectgaps: false,
        line: { color: COLORS.heston, width: 2 },
        showlegend: i === 0,
      });
    }

    const layout = {
      title: { text: `T = ${t.toFixed(3)}y`, font: { size: 12 } },
      margin: { t: 28, l: 44, r: 10, b: 36 },
      paper_bgcolor: "#1a2029",
      plot_bgcolor: "#1a2029",
      font: { color: "#e7ecf2", size: 10 },
      xaxis: { title: xTitle, gridcolor: "#2a3340", tickfont: { size: 9 }, autorange: useDelta ? "reversed" : true },
      yaxis: { title: "IV", gridcolor: "#2a3340", tickfont: { size: 9 } },
      showlegend: false,
    };
    Plotly.react(cellId, traces, layout, { responsive: true, displaylogo: false, displayModeBar: false });
  });
}


// ───── Single-expiry detail (within the Smiles tab) ─────
function refreshSmileDetailControls() {
  const sel = $("expirySelect");
  if (!sel) return;
  if (!state.surface || !state.surface.expiries || state.surface.expiries.length === 0) {
    sel.innerHTML = "";
    sel.disabled = true;
    return;
  }
  const exps = state.surface.expiries;
  sel.innerHTML = "";
  exps.forEach((t, i) => {
    const opt = document.createElement("option");
    opt.value = String(i);
    opt.textContent = `T = ${t.toFixed(3)}y`;
    sel.appendChild(opt);
  });
  sel.disabled = false;
  sel.value = "0";

  // Default K range with 10% padding, rounded to whole numbers.
  const ks = state.surface.strikes;
  if (ks && ks.length) {
    const kmin = Math.round(Math.min(...ks) * 0.9);
    const kmax = Math.round(Math.max(...ks) * 1.1);
    $("smileKmin").value = String(kmin);
    $("smileKmax").value = String(kmax);
  }
}

function clearSmileDetailPlot() {
  const el = $("smileDetail");
  if (!el) return;
  Plotly.purge(el);
  el.innerHTML = "";
}

function readSmileDetailInputs() {
  const idx = parseInt($("expirySelect").value, 10);
  const strikeCount = Math.max(10, Math.min(400, parseInt($("smileStrikeCount").value, 10) || 80));
  const kmin = parseFloat($("smileKmin").value);
  const kmax = parseFloat($("smileKmax").value);
  return { idx, strikeCount, kmin, kmax };
}

async function fetchSmileDetailHeston(idx, strikeCount, kmin, kmax) {
  const cal = state.calibration;
  if (!cal || !state.surface) return null;
  const cparams = cal.hestonParams || cal.params;
  if (!cparams) return null;
  const T = state.surface.expiries[idx];
  const strikes = linspace(kmin, kmax, strikeCount);
  const payload = {
    params: cparams,
    spot: state.surface.spot,
    riskFreeRate: surfaceRiskFreeRate(),
    dividendYield: surfaceDividendYield(),
    strikes,
    maturities: [T],
  };
  const out = await apiJson("/api/heston-surface", payload);
  // out.iv shape: [1][nStrikes]
  return { strikes, iv: (out.iv && out.iv[0]) ? out.iv[0] : [] };
}

async function plotSmileDetail() {
  clearError();
  if (!state.surface) { showError("Load a surface first."); return; }
  const { idx, strikeCount, kmin, kmax } = readSmileDetailInputs();
  if (!Number.isFinite(kmin) || !Number.isFinite(kmax) || kmax <= kmin) {
    showError("Invalid Kmin/Kmax for smile detail.");
    return;
  }
  $("plotSmileDetailBtn").disabled = true;
  try {
    let hesData = null;
    if (state.calibration) {
      const stamp = state.calibration.totalIterations + "|" + state.calibration.finalRmse;
      const key = `${idx}|${strikeCount}|${kmin}|${kmax}|${stamp}`;
      if (state.smileDetail.cache.has(key)) {
        hesData = state.smileDetail.cache.get(key);
      } else {
        hesData = await fetchSmileDetailHeston(idx, strikeCount, kmin, kmax);
        if (hesData) state.smileDetail.cache.set(key, hesData);
      }
      state.smileDetail.lastKey = key;
    }
    state.smileDetail.rendered = true;
    renderSmileDetail(hesData);
  } catch (e) {
    showError("Smile detail fetch failed: " + e.message);
  } finally {
    $("plotSmileDetailBtn").disabled = false;
  }
}

// Re-render using current inputs. If hesData not supplied, look up cache.
async function renderSmileDetail(hesData) {
  if (!state.surface) return;
  const { idx, strikeCount, kmin, kmax } = readSmileDetailInputs();
  const surf = state.surface;
  if (!Array.isArray(surf.expiries) || !Array.isArray(surf.strikes)
      || surf.expiries.length === 0 || surf.strikes.length === 0) {
    console.warn("renderSmileDetail: empty surface grid");
    Plotly.purge("smileDetail");
    return;
  }
  const safeIdx = Math.max(0, Math.min(surf.expiries.length - 1, idx || 0));
  const T = surf.expiries[safeIdx];

  const wantDelta = getSmileXAxis() === "delta";
  let mktDeltaRow = null;
  if (wantDelta && state.calibration) {
    try {
      await ensureMarketSurfaceDeltaGrid();
      mktDeltaRow = marketDeltaRow(safeIdx);
    } catch (e) {
      log("err", "smile", `delta fetch failed: ${e.message}`);
    }
  } else if (wantDelta && !state.calibration) {
    log("warn", "smile", "delta axis requires a calibration — falling back to strike");
  }
  const useDelta = wantDelta && Array.isArray(mktDeltaRow);

  // Market trace at original strikes (skip null/NaN). When delta-axis is selected, swap
  // the x coordinate for the Heston-derived per-cell delta fetched above.
  const mktX = [], mktY = [];
  const ivRow = (surf.iv && surf.iv[safeIdx]) || [];
  for (let j = 0; j < surf.strikes.length; j++) {
    const v = ivRow[j];
    if (v === null || v === undefined || !Number.isFinite(v)) continue;
    if (useDelta) {
      const d = mktDeltaRow[j];
      if (d === null || !Number.isFinite(d)) continue;
      mktX.push(d);
    } else {
      mktX.push(surf.strikes[j]);
    }
    mktY.push(v);
  }

  const traces = [
    {
      x: mktX, y: mktY,
      mode: "markers",
      type: "scatter",
      name: "Market",
      marker: { color: COLORS.market, size: 10 },
    },
  ];

  // If calibrated and we have hesData (or cached), add the Heston smooth line.
  let heston = hesData;
  if (!heston && state.calibration) {
    const stamp = state.calibration.totalIterations + "|" + state.calibration.finalRmse;
    const key = `${idx}|${strikeCount}|${kmin}|${kmax}|${stamp}`;
    if (state.smileDetail.cache.has(key)) heston = state.smileDetail.cache.get(key);
    else {
      // Fetch on demand (e.g. auto-rerender after calibration with prior inputs).
      try {
        heston = await fetchSmileDetailHeston(idx, strikeCount, kmin, kmax);
        if (heston) state.smileDetail.cache.set(key, heston);
        state.smileDetail.lastKey = key;
      } catch (e) {
        showError("Smile detail fetch failed: " + e.message);
      }
    }
  }

  if (heston && heston.strikes && heston.iv) {
    const hesY = heston.iv.map((v) =>
      (v === null || v === undefined || !Number.isFinite(v)) ? null : v
    );
    // For delta-axis, fetch BS-deltas at the dense strike grid in a single call. The same
    // endpoint computes IV and delta, but we already have the dense IV from fetchSmileDetailHeston
    // — we just need the deltas for these specific (T, strikes).
    let hesX = heston.strikes;
    if (useDelta) {
      try {
        const r = await fetchHestonDelta([T], heston.strikes);
        const drow = r?.delta?.[0];
        if (Array.isArray(drow)) {
          hesX = drow.map(v => (v === null || v === undefined || !Number.isFinite(v)) ? null : v);
        }
      } catch (e) {
        log("err", "smile", `dense delta fetch failed: ${e.message}`);
      }
    }
    traces.push({
      x: hesX, y: hesY,
      mode: "lines",
      type: "scatter",
      name: "Heston",
      connectgaps: false,
      line: { color: COLORS.heston, width: 2 },
    });
  }

  const titleBase = `Smile detail — T = ${T.toFixed(3)}y`;
  const title = state.calibration
    ? titleBase
    : titleBase + " (market only — run calibration to overlay Heston)";

  const layout = {
    title: { text: title, font: { size: 14 } },
    margin: { t: 44, l: 60, r: 20, b: 50 },
    paper_bgcolor: "#1e2630",
    plot_bgcolor: "#1e2630",
    font: { color: "#e7ecf2" },
    xaxis: { title: useDelta ? "Delta (call, Heston)" : "Strike", gridcolor: "#2a3340", autorange: useDelta ? "reversed" : true },
    yaxis: { title: "Implied volatility", gridcolor: "#2a3340" },
    showlegend: true,
    legend: { x: 1, xanchor: "right", y: 1 },
  };
  Plotly.react("smileDetail", traces, layout, { responsive: true, displaylogo: false });
}

async function plotInterpolatedHestonSurface() {
  clearError();
  if (!state.calibration) { showError("Run a calibration first."); return; }
  const c = state.calibration;
  const cparams = c.hestonParams || c.params;
  const nk = Math.max(2, parseInt($("interpStrikes").value, 10) || 41);
  const nt = Math.max(2, parseInt($("interpMats").value, 10) || 25);
  const kmin = Math.min(...c.strikes), kmax = Math.max(...c.strikes);
  const tmin = Math.min(...c.expiries), tmax = Math.max(...c.expiries);
  const strikes = linspace(kmin, kmax, nk);
  const maturities = linspace(tmin, tmax, nt);

  $("plotInterpBtn").disabled = true;
  try {
    const res = await apiJson("/api/heston-surface", {
      params: cparams,
      spot: state.surface.spot,
      riskFreeRate: surfaceRiskFreeRate(),
      dividendYield: surfaceDividendYield(),
      strikes,
      maturities,
    });
    const data = [{
      type: "surface",
      x: strikes,
      y: maturities,
      z: res.iv,
      connectgaps: true,
      colorscale: "Viridis",
      colorbar: { title: "IV", thickness: 12 },
    }];
    Plotly.react("plot-interp", data, baseLayout3D("Interpolated Heston Surface"),
      { responsive: true, displaylogo: false });
  } catch (e) {
    showError("Interpolated surface failed: " + e.message);
  } finally {
    $("plotInterpBtn").disabled = false;
  }
}

// ───────────────── Greeks ─────────────────
// Display style per Greek so the user gets useful axis labels and colour ramps.
const GREEK_META = {
  delta: { label: "Delta", colorscale: "RdBu",    title: "Heston Delta" },
  gamma: { label: "Gamma", colorscale: "Viridis", title: "Heston Gamma" },
  vega:  { label: "Vega",  colorscale: "Cividis", title: "Heston Vega"  },
  theta: { label: "Theta", colorscale: "Magma",   title: "Heston Theta" },
  rho:   { label: "Rho",   colorscale: "Plasma",  title: "Heston Rho"   },
};

function selectedGreek() {
  return document.querySelector('input[name="greekQty"]:checked')?.value || "delta";
}

// Progress-bar helpers. Throttle the DOM update to once per animation frame so an SSE
// stream firing every few ms doesn't thrash layout — the latest frame wins.
let _greeksProgressPending = null;
let _greeksRafId = 0;
function updateGreeksProgress(frame, nt, nk) {
  _greeksProgressPending = { frame, nt, nk };
  if (_greeksRafId) return;
  _greeksRafId = requestAnimationFrame(() => {
    _greeksRafId = 0;
    const { frame: f, nt: NT, nk: NK } = _greeksProgressPending || {};
    if (!f) return;
    const pct = f.total > 0 ? (100 * f.iter / f.total) : 0;
    const fill = $("greeksProgressFill");
    const text = $("greeksProgressText");
    if (fill) fill.style.width = pct.toFixed(1) + "%";
    if (text) {
      const exp = (typeof f.expiry === "number") ? f.expiry.toFixed(4) : "?";
      const strk = (typeof f.strike === "number") ? f.strike.toFixed(2) : "?";
      text.textContent =
        `${f.iter}/${f.total} (${pct.toFixed(1)}%) · ` +
        `expiry ${f.expiryIdx + 1}/${NT} (T=${exp}y) · ` +
        `strike ${f.strikeIdx + 1}/${NK} (K=${strk})`;
    }
  });
}
function showGreeksProgress(show) {
  $("greeksProgressWrap")?.classList.toggle("hidden", !show);
  if (!show && $("greeksProgressFill")) $("greeksProgressFill").style.width = "0%";
}

// Pull the (Kmin, Kmax, Tmin, Tmax, nK, nT) inputs from the Greeks controls. Falls back
// to the calibration's natural range when a box is left blank.
function readGreekRange() {
  const c = state.calibration;
  const calibKmin = c ? Math.min(...c.strikes) : 0;
  const calibKmax = c ? Math.max(...c.strikes) : 0;
  const calibTmin = c ? Math.min(...c.expiries) : 0;
  const calibTmax = c ? Math.max(...c.expiries) : 0;
  const parseOr = (id, fallback) => {
    const v = parseFloat($(id).value);
    return Number.isFinite(v) ? v : fallback;
  };
  return {
    kmin: parseOr("greekKmin", calibKmin),
    kmax: parseOr("greekKmax", calibKmax),
    tmin: parseOr("greekTmin", calibTmin),
    tmax: parseOr("greekTmax", calibTmax),
    nk: Math.max(2, parseInt($("greekStrikes").value, 10) || 41),
    nt: Math.max(2, parseInt($("greekMats").value, 10) || 25),
  };
}

// Pre-fills the (K, T) range boxes from the current calibration so the user sees what
// the default span would be. Called after a calibration completes / a snapshot is loaded
// and when the user clicks "Use calibration range".
function applyCalibRangeToGreekInputs() {
  const c = state.calibration;
  if (!c) return;
  const kmin = Math.min(...c.strikes), kmax = Math.max(...c.strikes);
  const tmin = Math.min(...c.expiries), tmax = Math.max(...c.expiries);
  $("greekKmin").value = kmin.toFixed(2);
  $("greekKmax").value = kmax.toFixed(2);
  $("greekTmin").value = tmin.toFixed(4);
  $("greekTmax").value = tmax.toFixed(4);
}

async function computeGreeksGrid() {
  clearError();
  if (!state.calibration) {
    showError("Run or load a calibration first — Greeks are computed from the fitted Heston parameters.");
    return;
  }
  const c = state.calibration;
  const cparams = c.hestonParams || c.params;
  const r = readGreekRange();
  if (!(r.kmax > r.kmin) || !(r.tmax > r.tmin)) {
    showError("Strike/maturity range invalid — max must exceed min.");
    return;
  }
  const { nk, nt } = r;
  const strikes = linspace(r.kmin, r.kmax, nk);
  const maturities = linspace(r.tmin, r.tmax, nt);

  setStatus("greeksStatus", "Streaming…", "busy");
  $("computeGreeksBtn").disabled = true;
  $("cancelGreeksBtn").classList.remove("hidden");
  showGreeksProgress(true);
  updateGreeksProgress({ iter: 0, total: nt * nk, expiryIdx: 0, strikeIdx: 0, expiry: 0, strike: 0 }, nt, nk);
  log("info", "greeks", `stream ${nk}×${nt} grid (${nk*nt} cells) for ticker=${state.surface?.ticker || "?"}`);

  const controller = new AbortController();
  state.greeksAbort = controller;

  const body = {
    params: cparams,
    spot: state.surface.spot,
    riskFreeRate: surfaceRiskFreeRate(),
    dividendYield: surfaceDividendYield(),
    strikes,
    maturities,
  };

  try {
    const final = await streamGreeks(body, controller.signal, (frame) =>
      updateGreeksProgress(frame, nt, nk));
    if (!final) throw new Error("Stream closed without a final frame.");
    state.greeks = {
      strikes, maturities,
      iv: final.iv,
      delta: final.delta, gamma: final.gamma, vega: final.vega, theta: final.theta, rho: final.rho,
      computedAt: Date.now(),
    };
    setStatus("greeksStatus", "Done", "ok");
    log("ok", "greeks", `received full grid ${nk}×${nt}`);
    renderGreeksSurface();
  } catch (e) {
    if (e.name === "AbortError") {
      setStatus("greeksStatus", "Cancelled", "err");
      log("warn", "greeks", "cancelled by user");
    } else {
      setStatus("greeksStatus", "Failed", "err");
      showError("Greeks compute failed: " + e.message);
      log("err", "greeks", e.message);
    }
  } finally {
    $("computeGreeksBtn").disabled = false;
    $("cancelGreeksBtn").classList.add("hidden");
    state.greeksAbort = null;
    // Leave the bar at 100% so the user can see the final state — clears on next run.
  }
}

function cancelGreeksCompute() {
  state.greeksAbort?.abort();
}

// SSE-over-POST stream parser for /api/heston-surface-with-greeks/stream.
// Mirrors apiStreamCalibrate's shape but with progress / done / error event types specific
// to this endpoint. Returns the parsed 'done' payload when the stream completes.
async function streamGreeks(body, signal, onProgress) {
  const t0 = performance.now();
  const res = await fetch(API + "/api/heston-surface-with-greeks/stream", {
    method: "POST",
    headers: { "Content-Type": "application/json", "Accept": "text/event-stream" },
    body: JSON.stringify(body),
    signal,
  });
  if (!res.ok || !res.body) {
    const txt = await res.text().catch(() => "");
    log("err", "sse", `${res.status} ${res.statusText}: ${txt.slice(0, 200)}`);
    throw new Error(`${res.status} ${res.statusText}: ${txt}`);
  }
  log("ok", "sse", `greeks stream connected`);
  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  let buf = "";
  let finalResult = null;
  let progressCount = 0;
  let lastLogAt = 0;

  while (true) {
    const { value, done } = await reader.read();
    if (done) break;
    buf += decoder.decode(value, { stream: true });

    let idx;
    while ((idx = buf.indexOf("\n\n")) !== -1) {
      const raw = buf.slice(0, idx);
      buf = buf.slice(idx + 2);

      let event = "message";
      const dataLines = [];
      for (const line of raw.split("\n")) {
        if (line.startsWith("event:")) event = line.slice(6).trim();
        else if (line.startsWith("data:")) dataLines.push(line.slice(5).trim());
      }
      if (dataLines.length === 0) continue;
      let payload;
      try { payload = JSON.parse(dataLines.join("\n")); }
      catch (e) { log("err", "sse", `parse failed: ${e.message}`); continue; }

      if (event === "started") {
        log("info", "greeks", `started: ${payload.total} cells (${payload.expiries}×${payload.strikes})`);
      } else if (event === "progress") {
        progressCount++;
        onProgress(payload);
        // Throttle activity-log entries to ~4/sec; the progress bar already shows every frame.
        const now = performance.now();
        if (now - lastLogAt > 250) {
          lastLogAt = now;
          log("event", `cell ${payload.iter}/${payload.total}`,
            `T=${payload.expiry?.toFixed(4)} K=${payload.strike?.toFixed(2)} δ=${payload.delta?.toFixed ? payload.delta.toFixed(4) : payload.delta}`);
        }
      } else if (event === "done") {
        finalResult = payload;
        log("ok", "sse", `done (${progressCount} cells, ${(performance.now() - t0).toFixed(0)}ms)`);
      } else if (event === "error") {
        const msg = payload?.message || "Greeks compute failed.";
        log("err", "sse", `server error: ${msg}`);
        throw new Error(msg);
      }
    }
  }
  return finalResult;
}

function renderGreeksSurface() {
  if (!state.greeks) {
    log("warn", "greeks", "renderGreeksSurface called with no cached data");
    Plotly.purge("plot-greeks");
    return;
  }
  const which = selectedGreek();
  const meta = GREEK_META[which];
  const z = state.greeks[which];
  if (!z) {
    log("err", "greeks", `field "${which}" missing — server may not have computed it (includeGreeks=false?)`);
    showError(`Greek "${which}" missing from response.`);
    return;
  }
  // Diagnostic: count non-null cells and report a sample to the activity log so the user
  // can confirm data is being received even if the 3D plot fails to render visibly.
  let nValid = 0, total = 0, sample = null;
  for (const row of z) {
    for (const v of row) {
      total++;
      if (v !== null && Number.isFinite(v)) {
        nValid++;
        if (sample === null) sample = v;
      }
    }
  }
  log("info", "greeks", `render ${which}: ${nValid}/${total} valid, sample=${sample?.toExponential ? sample.toExponential(3) : sample}`);

  const layout = baseLayout3D(meta.title);
  if (layout.scene) layout.scene.zaxis = { ...layout.scene.zaxis, title: meta.label };

  const data = [{
    type: "surface",
    x: state.greeks.strikes,
    y: state.greeks.maturities,
    z,
    connectgaps: true,
    colorscale: meta.colorscale,
    colorbar: { title: meta.label, thickness: 12 },
  }];
  Plotly.react("plot-greeks", data, layout, { responsive: true, displaylogo: false });
  // Plotly 3D plots that are created while the tab is hidden can end up with a 0-width
  // canvas. Force a resize on the next animation frame so the WebGL context picks up the
  // visible container size.
  requestAnimationFrame(() => {
    const el = document.getElementById("plot-greeks");
    if (el && el.offsetWidth > 0) Plotly.Plots.resize(el);
  });
  renderGreeksTable();
}

// Format a Greek value for the table. Tight precision rules so the columns line up:
// near-integer values get fewer decimals, very small values use exponential form,
// nulls render as an em-dash.
function fmtGreek(v) {
  if (v === null || v === undefined) return "—";
  if (!Number.isFinite(v)) return "NaN";
  const a = Math.abs(v);
  if (a === 0) return "0";
  if (a >= 1000 || a < 1e-3) return v.toExponential(3);
  if (a >= 10) return v.toFixed(3);
  if (a >= 1) return v.toFixed(4);
  return v.toFixed(5);
}

// Render the currently-selected Greek as a (T × K) table. Headers are sticky on both axes
// so the user can scroll a 25 × 41 grid without losing context.
function renderGreeksTable() {
  const wrap = $("greeksTableWrap");
  if (!wrap) return;
  if (!state.greeks) { wrap.innerHTML = ""; return; }

  const show = $("greeksTableShow")?.checked ?? true;
  wrap.classList.toggle("hidden", !show);
  if (!show) return;

  const which = selectedGreek();
  const meta = GREEK_META[which];
  $("greeksTableTitle").textContent = `${meta.label} table (rows = maturity, cols = strike)`;

  const z = state.greeks[which];
  if (!z) { wrap.innerHTML = ""; return; }

  const strikes = state.greeks.strikes;
  const maturities = state.greeks.maturities;

  // Build the table HTML in one string concat — DOM-construction APIs are noticeably
  // slower for 25×41 = 1025 cells on first render.
  const parts = ['<table class="greeks-table"><thead><tr><th>T \\ K</th>'];
  for (const k of strikes) parts.push(`<th>${k.toFixed(2)}</th>`);
  parts.push("</tr></thead><tbody>");
  for (let i = 0; i < maturities.length; i++) {
    parts.push(`<tr><th>${maturities[i].toFixed(4)}</th>`);
    const row = z[i] || [];
    for (let j = 0; j < strikes.length; j++) {
      const v = row[j];
      const cls = v === null || v === undefined || !Number.isFinite(v) ? ' class="null-cell"' : "";
      parts.push(`<td${cls}>${fmtGreek(v)}</td>`);
    }
    parts.push("</tr>");
  }
  parts.push("</tbody></table>");
  wrap.innerHTML = parts.join("");
}

// Download the current Greek grid as CSV. Maturities go down rows, strikes across columns.
// NaN/null cells become empty cells so spreadsheets show blanks rather than literal "NaN".
function downloadGreekCsv() {
  if (!state.greeks) { showError("Compute Greeks first."); return; }
  const which = selectedGreek();
  const z = state.greeks[which];
  if (!z) { showError(`Greek "${which}" missing.`); return; }
  const strikes = state.greeks.strikes;
  const maturities = state.greeks.maturities;
  const rows = [["T\\K", ...strikes.map(k => k.toFixed(4))].join(",")];
  for (let i = 0; i < maturities.length; i++) {
    const row = z[i] || [];
    const cells = [maturities[i].toFixed(6)];
    for (let j = 0; j < strikes.length; j++) {
      const v = row[j];
      cells.push(v === null || v === undefined || !Number.isFinite(v) ? "" : String(v));
    }
    rows.push(cells.join(","));
  }
  const csv = rows.join("\n") + "\n";
  const blob = new Blob([csv], { type: "text/csv" });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  const t = state.surface?.ticker || "snapshot";
  const safe = String(t).toLowerCase().replace(/[^a-z0-9.-]+/g, "_");
  a.href = url; a.download = `heston-${safe}-${which}.csv`;
  document.body.appendChild(a); a.click(); document.body.removeChild(a);
  URL.revokeObjectURL(url);
  log("ok", "greeks", `csv exported (${which}, ${maturities.length}×${strikes.length})`);
}

// Rates the loaded surface was built with — the calibrator and any post-fit Heston pricing
// must use the same values, otherwise the model and market IVs sit on different forward curves.
function surfaceRiskFreeRate() {
  const r = state.surface && state.surface.riskFreeRate;
  return Number.isFinite(r) ? r : 0.045;
}
function surfaceDividendYield() {
  const q = state.surface && state.surface.dividendYield;
  return Number.isFinite(q) ? q : 0.013;
}

function linspace(a, b, n) {
  if (n <= 1) return [a];
  const step = (b - a) / (n - 1);
  const out = new Array(n);
  for (let i = 0; i < n; i++) out[i] = a + step * i;
  return out;
}

// ───────────────── Tabs ─────────────────
function switchTab(name) {
  for (const btn of $$(".tab-btn")) btn.classList.toggle("active", btn.dataset.tab === name);
  for (const panel of $$(".tab-panel")) panel.classList.toggle("active", panel.id === `tab-${name}`);
  // Resize the now-visible Plotly div.
  const id = `plot-${name}`;
  const el = document.getElementById(id);
  if (el && el.data) Plotly.Plots.resize(el);
  // Market tab also has a cut plot below the surface.
  if (name === "market") {
    const cut = document.getElementById("plot-market-cut");
    if (cut && cut.data) Plotly.Plots.resize(cut);
  }
  // Resize Smiles tiles when its tab activates.
  if (name === "smiles") {
    for (const c of $$(".smile-tile")) if (c.data) Plotly.Plots.resize(c);
    const sd = document.getElementById("smileDetail");
    if (sd && sd.data) Plotly.Plots.resize(sd);
  }
  // Resize interpolated surface when results tab activates.
  if (name === "results") {
    const ip = document.getElementById("plot-interp");
    if (ip && ip.data) Plotly.Plots.resize(ip);
  }
}

// ───────────────── Snapshots ─────────────────
// Persist the loaded surface + last calibration to a server-side file and re-hydrate
// the UI from one later. The frontend builds the snapshot payload from `state` so the
// save endpoint is stateless — useful when the in-memory cache has rotated.
function buildSurfaceSnapshot() {
  const s = state.surface;
  if (!s) return null;
  return {
    ticker: s.ticker,
    source: s.source,
    spot: s.spot,
    riskFreeRate: s.riskFreeRate,
    dividendYield: s.dividendYield,
    expiries: s.expiries,
    strikes: s.strikes,
    iv: s.iv,
    callPrice: s.callPrice,
    putPrice: s.putPrice,
    cleanStats: s.cleanStats || null,
  };
}
function buildCalibrationSnapshot() {
  const c = state.calibration;
  if (!c) return null;
  return {
    params: c.hestonParams,
    finalRmse: c.finalRmse,
    converged: !!c.converged,
    totalIterations: c.totalIterations,
    elapsedMs: c.elapsedMs,
    expiries: c.expiries,
    strikes: c.strikes,
    marketIv: c.marketIv,
    hestonIv: c.hestonIv,
    stages: c.stages || [],
    history: c.history || [],
  };
}

// Build the full Snapshot JSON object the way the server expects it.
function composeSnapshotPayload() {
  if (!state.surface) return null;
  return {
    version: "1.0",
    createdAtUtc: new Date().toISOString(),
    surface: buildSurfaceSnapshot(),
    calibration: buildCalibrationSnapshot(),
  };
}

function defaultSnapshotFilename() {
  const t = state.surface?.ticker || "snapshot";
  const safe = String(t).toLowerCase().replace(/[^a-z0-9.-]+/g, "_").replace(/^_+|_+$/g, "");
  const ts = new Date().toISOString().replace(/[:T]/g, "-").slice(0, 16);
  return `heston-${safe || "snapshot"}-${ts}.json`;
}

// Save the snapshot to a local file via the browser. Uses the File System Access API
// (Chrome/Edge) for a real "Save As…" dialog when available; falls back to the classic
// anchor-download trick everywhere else.
async function saveSnapshotToFile() {
  clearError();
  if (!state.surface) { showError("Load a surface before saving."); return; }
  const payload = composeSnapshotPayload();
  const json = JSON.stringify(payload, null, 2);
  const suggested = defaultSnapshotFilename();
  setStatus("snapshotStatus", "Saving…", "busy");
  try {
    if (window.showSaveFilePicker) {
      const handle = await window.showSaveFilePicker({
        suggestedName: suggested,
        types: [{ description: "Heston snapshot (JSON)", accept: { "application/json": [".json"] } }],
      });
      const writable = await handle.createWritable();
      await writable.write(json);
      await writable.close();
      setStatus("snapshotStatus", `Saved ${handle.name}`, "ok");
      log("ok", "snap", `saved file "${handle.name}" (${json.length} bytes)`);
    } else {
      // Fallback: synthesize a blob URL and click an anchor — browser shows its
      // standard download prompt (which is "save as" in most configurations).
      const blob = new Blob([json], { type: "application/json" });
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url; a.download = suggested;
      document.body.appendChild(a); a.click(); document.body.removeChild(a);
      URL.revokeObjectURL(url);
      setStatus("snapshotStatus", `Downloaded ${suggested}`, "ok");
      log("ok", "snap", `downloaded "${suggested}" (${json.length} bytes)`);
    }
  } catch (e) {
    if (e?.name === "AbortError") {
      setStatus("snapshotStatus", "Cancelled", "err");
      log("warn", "snap", "save cancelled by user");
      return;
    }
    setStatus("snapshotStatus", "Save failed", "err");
    showError("Save failed: " + e.message);
    log("err", "snap", `save failed: ${e.message}`);
  }
}

// Load a snapshot from a local file. Uses the File System Access API when available;
// otherwise triggers the hidden <input type="file"> the OS file picker is attached to.
async function loadSnapshotFromFile() {
  clearError();
  setStatus("snapshotStatus", "Choosing file…", "busy");
  try {
    let text;
    let filename;
    if (window.showOpenFilePicker) {
      const [handle] = await window.showOpenFilePicker({
        types: [{ description: "Heston snapshot (JSON)", accept: { "application/json": [".json"] } }],
        multiple: false,
      });
      filename = handle.name;
      const file = await handle.getFile();
      text = await file.text();
    } else {
      const file = await pickFileViaInput();
      filename = file.name;
      text = await file.text();
    }
    const snap = JSON.parse(text);
    hydrateFromSnapshot(snap);
    setStatus("snapshotStatus", `Loaded ${filename}`, "ok");
    log("ok", "snap", `loaded file "${filename}" (${text.length} bytes)`);
  } catch (e) {
    if (e?.name === "AbortError") {
      setStatus("snapshotStatus", "Cancelled", "err");
      log("warn", "snap", "load cancelled by user");
      return;
    }
    setStatus("snapshotStatus", "Load failed", "err");
    showError("Load failed: " + e.message);
    log("err", "snap", `load failed: ${e.message}`);
  }
}

// Promise-wrapped <input type="file"> for browsers without the File System Access API.
// Resolves on the first change event; rejects with AbortError on cancellation.
function pickFileViaInput() {
  return new Promise((resolve, reject) => {
    const input = $("snapshotFileInput");
    if (!input) return reject(new Error("File input not in DOM"));
    input.value = "";
    let settled = false;
    const onChange = () => {
      settled = true;
      input.removeEventListener("change", onChange);
      window.removeEventListener("focus", onCancel);
      const f = input.files?.[0];
      if (!f) {
        const err = new Error("No file selected");
        err.name = "AbortError";
        reject(err);
      } else {
        resolve(f);
      }
    };
    // Heuristic cancellation: focus returns to the window without a change firing.
    const onCancel = () => {
      setTimeout(() => {
        if (settled) return;
        if (input.files?.length) return;
        input.removeEventListener("change", onChange);
        window.removeEventListener("focus", onCancel);
        const err = new Error("Cancelled");
        err.name = "AbortError";
        reject(err);
      }, 250);
    };
    input.addEventListener("change", onChange);
    window.addEventListener("focus", onCancel, { once: true });
    input.click();
  });
}

async function refreshSnapshotList() {
  try {
    const list = await apiGet("/api/snapshot/list");
    const sel = $("snapshotPicker");
    sel.innerHTML = "";
    if (list.length === 0) {
      const opt = document.createElement("option");
      opt.value = ""; opt.textContent = "(no snapshots yet)";
      sel.appendChild(opt);
      sel.disabled = true;
    } else {
      sel.disabled = false;
      for (const entry of list) {
        const opt = document.createElement("option");
        opt.value = entry.name;
        const calib = entry.hasCalibration ? " · calibrated" : "";
        const ts = new Date(entry.createdAtUtc).toISOString().slice(0, 16).replace("T", " ");
        opt.textContent = `${entry.name} — ${entry.ticker} (${entry.source})${calib} · ${ts}`;
        sel.appendChild(opt);
      }
    }
    log("info", "snap", `${list.length} snapshot(s) available`);
  } catch (e) {
    log("err", "snap", `list failed: ${e.message}`);
  }
}

async function saveSnapshot() {
  clearError();
  if (!state.surface) { showError("Load a surface before saving."); return; }
  const name = ($("snapshotName").value || "").trim();
  if (!name) { showError("Snapshot name is required."); return; }
  setStatus("snapshotStatus", "Saving…", "busy");
  try {
    const payload = {
      name,
      snapshot: {
        version: "1.0",
        createdAtUtc: new Date().toISOString(),
        surface: buildSurfaceSnapshot(),
        calibration: buildCalibrationSnapshot(),
      },
    };
    const res = await apiJson("/api/snapshot/save", payload);
    setStatus("snapshotStatus", `Saved as ${res.name}`, "ok");
    log("ok", "snap", `saved "${res.name}"`);
    await refreshSnapshotList();
    // Pre-select the just-saved entry.
    $("snapshotPicker").value = res.name;
  } catch (e) {
    setStatus("snapshotStatus", "Save failed", "err");
    showError("Save failed: " + e.message);
  }
}

async function loadSnapshot() {
  clearError();
  const name = $("snapshotPicker").value;
  if (!name) { showError("Pick a snapshot to load."); return; }
  setStatus("snapshotStatus", "Loading…", "busy");
  try {
    const snap = await apiGet(`/api/snapshot/load/${encodeURIComponent(name)}`);
    hydrateFromSnapshot(snap);
    setStatus("snapshotStatus", `Loaded ${name}`, "ok");
    log("ok", "snap", `loaded "${name}"`);
  } catch (e) {
    setStatus("snapshotStatus", "Load failed", "err");
    showError("Load failed: " + e.message);
  }
}

async function deleteSnapshot() {
  clearError();
  const name = $("snapshotPicker").value;
  if (!name) { showError("Pick a snapshot to delete."); return; }
  if (!confirm(`Delete snapshot "${name}"?`)) return;
  setStatus("snapshotStatus", "Deleting…", "busy");
  try {
    await apiDelete(`/api/snapshot/${encodeURIComponent(name)}`);
    setStatus("snapshotStatus", `Deleted ${name}`, "ok");
    log("ok", "snap", `deleted "${name}"`);
    await refreshSnapshotList();
  } catch (e) {
    setStatus("snapshotStatus", "Delete failed", "err");
    showError("Delete failed: " + e.message);
  }
}

// Restore the on-screen state from a snapshot: market surface tab + calibration tabs.
// This deliberately mirrors the post-load / post-calibrate render paths so the UI looks
// identical to a freshly-loaded-and-calibrated session.
function hydrateFromSnapshot(snap) {
  if (!snap?.surface) throw new Error("Snapshot missing surface");
  const s = snap.surface;
  // Reconstruct the SurfaceResponse-shaped object the renderers expect.
  state.surface = {
    spot: s.spot,
    ticker: s.ticker,
    expiries: s.expiries,
    strikes: s.strikes,
    iv: s.iv,
    callPrice: s.callPrice,
    putPrice: s.putPrice,
    source: s.source,
    riskFreeRate: s.riskFreeRate,
    dividendYield: s.dividendYield,
    cleanStats: s.cleanStats || null,
  };
  setStatus("surfaceStatus",
    `Loaded snapshot · ${s.source} surface (spot=${fmt(s.spot, 2)}, ${s.expiries.length}×${s.strikes.length})`,
    "ok");
  refreshMarketSourcePill?.();
  refreshMarketCutControls?.();
  $("calibPanel").classList.remove("hidden");
  $("tabsSection").classList.remove("hidden");
  renderMarketSurface(getSelectedMarketQty());
  renderMarketCut();
  // Invalidate caches that depend on the calibration before we render Heston tabs.
  state.smiles = { denseStrikes: null, denseIv: null };
  state.smileDetail = { cache: new Map(), rendered: false, lastKey: null };

  if (snap.calibration) {
    // Project a CalibrationResult-shaped object out of the snapshot.
    const c = snap.calibration;
    state.calibration = {
      hestonParams: c.params,
      finalRmse: c.finalRmse,
      converged: c.converged,
      totalIterations: c.totalIterations,
      elapsedMs: c.elapsedMs,
      expiries: c.expiries,
      strikes: c.strikes,
      marketIv: c.marketIv,
      hestonIv: c.hestonIv,
      stages: c.stages || [],
      history: c.history || [],
    };
    const result = state.calibration;
    applyCalibRangeToGreekInputs();
    plotConvergenceFromHistory(result.history);
    renderHestonSurface(result);
    renderComparison(result);
    renderResults(result);
    renderSmiles();
    setStatus("runStatus",
      `Loaded: RMSE=${fmt(result.finalRmse, 6)} · iter=${result.totalIterations}`,
      result.converged === false ? "warn" : "ok");
    switchTab("results");
  } else {
    state.calibration = null;
    renderSmiles();
    switchTab("market");
  }
}

// ───────────────── Swaption surface ─────────────────
state.swaptionSurface = null;

function buildSwaptionRequest() {
  return {
    optionExpiries:   parseCsvNumbers($("swaptionExpiries").value),
    swapTenors:       parseCsvNumbers($("swaptionTenors").value),
    strikeOffsetsBps: parseCsvNumbers($("swaptionOffsets").value),
    asOf:             $("swaptionAsOf").value.trim() || null,
    forceSynthetic:   $("swaptionForceSynthetic").checked,
    couponFrequency:  2,
  };
}

function swaptionAtmVol(pt) {
  const fwd = pt.forwardSwapRate;
  let bestIdx = 0, bestDist = Infinity;
  for (let i = 0; i < pt.strikes.length; i++) {
    const d = Math.abs(pt.strikes[i] - fwd);
    if (d < bestDist) { bestDist = d; bestIdx = i; }
  }
  return pt.marketVols[bestIdx] * 10000; // bps
}

function renderSwaptionHeatmap(data) {
  const pts = data.volSurface;
  const expiries = [...new Set(pts.map(p => p.optionExpiry))].sort((a, b) => a - b);
  const tenors   = [...new Set(pts.map(p => p.swapTenor))].sort((a, b) => a - b);

  const z = expiries.map(t =>
    tenors.map(s => {
      const pt = pts.find(p => Math.abs(p.optionExpiry - t) < 1e-9 && Math.abs(p.swapTenor - s) < 1e-9);
      return pt ? swaptionAtmVol(pt) : null;
    })
  );

  const layout = {
    title: { text: `ATM Normal Vol (bps) — ${data.asOf}`, font: { size: 13 } },
    margin: { t: 44, l: 80, r: 80, b: 60 },
    paper_bgcolor: "#1e2630",
    plot_bgcolor: "#1e2630",
    font: { color: "#e7ecf2" },
    xaxis: { title: "Swap tenor (yr)", tickvals: tenors, ticktext: tenors.map(t => `${t}Y`), gridcolor: "#2a3340" },
    yaxis: { title: "Option expiry (yr)", tickvals: expiries, ticktext: expiries.map(t => `${t}Y`), gridcolor: "#2a3340" },
  };
  Plotly.react("plot-swaption-heatmap", [{
    type: "heatmap",
    x: tenors,
    y: expiries,
    z,
    colorscale: "Viridis",
    colorbar: { title: "ATM σ_N (bps)", thickness: 14 },
    hovertemplate: "Expiry %{y}Y × Tenor %{x}Y<br>ATM vol: %{z:.1f} bps<extra></extra>",
    connectgaps: false,
  }], layout, { responsive: true, displaylogo: false });
}

// (swaption slice helpers removed — cube approach replaces per-slice UI)

// Populate the source dropdown: first entry is always FRED, then all DB surfaces.
async function populateSwaptionSourceDropdown() {
  const sel = $("swaptionSourceSel");
  const currentVal = sel.value;
  sel.innerHTML = '<option value="fred">— Source from FRED —</option>';
  try {
    const rows = await apiGet("/api/db/swaption-surfaces");
    rows.forEach(r => {
      const ts = new Date(r.createdAt).toISOString().slice(0, 10);
      const opt = document.createElement("option");
      opt.value = `db:${r.id}`;
      opt.textContent = `[DB ${r.id}] ${r.asOf} · ${r.source} (${r.nExpiries}×${r.nTenors} · ${r.nCells} cells · saved ${ts})`;
      sel.appendChild(opt);
    });
    // Restore selection if it still exists.
    if ([...sel.options].some(o => o.value === currentVal)) sel.value = currentVal;
  } catch { /* DB not yet populated */ }
  // Show/hide FRED config based on current selection.
  updateSwaptionFredConfigVisibility();
}

function updateSwaptionFredConfigVisibility() {
  const isFred = $("swaptionSourceSel").value === "fred";
  $("swaptionFredConfig").classList.toggle("hidden", !isFred);
}

// Dispatcher: routes to FRED load or DB load based on the dropdown.
async function loadSwaptionFromSource() {
  const src = $("swaptionSourceSel").value;
  if (src === "fred") {
    await loadSwaptionSurface();
  } else {
    const id = parseInt(src.replace("db:", ""), 10);
    await applySwaptionFromDb(id);
  }
}

async function loadSwaptionSurface() {
  clearError();
  log("info", "swaption", "Loading surface from FRED");
  setStatus("swaptionStatus", "Loading…", "busy");
  $("loadSwaptionBtn").disabled = true;
  try {
    const req = buildSwaptionRequest();
    if (!req.optionExpiries.length || !req.swapTenors.length) {
      showError("Enter at least one option expiry and one swap tenor.");
      return;
    }
    const data = await apiJson("/api/swaption/surface", req);
    applySwaptionSurfaceData(data, null);
  } catch (e) {
    setStatus("swaptionStatus", "Failed", "err");
    showError("Swaption surface load failed: " + e.message);
    log("err", "swaption", e.message);
  } finally {
    $("loadSwaptionBtn").disabled = false;
  }
}

// Load a surface from DB and restore its stored SABR calibrations if available.
async function applySwaptionFromDb(id) {
  clearError();
  setStatus("swaptionStatus", `Loading DB id=${id}…`, "busy");
  $("loadSwaptionBtn").disabled = true;
  try {
    const [data, calibRows] = await Promise.all([
      apiGet(`/api/db/swaption-surface/${id}`),
      apiGet(`/api/db/sabr-calibrations/${id}`).catch(() => []),
    ]);
    applySwaptionSurfaceData(data, calibRows.length ? calibRows : null);
    setStatus("dbSwaptionStatus", `Loaded id=${id}`, "ok");
    log("ok", "db", `Surface id=${id} loaded (${calibRows.length} SABR calibrations)`);
  } catch (e) {
    setStatus("swaptionStatus", "Failed", "err");
    showError("Failed to load surface from DB: " + e.message);
  } finally {
    $("loadSwaptionBtn").disabled = false;
  }
}

// Common surface application: updates state, renders UI, triggers SABR panel.
// calibRows: array of SabrCalibrationRow from DB, or null to trigger fresh calibration.
async function applySwaptionSurfaceData(data, calibRows) {
  state.swaptionSurface = data;

  const pill = $("swaptionSourcePill");
  pill.textContent = `${data.asOf} · ${data.source}`;
  pill.className = `status-pill ${(data.source || "").includes("FRED") ? "ok" : "busy"}`;

  $("swaptionInfo").classList.remove("hidden");
  renderSwaptionHeatmap(data);

  const pts = data.volSurface;
  const expiries = [...new Set(pts.map(p => p.optionExpiry))].sort((a, b) => a - b);
  const tenors   = [...new Set(pts.map(p => p.swapTenor))].sort((a, b) => a - b);
  const nStrikes = pts.length > 0 ? pts[0].strikes.length : 0;

  const infoPill = $("sabrSurfaceInfoPill");
  infoPill.textContent =
    `${expiries.length} expiries (${expiries.map(e => `${e}Y`).join(", ")})  ·  ` +
    `${tenors.length} tenors (${tenors.map(t => `${t}Y`).join(", ")})  ·  ` +
    `${nStrikes} strikes per slice`;
  $("sabrSurfaceInfo").classList.remove("hidden");

  const cellCount = data.volSurface.length;
  setStatus("swaptionStatus", `Loaded ${cellCount} cells`, "ok");
  log("ok", "swaption", `${data.asOf} · ${cellCount} cells · ${data.source}`);

  if (calibRows?.length) {
    const convention = calibRows[0]?.convention ?? "Normal";
    $("sabrCalibConvention").value = convention;
    // Reconstruct cube from stored rows
    const cube = {};
    for (const r of calibRows) {
      const t = r.swapTenor;
      if (!cube[t]) cube[t] = [];
      cube[t].push({
        expiry: r.optionExpiry, forward: r.forward,
        params: { alpha: r.alpha, beta: r.beta, rho: r.rho, nu: r.nu },
        shift: r.shift, converged: r.converged, rmse: r.finalRmse,
        strikes: null, marketVols: null, modelVols: null,
      });
    }
    // Sort slices within each tenor by expiry
    for (const t of Object.keys(cube)) cube[t].sort((a, b) => a.expiry - b.expiry);

    // Fetch model vols for each slice so smile plots can show fit overlay
    const fetchJobs = [];
    for (const [tenorStr, slices] of Object.entries(cube)) {
      const tenor = parseFloat(tenorStr);
      for (const sl of slices) {
        const pt = data.volSurface.find(p =>
          Math.abs(p.optionExpiry - sl.expiry) < 1e-9 && Math.abs(p.swapTenor - tenor) < 1e-9
        );
        if (!pt) continue;
        sl.strikes    = pt.strikes;
        sl.marketVols = pt.marketVols;
        fetchJobs.push(
          apiJson("/api/sabr/vol", {
            params: sl.params, forward: sl.forward, expiry: sl.expiry,
            strikes: pt.strikes, shift: sl.shift, convention,
          }).then(r => { sl.modelVols = r.vols; }).catch(() => {})
        );
      }
    }
    await Promise.all(fetchJobs);

    state.sabrCalibCube = cube;
    renderSabrCalibCube(cube, convention, data);
    populateSabrSmilePlotsTenors(data, cube, convention);
    renderSabrSmilePlotsGrid(data, cube, convention, tenors[0]);
    populateSabrInterpTenors(tenors);

    const nFail = Object.values(cube).flat().filter(s => !s.converged).length;
    const nTotal = Object.values(cube).flat().length;
    setStatus("sabrCalibStatus", `${nTotal} slices (from DB)` + (nFail ? ` · ⚠ ${nFail}` : ""), nFail ? "warn" : "ok");
    $("sabrSaveDbBtn").disabled = false;
    $("sabrSmilePlotsPanel").classList.remove("hidden");
    $("sabrInterpPanel").classList.remove("hidden");
  } else {
    state.sabrCalibCube = null;
    $("sabrCalibCubeWrap").classList.add("hidden");
    calibrateSabrSurface();
  }
}

// ───────────────── SABR calibration cube ─────────────────
function parseCsvNumbers(str) {
  return str.split(",").map(s => parseFloat(s.trim())).filter(v => Number.isFinite(v));
}

state.sabrCalibCube = null;
// { [tenor]: [{expiry, forward, params, shift, converged, rmse, strikes, marketVols, modelVols}] }

async function calibrateSabrSurface() {
  if (!state.swaptionSurface) { showError("Load the swaption surface first."); return; }
  const data = state.swaptionSurface;
  const tenors = [...new Set(data.volSurface.map(p => p.swapTenor))].sort((a, b) => a - b);
  const expiries = [...new Set(data.volSurface.map(p => p.optionExpiry))].sort((a, b) => a - b);
  const beta       = parseFloat($("sabrCalibBeta").value);
  const fixBeta    = $("sabrCalibFixBeta").checked;
  const convention = $("sabrCalibConvention").value;

  clearError();
  setStatus("sabrCalibStatus", `Calibrating ${tenors.length}T × ${expiries.length}E…`, "busy");
  $("sabrCalibBtn").disabled = true;

  const cube = {};
  let totalFail = 0;
  let totalSlices = 0;

  for (const tenor of tenors) {
    const slices = data.volSurface
      .filter(p => Math.abs(p.swapTenor - tenor) < 1e-9)
      .sort((a, b) => a.optionExpiry - b.optionExpiry);
    if (slices.length < 2) continue;

    try {
      const req = {
        slices: slices.map(p => ({
          forward: p.forwardSwapRate, expiry: p.optionExpiry,
          strikes: p.strikes, marketVols: p.marketVols,
        })),
        beta, fixBeta, shift: 0, convention,
      };
      const res = await apiJson("/api/sabr/calibrate-surface", req);
      cube[tenor] = res.slices.map((s, i) => ({
        expiry:    slices[i].optionExpiry,
        forward:   slices[i].forwardSwapRate,
        params:    s.params,
        shift:     0,
        converged: s.converged,
        rmse:      s.finalRmse,
        strikes:   slices[i].strikes,
        marketVols: slices[i].marketVols,
        modelVols: s.modelVols,
      }));
      totalFail += res.slices.filter(s => !s.converged).length;
      totalSlices += res.slices.length;
    } catch (e) {
      log("err", "sabr", `Tenor ${tenor}Y calibration failed: ${e.message}`);
    }
  }

  state.sabrCalibCube = cube;
  renderSabrCalibCube(cube, convention, data);
  populateSabrSmilePlotsTenors(data, cube, convention);
  renderSabrSmilePlotsGrid(data, cube, convention, tenors[0]);
  populateSabrInterpTenors(tenors);

  const msg = `${totalSlices} slices calibrated` + (totalFail ? ` · ⚠ ${totalFail}` : "");
  setStatus("sabrCalibStatus", msg, totalFail ? "warn" : "ok");
  log("ok", "sabr-surface", msg);

  $("sabrCalibBtn").disabled = false;
  $("sabrSaveDbBtn").disabled = false;
  $("sabrSmilePlotsPanel").classList.remove("hidden");
  $("sabrInterpPanel").classList.remove("hidden");

  // Pre-fill interp inputs from first tenor's data
  const firstTenorSlices = cube[tenors[0]] || [];
  if (firstTenorSlices.length) {
    const allK = firstTenorSlices.flatMap(s => s.strikes || []);
    if (allK.length) {
      $("sabrInterpStrikeMin").value = (Math.min(...allK) * 100).toFixed(2);
      $("sabrInterpStrikeMax").value = (Math.max(...allK) * 100).toFixed(2);
    }
    const expList = firstTenorSlices.map(s => s.expiry);
    $("sabrInterpExpiry").value = ((expList[0] + expList[expList.length - 1]) / 2).toFixed(2);
  }
}

function renderSabrCalibCube(cube, convention, data) {
  const pts = data.volSurface;
  const expiries = [...new Set(pts.map(p => p.optionExpiry))].sort((a, b) => a - b);
  const tenors   = [...new Set(pts.map(p => p.swapTenor))].sort((a, b) => a - b);
  const isNormal = convention.toLowerCase() === "normal";

  const parts = ['<table class="calib-cube-table"><thead><tr><th>Expiry \\ Tenor</th>'];
  tenors.forEach(t => parts.push(`<th>${t}Y</th>`));
  parts.push("</tr></thead><tbody>");

  expiries.forEach(expiry => {
    parts.push(`<tr><td class="cube-expiry">${expiry}Y</td>`);
    tenors.forEach(tenor => {
      const slices = cube[tenor] || [];
      const s = slices.find(c => Math.abs(c.expiry - expiry) < 1e-9);
      if (!s || !Number.isFinite(s.rmse)) {
        parts.push('<td class="cube-empty">—</td>');
      } else {
        const a = isNormal ? (s.params.alpha * 10000).toFixed(1) + "bp" : fmt(s.params.alpha, 4);
        const rmse = isNormal ? (s.rmse * 10000).toFixed(2) + "bp" : fmt(s.rmse, 4);
        const convCls = s.converged ? "conv" : "noconv";
        const encoded = encodeURIComponent(JSON.stringify({
          expiry, tenor, params: s.params, shift: s.shift,
          forward: s.forward, converged: s.converged, rmse: s.rmse, convention,
        }));
        parts.push(`<td class="cube-cell ${convCls}" data-params="${encoded}" onclick="openSabrModal(this)">` +
          `α=${a}, ρ=${fmt(s.params.rho,2)}, ν=${fmt(s.params.nu,2)}, ${rmse}` +
          ` <span class="conv-flag">${s.converged ? "✓" : "⚠"}</span></td>`);
      }
    });
    parts.push("</tr>");
  });

  parts.push("</tbody></table>");
  const wrap = $("sabrCalibCubeWrap");
  wrap.innerHTML = parts.join("");
  wrap.classList.remove("hidden");
}

function openSabrModal(cell) {
  const raw = cell.dataset.params;
  if (!raw) return;
  const d = JSON.parse(decodeURIComponent(raw));
  const isNormal = (d.convention || "").toLowerCase() === "normal";
  const sc = isNormal ? 10000 : 1;
  const u  = isNormal ? " bps" : "";
  $("sabrModalTitle").textContent = `SABR — ${d.expiry}Y expiry × ${d.tenor}Y tenor`;
  const rows = [
    ["α (alpha)", fmt(d.params.alpha * sc, isNormal ? 2 : 6) + u],
    ["β (beta)",  fmt(d.params.beta, 4)],
    ["ρ (rho)",   fmt(d.params.rho, 4)],
    ["ν (nu)",    fmt(d.params.nu, 4)],
    ["Shift",     fmt(d.shift, 4)],
    ["RMSE",      fmt(d.rmse * sc, isNormal ? 3 : 6) + u],
    ["Forward",   fmt(d.forward * 100, 3) + "%"],
    ["Converged", d.converged ? "Yes" : "No ⚠"],
  ];
  $("sabrModalTableBody").innerHTML =
    rows.map(([k, v]) => `<tr><td>${k}</td><td class="value">${v}</td></tr>`).join("");
  $("sabrParamsModal").classList.remove("hidden");
}

function closeSabrModal() {
  $("sabrParamsModal").classList.add("hidden");
}

// ───────────────── Smile Plots ─────────────────

function populateSabrSmilePlotsTenors(data, cube, convention) {
  const tenors = [...new Set(data.volSurface.map(p => p.swapTenor))].sort((a, b) => a - b);
  const sel = $("smilePlotsTenorSel");
  const prev = sel.value;
  sel.innerHTML = "";
  tenors.forEach(t => {
    const o = document.createElement("option");
    o.value = String(t); o.textContent = `${t}Y`;
    sel.appendChild(o);
  });
  if ([...sel.options].some(o => o.value === prev)) sel.value = prev;
}

function renderSabrSmilePlotsGrid(data, cube, convention, tenor) {
  const grid = $("sabrSmilePlotsGrid");
  grid.innerHTML = "";
  const isNormal = convention.toLowerCase() === "normal";
  const scale    = isNormal ? 10000 : 1;
  const yLabel   = isNormal ? "Normal vol (bps)" : "Lognormal vol";

  const slices = (cube[tenor] || []).sort((a, b) => a.expiry - b.expiry);
  if (!slices.length) {
    grid.innerHTML = `<p style="color:var(--text-dim);padding:8px">No calibrated slices for tenor ${tenor}Y.</p>`;
    return;
  }

  slices.forEach((sl, idx) => {
    const cellId = `sabr-sp-${String(sl.expiry).replace(".", "_")}-${String(tenor).replace(".", "_")}`;
    const div = document.createElement("div");
    div.className = "smile-tile";
    div.id = cellId;
    grid.appendChild(div);

    const mktX = (sl.strikes || []).map(k => k * 100);
    const mktY = (sl.marketVols || []).map(v => v * scale);
    const traces = [{
      x: mktX, y: mktY, mode: "markers", type: "scatter", name: "Market",
      marker: { color: COLORS.market, size: 6 }, showlegend: idx === 0,
    }];
    if (sl.modelVols?.length) {
      traces.push({
        x: mktX, y: sl.modelVols.map(v => v * scale),
        mode: "lines", type: "scatter", name: "SABR",
        line: { color: COLORS.heston, width: 2 }, showlegend: idx === 0,
      });
    }

    const rmseStr = Number.isFinite(sl.rmse)
      ? (isNormal ? (sl.rmse * 10000).toFixed(2) + "bp" : sl.rmse.toFixed(4))
      : "—";
    const layout = {
      title: { text: `${sl.expiry}Y × ${tenor}Y  F=${fmt(sl.forward*100,3)}%  RMSE=${rmseStr}`, font: { size: 10 } },
      margin: { t: 36, l: 44, r: 8, b: 36 },
      paper_bgcolor: "#1a2029", plot_bgcolor: "#1a2029",
      font: { color: "#e7ecf2", size: 9 },
      xaxis: { title: "Strike (%)", gridcolor: "#2a3340", tickfont: { size: 8 } },
      yaxis: { title: yLabel,       gridcolor: "#2a3340", tickfont: { size: 8 } },
      showlegend: false,
    };
    Plotly.react(cellId, traces, layout, { responsive: true, displaylogo: false, displayModeBar: false });
  });

  const nFail = slices.filter(s => !s.converged).length;
  setStatus("sabrSmilesInfo",
    `${slices.length} slices for ${tenor}Y` + (nFail ? ` · ⚠ ${nFail} not converged` : ""),
    nFail ? "warn" : "ok");
}

// ───────────────── Smile Interpolation ─────────────────

function populateSabrInterpTenors(tenors) {
  const sels = [$("sabrInterpTenorSel")];
  sels.forEach(sel => {
    if (!sel) return;
    const prev = sel.value;
    sel.innerHTML = "";
    tenors.forEach(t => {
      const o = document.createElement("option");
      o.value = String(t); o.textContent = `${t}Y`;
      sel.appendChild(o);
    });
    if ([...sel.options].some(o => o.value === prev)) sel.value = prev;
  });
}

async function computeSabrInterpolatedSmile() {
  const cube = state.sabrCalibCube;
  if (!cube) { showError("Calibrate SABR surface first."); return; }

  const tenor       = parseFloat($("sabrInterpTenorSel").value);
  const targetExpiry = parseFloat($("sabrInterpExpiry").value);
  const strikeMinPct = parseFloat($("sabrInterpStrikeMin").value);
  const strikeMaxPct = parseFloat($("sabrInterpStrikeMax").value);
  const nPoints      = parseInt($("sabrInterpPoints").value, 10);
  const convention   = $("sabrCalibConvention").value;

  const slices = cube[tenor];
  if (!slices || slices.length < 2) {
    showError(`Need at least 2 calibrated expiries for tenor ${tenor}Y.`); return;
  }
  if (!Number.isFinite(targetExpiry) || targetExpiry <= 0) { showError("Enter a valid target expiry."); return; }
  if (strikeMinPct >= strikeMaxPct) { showError("Strike min must be less than strike max."); return; }

  clearError();
  setStatus("sabrInterpStatus", "Computing…", "busy");
  $("sabrInterpBtn").disabled = true;
  try {
    const req = {
      slices: slices.map(c => ({ expiry: c.expiry, forward: c.forward, params: c.params, shift: c.shift })),
      targetExpiry,
      strikeMin: strikeMinPct / 100,
      strikeMax: strikeMaxPct / 100,
      nPoints,
      convention,
    };
    const res = await apiJson("/api/sabr/interpolate-smile", req);
    const isNormal = convention.toLowerCase() === "normal";
    const sc = isNormal ? 10000 : 1;
    const yLabel = isNormal ? "Normal Vol (bps)" : "Lognormal Vol";
    const calibExpiries = slices.map(c => c.expiry);
    const extrapolating = targetExpiry < Math.min(...calibExpiries) || targetExpiry > Math.max(...calibExpiries);
    const trace = {
      x: res.strikes.map(k => k * 100),
      y: res.vols.map(v => v * sc),
      mode: "lines", type: "scatter",
      name: `T=${targetExpiry}y${extrapolating ? " (extrap.)" : ""}`,
      line: { color: extrapolating ? "#f5a623" : COLORS.heston, width: 2, dash: extrapolating ? "dash" : "solid" },
    };
    const layout = {
      title: { text: `SABR smile — tenor ${tenor}Y, T=${targetExpiry}y (variance interpolated)`, font: { size: 12 } },
      margin: { t: 50, l: 60, r: 20, b: 50 },
      paper_bgcolor: "#1e2630", plot_bgcolor: "#1e2630",
      font: { color: "#e7ecf2" },
      xaxis: { title: "Strike (%)", gridcolor: "#2a3340" },
      yaxis: { title: yLabel,       gridcolor: "#2a3340" },
      showlegend: true, legend: { x: 1, xanchor: "right", y: 1 },
      annotations: [{
        x: 0.01, y: 0.98, xref: "paper", yref: "paper", xanchor: "left", yanchor: "top",
        text: `Calibrated: ${calibExpiries.map(e => `${e}Y`).join(", ")}` +
              (extrapolating ? "<br>⚠ outside calibrated range" : ""),
        showarrow: false, font: { size: 10, color: "#8a9bb0" }, bgcolor: "rgba(30,38,48,0.7)",
      }],
    };
    Plotly.react("plot-sabr-interp", [trace], layout, { responsive: true, displaylogo: false });
    const atmVol = res.vols[Math.round(nPoints / 2)] * sc;
    const msg = `ATM ≈${fmt(atmVol, 2)}${isNormal ? " bps" : ""} · F=${fmt(res.targetForward * 100, 3)}%`;
    setStatus("sabrInterpStatus", msg, "ok");
    log("ok", "sabr", `interpolated: T=${targetExpiry}y tenor=${tenor}Y ${msg}`);
    // Pre-fill annuity from surface data so Greeks are ready to compute.
    const autoAnnuity = getInterpolatedAnnuity(tenor, targetExpiry);
    const annuityInp = $("sabrGreeksAnnuity");
    if (annuityInp) annuityInp.value = autoAnnuity.toFixed(4);
  } catch (e) {
    setStatus("sabrInterpStatus", "Failed", "err");
    showError("Smile interpolation failed: " + e.message);
  } finally {
    $("sabrInterpBtn").disabled = false;
  }
}

// ───────────────── SABR Greeks ─────────────────

// Linearly interpolate annuity from surface pillar data for (tenor, targetExpiry).
function getInterpolatedAnnuity(tenor, targetExpiry) {
  const data = state.swaptionSurface;
  if (!data) return 1.0;
  const pts = data.volSurface
    .filter(p => Math.abs(p.swapTenor - tenor) < 1e-9)
    .sort((a, b) => a.optionExpiry - b.optionExpiry);
  if (!pts.length) return 1.0;
  if (targetExpiry <= pts[0].optionExpiry) return pts[0].annuity;
  if (targetExpiry >= pts[pts.length - 1].optionExpiry) return pts[pts.length - 1].annuity;
  for (let i = 0; i < pts.length - 1; i++) {
    if (targetExpiry >= pts[i].optionExpiry && targetExpiry <= pts[i + 1].optionExpiry) {
      const t0 = pts[i].optionExpiry, t1 = pts[i + 1].optionExpiry;
      const w = (targetExpiry - t0) / (t1 - t0);
      return pts[i].annuity * (1 - w) + pts[i + 1].annuity * w;
    }
  }
  return pts[pts.length - 1].annuity;
}

async function computeSabrGreeks() {
  const cube = state.sabrCalibCube;
  if (!cube) { showError("Calibrate SABR surface first."); return; }

  const tenor        = parseFloat($("sabrInterpTenorSel").value);
  const targetExpiry = parseFloat($("sabrInterpExpiry").value);
  const strikeMinPct = parseFloat($("sabrInterpStrikeMin").value);
  const strikeMaxPct = parseFloat($("sabrInterpStrikeMax").value);
  const nPoints      = parseInt($("sabrInterpPoints").value, 10);
  const convention   = $("sabrCalibConvention").value;
  const isPayer      = $("sabrGreeksIsPayer").checked;

  // Auto-fill annuity from surface data if not yet set by user.
  let annuity = parseFloat($("sabrGreeksAnnuity").value);
  if (!Number.isFinite(annuity) || annuity <= 0) {
    annuity = getInterpolatedAnnuity(tenor, targetExpiry);
    $("sabrGreeksAnnuity").value = annuity.toFixed(4);
  }

  const slices = cube[tenor];
  if (!slices || slices.length < 2) { showError(`Need at least 2 calibrated expiries for tenor ${tenor}Y.`); return; }
  if (!Number.isFinite(targetExpiry) || targetExpiry <= 0) { showError("Enter a valid target expiry."); return; }
  if (strikeMinPct >= strikeMaxPct) { showError("Strike min must be less than strike max."); return; }

  clearError();
  setStatus("sabrGreeksStatus", "Computing…", "busy");
  $("sabrGreeksBtn").disabled = true;

  try {
    const req = {
      slices: slices.map(c => ({ expiry: c.expiry, forward: c.forward, params: c.params, shift: c.shift })),
      targetExpiry,
      strikeMin: strikeMinPct / 100,
      strikeMax: strikeMaxPct / 100,
      nPoints,
      convention,
      annuity,
      isPayer,
    };
    const res = await apiJson("/api/sabr/greeks", req);
    renderSabrGreeksTable(res, convention);
    setStatus("sabrGreeksStatus",
      `${res.greeks.length} strikes · F=${fmt(res.targetForward * 100, 3)}% · annuity=${fmt(annuity, 4)}`, "ok");
    log("ok", "sabr-greeks", `T=${targetExpiry}y tenor=${tenor}Y isPayer=${isPayer} annuity=${fmt(annuity,4)}`);
  } catch (e) {
    setStatus("sabrGreeksStatus", "Failed", "err");
    showError("Greeks computation failed: " + e.message);
  } finally {
    $("sabrGreeksBtn").disabled = false;
  }
}

function renderSabrGreeksTable(res, convention) {
  const wrap = $("sabrGreeksTableWrap");
  if (!wrap) return;
  const isNormal = convention.toLowerCase() === "normal";
  const volScale = isNormal ? 10000 : 1;

  const parts = [
    '<div style="overflow-x:auto;margin-top:8px">',
    '<table class="greeks-table" style="min-width:680px;font-size:0.82em">',
    '<thead><tr>',
    '<th>Strike (%)</th>',
    `<th>Vol${isNormal ? " (bps)" : ""}</th>`,
    '<th>Price</th>',
    '<th>Delta</th>',
    '<th>Gamma</th>',
    '<th>Vega</th>',
    '<th>Vanna</th>',
    '<th>Volga</th>',
    '</tr></thead><tbody>',
  ];
  for (const row of res.greeks) {
    parts.push(
      `<tr>` +
      `<td class="value">${(row.strike * 100).toFixed(3)}</td>` +
      `<td class="value">${fmtGreek(row.vol * volScale)}</td>` +
      `<td class="value">${fmtGreek(row.price)}</td>` +
      `<td class="value">${fmtGreek(row.delta)}</td>` +
      `<td class="value">${fmtGreek(row.gamma)}</td>` +
      `<td class="value">${fmtGreek(row.vega)}</td>` +
      `<td class="value">${fmtGreek(row.vanna)}</td>` +
      `<td class="value">${fmtGreek(row.volga)}</td>` +
      `</tr>`
    );
  }
  parts.push('</tbody></table></div>');
  wrap.innerHTML = parts.join("");
}

// ───────────────── Database panel ─────────────────

async function dbSaveSwaptionSurface() {
  if (!state.swaptionSurface) { showError("Load a swaption surface first."); return; }
  setStatus("dbSwaptionStatus", "Saving…", "busy");
  try {
    const r = await apiJson("/api/db/swaption-surface/save", state.swaptionSurface);
    const surfaceId = r.id;
    setStatus("dbSwaptionStatus", `Saved (id=${surfaceId})`, "ok");
    log("ok", "db", `Swaption surface saved, id=${surfaceId}`);

    // Save all tenors from the calibration cube.
    const cube = state.sabrCalibCube;
    if (cube && Object.keys(cube).length) {
      const conv = $("sabrCalibConvention").value;
      const entries = [];
      for (const [tenorStr, slices] of Object.entries(cube)) {
        const tenor = parseFloat(tenorStr);
        for (const c of slices) {
          entries.push({
            optionExpiry: c.expiry, swapTenor: tenor,
            forward: c.forward, alpha: c.params.alpha, beta: c.params.beta,
            rho: c.params.rho, nu: c.params.nu, shift: c.shift,
            finalRmse: c.rmse, converged: c.converged, convention: conv,
          });
        }
      }
      if (entries.length) {
        await apiJson(`/api/db/sabr-calibrations/${surfaceId}`, entries);
        log("ok", "db", `${entries.length} SABR calibrations saved for surface id=${surfaceId}`);
      }
    }
    await dbRefreshSwaptionTable();
    await populateSwaptionSourceDropdown();
  } catch (e) {
    setStatus("dbSwaptionStatus", "Failed", "err");
    showError("DB save failed: " + e.message);
  }
}

async function dbRefreshSwaptionTable() {
  try {
    const rows = await apiGet("/api/db/swaption-surfaces");
    const tbody = $("dbSwaptionTableBody");
    tbody.innerHTML = "";
    if (!rows.length) {
      tbody.innerHTML = '<tr><td colspan="8" style="color:var(--text-dim)">No surfaces stored yet.</td></tr>';
      return;
    }
    rows.forEach(r => {
      const ts = new Date(r.createdAt).toISOString().slice(0, 16).replace("T", " ");
      const tr = document.createElement("tr");
      tr.innerHTML = `
        <td class="value">${r.id}</td>
        <td class="value">${r.asOf}</td>
        <td class="value" style="max-width:120px;overflow:hidden;text-overflow:ellipsis">${r.source}</td>
        <td class="value">${r.nExpiries}</td>
        <td class="value">${r.nTenors}</td>
        <td class="value">${r.nCells}</td>
        <td class="value">${ts}</td>
        <td><button class="ghost" style="font-size:0.8em;padding:2px 8px"
            onclick="applySwaptionFromDb(${r.id})">Load</button></td>`;
      tbody.appendChild(tr);
    });
  } catch (e) {
    log("err", "db", "Failed to refresh swaption list: " + e.message);
  }
}


async function dbSaveHestonSurface() {
  if (!state.surface) { showError("Load a Heston surface first."); return; }
  setStatus("dbHestonStatus", "Saving…", "busy");
  try {
    const r = await apiJson("/api/db/heston-surface/save", state.surface);
    const surfaceId = r.id;
    setStatus("dbHestonStatus", `Saved (id=${surfaceId})`, "ok");
    log("ok", "db", `Heston surface saved, id=${surfaceId}`);

    // Save calibration result if available.
    if (state.calibration?.params) {
      const c = state.calibration.params;
      await apiJson(`/api/db/heston-calibration/${surfaceId}`, {
        ticker: state.surface.ticker,
        asOf: new Date().toISOString().slice(0, 10),
        kappa: c.kappa, theta: c.theta, sigma: c.sigma,
        rho: c.rho, v0: c.v0,
        finalRmse: state.calibration.finalRmse ?? 0,
      });
      log("ok", "db", `Heston calibration saved for surface id=${surfaceId}`);
    }
    await dbRefreshHestonTables();
  } catch (e) {
    setStatus("dbHestonStatus", "Failed", "err");
    showError("DB save failed: " + e.message);
  }
}

async function dbRefreshHestonTables() {
  try {
    const [surfaces, calibs] = await Promise.all([
      apiGet("/api/db/heston-surfaces"),
      apiGet("/api/db/heston-calibrations"),
    ]);

    const surfBody = $("dbHestonTableBody");
    surfBody.innerHTML = "";
    if (!surfaces.length) {
      surfBody.innerHTML = '<tr><td colspan="8" style="color:var(--text-dim)">No surfaces stored yet.</td></tr>';
    } else {
      surfaces.forEach(r => {
        const ts = new Date(r.createdAt).toISOString().slice(0, 16).replace("T", " ");
        const tr = document.createElement("tr");
        tr.innerHTML = `
          <td class="value">${r.id}</td>
          <td class="value">${r.ticker}</td>
          <td class="value">${fmt(r.spot, 2)}</td>
          <td class="value">${r.nExpiries}</td>
          <td class="value">${r.nStrikes}</td>
          <td class="value" style="max-width:100px;overflow:hidden;text-overflow:ellipsis">${r.source}</td>
          <td class="value">${ts}</td>
          <td><button class="ghost" style="font-size:0.8em;padding:2px 8px"
              onclick="dbLoadHestonSurface(${r.id})">Load</button></td>`;
        surfBody.appendChild(tr);
      });
    }

    const calibBody = $("dbHestonCalibTableBody");
    calibBody.innerHTML = "";
    if (!calibs.length) {
      calibBody.innerHTML = '<tr><td colspan="10" style="color:var(--text-dim)">No calibrations stored yet.</td></tr>';
    } else {
      calibs.forEach(c => {
        const ts = new Date(c.createdAt).toISOString().slice(0, 16).replace("T", " ");
        const tr = document.createElement("tr");
        tr.innerHTML = `
          <td class="value">${c.hestonSurfaceId}</td>
          <td class="value">${c.ticker}</td>
          <td class="value">${c.asOf}</td>
          <td class="value">${fmt(c.kappa, 4)}</td>
          <td class="value">${fmt(c.theta, 4)}</td>
          <td class="value">${fmt(c.sigma, 4)}</td>
          <td class="value">${fmt(c.rho, 4)}</td>
          <td class="value">${fmt(c.v0, 4)}</td>
          <td class="value">${fmt(c.finalRmse * 100, 4)}%</td>
          <td class="value">${ts}</td>`;
        calibBody.appendChild(tr);
      });
    }
  } catch (e) {
    log("err", "db", "Failed to refresh Heston tables: " + e.message);
  }
}

async function dbLoadHestonSurface(id) {
  setStatus("dbHestonStatus", `Loading id=${id}…`, "busy");
  try {
    const data = await apiGet(`/api/db/heston-surface/${id}`);
    state.surface = data;
    renderMarketSurface(getSelectedMarketQty());
    renderMarketCut();
    $("calibPanel").classList.remove("hidden");
    $("tabsSection").classList.remove("hidden");
    switchTab("market");
    setStatus("dbHestonStatus", `Loaded id=${id}`, "ok");
    log("ok", "db", `Heston surface id=${id} loaded from DB`);
  } catch (e) {
    setStatus("dbHestonStatus", "Failed", "err");
    showError("DB load failed: " + e.message);
  }
}

// ───────────────── Wiring ─────────────────
function wireUi() {
  setupLogPanel();
  $("loadSurfaceBtn").addEventListener("click", loadSurface);
  $("runBtn").addEventListener("click", runCalibration);
  $("cancelBtn").addEventListener("click", cancelCalibration);
  $("plotInterpBtn").addEventListener("click", plotInterpolatedHestonSurface);
  $("plotSmileDetailBtn").addEventListener("click", plotSmileDetail);
  $("saveMarketCsvBtn").addEventListener("click", saveMarketCsv);

  for (const btn of $$(".tab-btn")) {
    btn.addEventListener("click", () => switchTab(btn.dataset.tab));
  }

  // Market tab: quantity selector swaps the surface; expiry selector swaps the cut.
  for (const r of $$('input[name="marketQty"]')) {
    r.addEventListener("change", () => {
      if (state.surface) renderMarketSurface(getSelectedMarketQty());
    });
  }
  const cutSel = $("marketCutExpiry");
  if (cutSel) cutSel.addEventListener("change", () => {
    if (state.surface) renderMarketCut();
  });

  // Smile-cut x-axis toggle (Strike / Delta). The radios live in the Market tab but the
  // selection is global — switching also re-renders Smiles tab tiles and the single-expiry
  // detail so the entire smile-view stays internally consistent.
  for (const r of $$('input[name="smileXAxis"]')) {
    r.addEventListener("change", () => {
      if (state.surface) renderMarketCut();
      renderSmiles?.();
      if (state.smileDetail?.rendered) renderSmileDetail?.();
    });
  }

  // Show/hide NM restarts depending on global method.
  const refreshNm = () => {
    const isNm = $("globalMethod").value === "nelderMead";
    $("nmRestartsWrap").classList.toggle("hidden", !isNm);
  };
  $("globalMethod").addEventListener("change", refreshNm);
  refreshNm();

  // Collapse calibration card.
  $("toggleCalibBtn").addEventListener("click", () => {
    const body = $("calibBody");
    const hidden = body.classList.toggle("hidden");
    $("toggleCalibBtn").textContent = hidden ? "Expand" : "Collapse";
  });

  // Greeks tab.
  $("computeGreeksBtn").addEventListener("click", computeGreeksGrid);
  $("cancelGreeksBtn").addEventListener("click", cancelGreeksCompute);
  $("useCalibRangeBtn").addEventListener("click", applyCalibRangeToGreekInputs);
  $("greeksTableShow")?.addEventListener("change", renderGreeksTable);
  $("greeksTableCsvBtn")?.addEventListener("click", downloadGreekCsv);
  for (const r of $$('input[name="greekQty"]')) {
    r.addEventListener("change", () => renderGreeksSurface());
  }

  // Snapshot panel wiring.
  $("saveSnapshotFileBtn").addEventListener("click", saveSnapshotToFile);
  $("loadSnapshotFileBtn").addEventListener("click", loadSnapshotFromFile);
  $("saveSnapshotBtn").addEventListener("click", saveSnapshot);
  $("loadSnapshotBtn").addEventListener("click", loadSnapshot);
  $("deleteSnapshotBtn").addEventListener("click", deleteSnapshot);
  refreshSnapshotList();

  // Swaption surface section.
  $("loadSwaptionBtn").addEventListener("click", loadSwaptionFromSource);
  $("swaptionSourceSel").addEventListener("change", updateSwaptionFredConfigVisibility);
  $("toggleSwaptionBtn").addEventListener("click", () => {
    const body = $("swaptionBody");
    const hidden = body.classList.toggle("hidden");
    $("toggleSwaptionBtn").textContent = hidden ? "Expand" : "Collapse";
  });

  // SABR Calibration section.
  $("sabrCalibBtn").addEventListener("click", calibrateSabrSurface);
  $("sabrSaveDbBtn").addEventListener("click", dbSaveSwaptionSurface);
  $("toggleSabrCalibBtn").addEventListener("click", () => {
    const body = $("sabrCalibBody");
    const hidden = body.classList.toggle("hidden");
    $("toggleSabrCalibBtn").textContent = hidden ? "Expand" : "Collapse";
  });

  // Smile Plots section.
  $("smilePlotsTenorSel").addEventListener("change", () => {
    const cube = state.sabrCalibCube;
    if (!cube || !state.swaptionSurface) return;
    const tenor = parseFloat($("smilePlotsTenorSel").value);
    renderSabrSmilePlotsGrid(state.swaptionSurface, cube, $("sabrCalibConvention").value, tenor);
  });
  $("toggleSabrSmilesBtn").addEventListener("click", () => {
    const body = $("sabrSmilesBody");
    const hidden = body.classList.toggle("hidden");
    $("toggleSabrSmilesBtn").textContent = hidden ? "Expand" : "Collapse";
  });

  // Smile Interpolation section.
  $("sabrInterpBtn").addEventListener("click", computeSabrInterpolatedSmile);
  $("sabrGreeksBtn").addEventListener("click", computeSabrGreeks);
  $("toggleSabrInterpBtn").addEventListener("click", () => {
    const body = $("sabrInterpBody");
    const hidden = body.classList.toggle("hidden");
    $("toggleSabrInterpBtn").textContent = hidden ? "Expand" : "Collapse";
  });

  // Modal close.
  $("sabrModalCloseBtn").addEventListener("click", closeSabrModal);
  $("sabrParamsModal").addEventListener("click", e => { if (e.target === $("sabrParamsModal")) closeSabrModal(); });

  // Database panel.
  $("dbSaveSwaptionBtn").addEventListener("click", dbSaveSwaptionSurface);
  $("dbRefreshSwaptionBtn").addEventListener("click", dbRefreshSwaptionTable);
  $("dbSaveHestonBtn").addEventListener("click", dbSaveHestonSurface);
  $("dbRefreshHestonBtn").addEventListener("click", dbRefreshHestonTables);
  $("toggleDbBtn").addEventListener("click", () => {
    const body = $("dbBody");
    const hidden = body.classList.toggle("hidden");
    $("toggleDbBtn").textContent = hidden ? "Expand" : "Collapse";
  });
  // Load tables and populate source dropdown on startup.
  populateSwaptionSourceDropdown();
  dbRefreshSwaptionTable();
  dbRefreshHestonTables();

  // Resize on window resize for visible plots.
  window.addEventListener("resize", () => {
    for (const el of $$(".plot")) if (el.data) Plotly.Plots.resize(el);
  });
}

wireUi();
