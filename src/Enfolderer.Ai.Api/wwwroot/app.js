"use strict";
const $ = id => document.getElementById(id);
let auth, account, config, photo, activeJob, polling = false, busy = false;
let previewUrl, imageUrl, csvUrl, collectionAvailable = false;
let scansAvailable = false;
const scopes = () => [config.scope];
const report = error => {
  console.error(error);
  $("message").textContent = error.message || "Something went wrong. Please try again.";
  $("message").hidden = false;
};
const clearMessage = () => { $("message").hidden = true; };
const run = task => async event => {
  clearMessage();
  try { await task(event); } catch (error) { report(error); }
};
const jobKey = () => "enfolderer.scan." + account.localAccountId;
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));

async function token() {
  try {
    return (await auth.acquireTokenSilent({ scopes: scopes(), account })).accessToken;
  } catch (error) {
    if (error instanceof msal.InteractionRequiredAuthError) {
      polling = false;
      await auth.acquireTokenRedirect({ scopes: scopes(), account });
      throw new Error("Sign-in needs to be refreshed. Your scan will resume afterwards.");
    }
    throw error;
  }
}

async function api(path, options = {}) {
  const response = await fetch(path, {
    ...options, cache: "no-store",
    headers: { ...options.headers, Authorization: "Bearer " + await token() }
  });
  if (!response.ok) {
    let detail = `${response.status} ${response.statusText}`;
    try {
      const body = await response.json();
      detail = body.error || body.detail || body.title || detail;
      if (body.code === "scan_allowance_exhausted") {
        scansAvailable = false;
        $("allowance").textContent = detail;
        setBusy(busy);
      }
    } catch (error) { console.warn("The API error response was not JSON.", error); }
    if (response.status === 403 && !detail.includes("scan allowance")) detail = "This account is not approved for the private pilot. Ask the administrator to invite and approve your account.";
    if (response.status === 413) detail = "This file is too large. Use a smaller photo or export a smaller collection snapshot.";
    throw new Error(detail);
  }
  return response;
}

function setBusy(value) {
  busy = value;
  $("scan").disabled = value || !photo || !scansAvailable;
  for (const id of ["camera", "photo", "snapshot", "deleteCollection"]) $(id).disabled = value;
  $("useCollection").disabled = value || !collectionAvailable;
  $("previewWrap").classList.toggle("scanning", value);
  $("scanSweep").hidden = !value;
}

async function loadAllowance() {
  scansAvailable = false;
  setBusy(busy);
  const allowance = await (await api("/allowance/")).json();
  scansAvailable = allowance.unlimited || allowance.remaining > 0;
  $("allowance").textContent = allowance.unlimited ? "Unlimited scans"
    : `${allowance.remaining} scans remaining. One submitted photo uses one scan, including failed scans.`
      + (scansAvailable ? "" : " Ask the administrator for more scans. Your previous downloads remain available.");
  setBusy(busy);
}

async function loadCollection() {
  const state = await (await api("/collection/")).json();
  collectionAvailable = state.available;
  $("collectionBadge").textContent = state.available ? "Ready" : "Not uploaded";
  $("collectionInfo").textContent = state.available
    ? `${state.printings.toLocaleString()} printings. Exported ${new Date(state.exportedAt).toLocaleString()}.`
    : "No collection snapshot. You can still scan for printings and prices.";
  $("deleteCollection").hidden = !state.available;
  $("useCollection").checked = state.available;
  setBusy(busy);
}

async function choosePhoto(event) {
  if (busy) return;
  const file = event.target.files[0];
  event.target.value = "";
  if (!file) return;
  if (file.size > 64 * 1024 * 1024) throw new Error("Please choose a photo smaller than 64 MB.");
  const originalUrl = URL.createObjectURL(file);
  try {
    const image = new Image();
    image.src = originalUrl;
    try { await image.decode(); }
    catch (error) {
      console.warn("Photo decoding failed.", error);
      throw new Error("This browser cannot read that photo. Choose or export a JPEG or PNG instead of HEIC/RAW.");
    }
    const scale = Math.min(1, 4096 / Math.max(image.naturalWidth, image.naturalHeight));
    const canvas = document.createElement("canvas");
    canvas.width = Math.round(image.naturalWidth * scale);
    canvas.height = Math.round(image.naturalHeight * scale);
    canvas.getContext("2d").drawImage(image, 0, 0, canvas.width, canvas.height);
    photo = await new Promise((resolve, reject) => canvas.toBlob(blob =>
      blob ? resolve(blob) : reject(new Error("Could not prepare the photo.")), "image/jpeg", .95));
    if (previewUrl) URL.revokeObjectURL(previewUrl);
    previewUrl = URL.createObjectURL(photo);
    $("preview").src = previewUrl;
    $("previewWrap").hidden = false;
    $("photoInfo").textContent = `${canvas.width} x ${canvas.height} pixels | ${(photo.size / 1024 / 1024).toFixed(1)} MB | location metadata removed`;
    $("results").hidden = true;
    $("progressPanel").hidden = true;
    setBusy(false);
  } finally { URL.revokeObjectURL(originalUrl); }
}

