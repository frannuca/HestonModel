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
    log("err", "api", `${path} → ${res.status} ${res.statusText}: ${txt.slice(0, 200)}`);
    throw new Error(`${res.status} ${res.statusText}: ${txt}`);
  }
  const json = await res.json();
  log("ok", "api", `${path} → 200 in ${(performance.now() - t0).toFixed(0)}ms`);
  return json;
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

function renderMarketCut() {
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

  const xs = surf.strikes.slice();
  const cleanRow = (row) => (row || []).map((v) =>
    (v === null || v === undefined || !Number.isFinite(v)) ? null : v
  );
  const ivY   = cleanRow(surf.iv && surf.iv[idx]);
  const callY = cleanRow(surf.callPrice && surf.callPrice[idx]);
  const putY  = cleanRow(surf.putPrice && surf.putPrice[idx]);

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
    {
      x: xs, y: callY,
      mode: "lines+markers",
      type: "scatter",
      name: "Call Price",
      yaxis: "y2",
      connectgaps: false,
      marker: { color: COLORS.call, size: 5 },
      line: { color: COLORS.call, width: 1 },
    },
    {
      x: xs, y: putY,
      mode: "lines+markers",
      type: "scatter",
      name: "Put Price",
      yaxis: "y2",
      connectgaps: false,
      marker: { color: COLORS.put, size: 5 },
      line: { color: COLORS.put, width: 1 },
    },
  ];
  const layout = {
    title: { text: `${surf.ticker} cut @ T = ${T.toFixed(3)}y`, font: { size: 14 } },
    margin: { t: 44, l: 60, r: 60, b: 50 },
    paper_bgcolor: "#1e2630",
    plot_bgcolor: "#1e2630",
    font: { color: "#e7ecf2" },
    xaxis: { title: "Strike", gridcolor: "#2a3340" },
    yaxis: { title: "IV", gridcolor: "#2a3340", side: "left" },
    yaxis2: {
      title: "Price",
      gridcolor: "#2a3340",
      overlaying: "y",
      side: "right",
      showgrid: false,
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

function renderSmiles() {
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
  surf.expiries.forEach((t, i) => {
    const cellId = `smile-tile-${i}`;
    const div = document.createElement("div");
    div.className = "smile-tile";
    div.id = cellId;
    grid.appendChild(div);

    // Market trace (markers + thin connecting line; gaps preserved as nulls).
    const mktX = surf.strikes.slice();
    const ivRow = (surf.iv && surf.iv[i]) ? surf.iv[i] : [];
    const mktY = mktX.map((_, j) => {
      const v = ivRow[j];
      return (v === null || v === undefined || !Number.isFinite(v)) ? null : v;
    });

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
      if (dense && dense.denseStrikes && dense.denseIv && dense.denseIv[i]) {
        hesX = dense.denseStrikes;
        hesY = dense.denseIv[i].map((v) => (v === null || v === undefined || !Number.isFinite(v)) ? null : v);
      } else if (cal.strikes && cal.hestonIv && cal.hestonIv[i]) {
        // Fallback: use market-grid Heston IV from the calibration result.
        hesX = cal.strikes;
        hesY = cal.hestonIv[i].map((v) => (v === null || v === undefined || !Number.isFinite(v)) ? null : v);
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
      xaxis: { title: "Strike", gridcolor: "#2a3340", tickfont: { size: 9 } },
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

  // Market trace at original strikes (skip null/NaN).
  const mktX = [], mktY = [];
  const ivRow = (surf.iv && surf.iv[safeIdx]) || [];
  for (let j = 0; j < surf.strikes.length; j++) {
    const v = ivRow[j];
    if (v === null || v === undefined || !Number.isFinite(v)) continue;
    mktX.push(surf.strikes[j]);
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
    traces.push({
      x: heston.strikes, y: hesY,
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
    xaxis: { title: "Strike", gridcolor: "#2a3340" },
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

  // Resize on window resize for visible plots.
  window.addEventListener("resize", () => {
    for (const el of $$(".plot")) if (el.data) Plotly.Plots.resize(el);
  });
}

wireUi();