function showProgress(job, started) {
  $("progressPanel").hidden = false;
  $("retry").hidden = true;
  const pricing = job.progressMessage && job.cardsDetected > 0 && job.cardsProcessed >= job.cardsDetected;
  const stage = job.status === "DetectingBoundaries" ? "Detecting your cards"
    : job.status === "Identifying" ? (pricing ? "Adding prices and collection overlays" : "Reading each card")
    : job.status === "Completed" ? "Your scan is ready" : "Waiting for the scanner";
  $("stage").textContent = stage;
  $("progressText").textContent = job.progressMessage || (job.cardsDetected > 0
    ? `${job.cardsProcessed} of ${job.cardsDetected} cards processed | ${job.cardsIdentified} identified`
    : "Measuring card boundaries and finding the reading order.");
  const progress = $("progress");
  if (job.cardsDetected > 0) {
    progress.max = job.cardsDetected;
    progress.value = job.cardsProcessed;
  } else progress.removeAttribute("value");
  const current = job.status === "Completed" ? 4 : pricing ? 3
    : job.status === "Identifying" ? 2 : job.status === "DetectingBoundaries" ? 1 : 0;
  ["stageUpload", "stageDetect", "stageIdentify", "stageRender"].forEach((id, i) => {
    $(id).classList.toggle("active", current === i);
    $(id).classList.toggle("done", current > i);
  });
  $("elapsed").textContent = `${Math.floor((Date.now() - started) / 1000)} seconds elapsed. Detailed scans can take several minutes.`;
  $("spinner").hidden = job.status === "Completed";
}

async function showResults(result) {
  $("results").hidden = false;
  $("summary").textContent = `${result.cards.length} cards detected. Download the overlay and price-ranked CSV below.`;
  $("cardList").replaceChildren();
  const amount = value => value == null ? "unavailable" : new Intl.NumberFormat(undefined, { style: "currency", currency: "EUR" }).format(value);
  for (const card of result.cards) {
    const row = document.createElement("article");
    row.className = "card-row";
    const title = document.createElement("strong");
    title.textContent = `${card.index + 1}. ${card.name || "Unidentified card"}`;
    const details = document.createElement("p");
    details.textContent = `${(card.set || "").toUpperCase()} ${card.collectorNumber || ""} | ${card.language || "Unknown language"} | Nonfoil ${amount(card.prices?.nonfoil)} | Foil ${amount(card.prices?.foil)}`;
    row.append(title, details);
    if (card.ownership) {
      const owned = document.createElement("p");
      const o = card.ownership;
      owned.textContent = o.error ? `Ownership unknown: ${o.error}`
        : `Owned: ${o.quantity} | All sets: ${o.allQuantity}`;
      owned.className = o.error ? "warning" : o.quantity < o.requiredQuantity ? "missing" : "";
      row.append(owned);
    }
    const printingPlaced = card.set && card.collectorNumber && (card.artVerdict === "moved"
      || card.artVerdict === "agrees" && card.artMargin > 0
      || ["confirmed", "corrected", "named", "relocated", "fuzzy"].includes(card.resolution));
    if (card.error || card.prices?.error || !printingPlaced) {
      const warning = document.createElement("p");
      warning.className = "warning";
      warning.textContent = card.error || card.prices?.error || `Printing needs review (${card.resolution || "no catalogue resolution"}).`;
      row.append(warning);
    }
    $("cardList").append(row);
  }
  if (csvUrl) URL.revokeObjectURL(csvUrl);
  csvUrl = URL.createObjectURL(await (await api(`/jobs/${activeJob.id}/cards.csv`)).blob());
  $("csvDownload").href = csvUrl;
  $("csvDownload").download = `scanned_cards_${activeJob.id}.csv`;
  $("csvDownload").hidden = false;
  $("resultImage").hidden = true;
  $("imageDownload").hidden = true;
  if (result.annotatedImagePath) {
    if (imageUrl) URL.revokeObjectURL(imageUrl);
    imageUrl = URL.createObjectURL(await (await api(`/jobs/${activeJob.id}/annotated-image`)).blob());
    $("resultImage").src = imageUrl;
    $("imageDownload").href = imageUrl;
    $("imageDownload").download = `scan-${activeJob.id}-overlay.png`;
    $("resultImage").hidden = false;
    $("imageDownload").hidden = false;
  } else if (result.annotationError) throw new Error(result.annotationError + " The CSV and card list remain available.");
}

async function pollJob() {
  if (polling || !activeJob) return;
  polling = true;
  let finished = false;
  setBusy(true);
  $("spinner").hidden = false;
  try {
    while (polling) {
      const job = await (await api(`/jobs/${activeJob.id}`)).json();
      showProgress(job, activeJob.started);
      if (job.status === "Failed") {
        sessionStorage.removeItem(jobKey());
        finished = true;
        throw new Error(job.error || "This scan failed. Please try another photograph.");
      }
      if (job.status === "Completed") {
        await showResults(job.result);
        finished = true;
        return;
      }
      if (job.status === "Pending") throw new Error("This upload was interrupted. Select the photo and start a new scan.");
      await pause(document.hidden ? 8000 : 2500);
    }
  } finally {
    polling = false;
    setBusy(false);
    $("spinner").hidden = true;
    $("retry").hidden = finished || !activeJob;
  }
}

async function startScan() {
  if (!photo || busy) return;
  await loadAllowance();
  if (!scansAvailable) return;
  $("results").hidden = true;
  $("retry").hidden = true;
  $("progressPanel").hidden = false;
  $("stage").textContent = "Uploading your photo";
  $("progressText").textContent = "Preparing a private scan...";
  $("progress").removeAttribute("value");
  $("spinner").hidden = false;
  setBusy(true);
  try {
    const job = await (await api("/jobs/", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ fileName: "mobile-photo.jpg", gameHint: "mtg",
        useCollection: $("useCollection").checked })
    })).json();
    await api(`/jobs/${job.jobId}/content`, { method: "PUT", headers: { "Content-Type": "image/jpeg" }, body: photo });
    await api(`/jobs/${job.jobId}/submit`, { method: "POST" });
    activeJob = { id: job.jobId, started: Date.now() };
    sessionStorage.setItem(jobKey(), JSON.stringify(activeJob));
    try { await loadAllowance(); } catch (error) { report(error); }
  } catch (error) {
    setBusy(false);
    $("spinner").hidden = true;
    throw error;
  }
  await pollJob();
}

async function initialize() {
  config = await (await fetch("/web-config", { cache: "no-store" })).json();
  if (!config.enabled) throw new Error("Mobile sign-in has not been configured by the administrator.");
  if (!window.msal) throw new Error("The Microsoft sign-in library could not load. Check your connection and reload.");
  auth = new msal.PublicClientApplication({
    auth: { clientId: config.clientId, authority: `https://login.microsoftonline.com/${config.tenantId}`,
      redirectUri: location.origin + "/", postLogoutRedirectUri: location.origin + "/", navigateToLoginRequestUrl: false },
    cache: { cacheLocation: "sessionStorage" }
  });
  await auth.initialize();
  const redirected = await auth.handleRedirectPromise();
  account = redirected?.account || auth.getActiveAccount() || auth.getAllAccounts()[0];
  $("signIn").disabled = false;
  $("signIn").textContent = "Sign in with your invited account";
  if (!account) return;
  auth.setActiveAccount(account);
  $("welcome").hidden = true;
  $("workspace").hidden = false;
  $("signOut").hidden = false;
  $("account").textContent = `Signed in as ${account.username}`;
  try { await loadAllowance(); } catch (error) { report(error); }
  await loadCollection();
  const saved = sessionStorage.getItem(jobKey());
  if (saved) {
    activeJob = JSON.parse(saved);
    if (/^[a-f0-9]{32}$/.test(activeJob.id) && Number.isFinite(activeJob.started)) await pollJob();
    else sessionStorage.removeItem(jobKey());
  }
}

$("signIn").onclick = run(() => auth.loginRedirect({ scopes: scopes(), prompt: "select_account" }));
$("signOut").onclick = run(async () => { polling = false; await auth.logoutRedirect({ account }); });
$("camera").onchange = run(choosePhoto);
$("photo").onchange = run(choosePhoto);
$("scan").onclick = run(startScan);
$("retry").onclick = run(pollJob);
$("snapshot").onchange = run(async event => {
  const file = event.target.files[0];
  event.target.value = "";
  if (!file) return;
  if (file.size > 32 * 1024 * 1024) throw new Error("The snapshot must be smaller than 32 MB.");
  $("snapshot").disabled = true;
  try {
    await api("/collection/", { method: "PUT", headers: { "Content-Type": "application/json" }, body: file });
    await loadCollection();
  } finally { $("snapshot").disabled = busy; }
});
$("deleteCollection").onclick = run(async () => {
  if (!confirm("Delete your stored collection snapshot? Existing scan images will not change.")) return;
  await api("/collection/", { method: "DELETE" });
  await loadCollection();
});
initialize().catch(report);
