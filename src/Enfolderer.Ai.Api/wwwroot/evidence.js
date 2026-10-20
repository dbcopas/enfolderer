"use strict";
const ScanEvidence = (() => {
  let version = 0, cropUrl;
  const get = id => document.getElementById(id);
  const present = value => typeof value === "string" && value.trim().length > 0;
  const identified = card => present(card.name) && present(card.set);
  const printingPlaced = card => identified(card) && present(card.collectorNumber) && (
    card.artVerdict?.toLowerCase() === "moved"
    || card.artVerdict?.toLowerCase() === "agrees" && card.artMargin > 0
    || ["confirmed", "corrected", "named", "relocated", "fuzzy"].includes(card.resolution?.toLowerCase()));
  const element = (tag, text, className) => {
    const node = document.createElement(tag);
    if (text != null) node.textContent = text;
    if (className) node.className = className;
    return node;
  };
  const value = text => present(text) ? text : "Not recorded";
  const score = number => Number.isFinite(number) ? number.toFixed(3) : "Not recorded";
  const amount = number => number == null ? "Unavailable"
    : new Intl.NumberFormat(undefined, { style: "currency", currency: "EUR" }).format(number);
  const resolutions = {
    confirmed: "The catalogue confirmed the reported printing.",
    corrected: "The catalogue corrected the reading. Compare the read and resolved fields below.",
    named: "The catalogue resolved the printing using the name.",
    relocated: "The catalogue resolved the printing in a different set.",
    fuzzy: "The catalogue used an approximate match; check the photographed text.",
    assumed: "A printing was assumed rather than established from the reading.",
    unverified: "The printing lacks independent corroboration.",
    unplaced: "The card was named, but its exact printing was not established.",
    ambiguous: "The evidence did not separate competing printings.",
    unresolved: "The catalogue could not resolve this reading."
  };

  function releaseCrop() {
    if (cropUrl) URL.revokeObjectURL(cropUrl);
    cropUrl = undefined;
  }

  function reset() {
    version++;
    releaseCrop();
    get("scanEvidence").hidden = true;
    get("scanEvidence").open = false;
    get("scanEvidence").ontoggle = null;
    get("evidenceCard").onchange = null;
    get("evidenceCard").replaceChildren();
    get("evidenceBody").replaceChildren();
  }

  function facts(parent, title, entries) {
    const section = element("section");
    section.append(element("h4", title));
    const list = element("dl", null, "evidence-facts");
    for (const [label, text] of entries) list.append(element("dt", label), element("dd", text));
    section.append(list);
    parent.append(section);
  }

  function show(result, jobId, request) {
    reset();
    const cards = result.cards;
    const panel = get("scanEvidence");
    panel.hidden = false;
    const named = cards.filter(identified).length;
    const placed = cards.filter(printingPlaced).length;
    get("evidenceSummary").textContent = `${cards.length} detected | ${named} named | ${placed} printings placed | ${cards.length - placed} need review`;
    const select = get("evidenceCard");
    select.disabled = cards.length === 0;
    cards.forEach((card, position) => {
      const option = element("option", `${card.index + 1}. ${card.name || "Unidentified card"}`);
      option.value = String(position);
      select.append(option);
    });
    let loadCrop;
    function selectCard() {
      const current = ++version;
      releaseCrop();
      const body = get("evidenceBody");
      body.replaceChildren();
      const card = cards[Number(select.value)];
      if (!card) {
        body.append(element("p", "No card evidence was recorded for this scan.", "muted"));
        loadCrop = null;
        return;
      }
      const status = !identified(card) ? "Unidentified" : printingPlaced(card) ? "Printing placed" : "Printing needs review";
      body.append(element("h3", card.name || "Unidentified card"));
      body.append(element("p", status, printingPlaced(card) ? "evidence-status" : "warning"));
      const layout = element("div", null, "evidence-layout");
      const figure = element("figure", null, "evidence-crop");
      const image = element("img");
      image.alt = `Photographed crop for card ${card.index + 1}`;
      image.hidden = true;
      const caption = element("figcaption", "The rectified crop sent for identification.");
      const cropStatus = element("p", "Open this panel to load the private crop.", "small muted");
      cropStatus.setAttribute("role", "status");
      const retry = element("button", "Retry crop", "secondary");
      retry.type = "button";
      retry.hidden = true;
      let loading = false, loaded = false;
      loadCrop = async () => {
        if (loading || loaded || current !== version) return;
        loading = true;
        retry.hidden = true;
        cropStatus.textContent = "Loading private crop...";
        try {
          const blob = await (await request(`/jobs/${jobId}/crops/${card.index}`)).blob();
          if (current !== version) return;
          cropUrl = URL.createObjectURL(blob);
          image.src = cropUrl;
          await image.decode();
          if (current !== version) return;
          image.hidden = false;
          cropStatus.textContent = "";
          loaded = true;
        } catch (error) {
          if (current !== version) return;
          releaseCrop();
          image.removeAttribute("src");
          console.warn("Scan evidence crop could not be loaded.", error);
          cropStatus.textContent = `Crop unavailable: ${error.message || "image could not be read"}. It may have been purged or expired. The recorded evidence remains below.`;
          retry.hidden = false;
        } finally { loading = false; }
      };
      retry.onclick = () => loadCrop();
      figure.append(image, caption, cropStatus, retry);
      const evidence = element("div", null, "evidence-records");
      facts(evidence, "Read from the photograph", [
        ["Title", value(card.readName)], ["Set", value(card.readSet)],
        ["Collector number", value(card.readCollectorNumber)], ["Language", value(card.readLanguage)]
      ]);
      facts(evidence, "Resolved catalogue identity", [
        ["Name", value(card.name)], ["Set", value(card.set)],
        ["Collector number", value(card.collectorNumber)], ["Language", value(card.language)],
        ["Resolution", value(card.resolution)]
      ]);
      evidence.append(element("p", resolutions[card.resolution?.toLowerCase()]
        || "No explanation is recorded for this catalogue resolution. Missing data does not prove a tool was skipped.", "small muted"));
      facts(evidence, "Independent artwork check", [
        ["Verdict", value(card.artVerdict)], ["Reason", value(card.artReason)],
        ["Moved from", value(card.artMovedFrom)], ["Separation margin (higher is better)", score(card.artMargin)],
        ["Image distance (lower is better)", score(card.artDistance)]
      ]);
      evidence.append(element("p", "Shared artwork can leave printings indistinguishable. An inconclusive or missing check is not confirmation.", "small muted"));
      facts(evidence, "Identification provenance", [
        ["Game", value(card.game)], ["Agent", value(card.agent)],
        ["Agent confidence score (not a probability)", score(card.confidence)]
      ]);
      if (card.prices) {
        const prices = card.prices;
        const retrieved = prices.retrievedAt && new Date(prices.retrievedAt);
        facts(evidence, "Price provenance", [
          ["Nonfoil", amount(prices.nonfoil)], ["Nonfoil price language", value(prices.nonfoilLanguage)],
          ["Foil", amount(prices.foil)], ["Foil price language", value(prices.foilLanguage)],
          ["Ranking value (nonfoil, otherwise foil)", amount(prices.nonfoil ?? prices.foil)],
          ["Source", value(prices.source)],
          ["Retrieved", retrieved && Number.isFinite(retrieved.getTime()) && retrieved.getUTCFullYear() > 1
            ? retrieved.toLocaleString() : "Not recorded"]
        ]);
        if (prices.error) evidence.append(element("p", `Price lookup: ${prices.error}`, "warning"));
      } else evidence.append(element("p", "No price lookup result was recorded.", "small muted"));
      if (card.ownership) {
        const owned = card.ownership;
        facts(evidence, "Collection comparison", owned.error ? [["Error", owned.error]] : [
          ["Owned (exact printing)", String(owned.quantity)], ["Owned (all sets)", String(owned.allQuantity)],
          ["Copies required", String(owned.requiredQuantity)]
        ]);
      }
      if (card.error) evidence.append(element("p", `Identification error: ${card.error}`, "warning"));
      layout.append(figure, evidence);
      body.append(layout);
      if (panel.open) void loadCrop();
    }
    select.onchange = selectCard;
    panel.ontoggle = () => { if (panel.open && loadCrop) void loadCrop(); };
    selectCard();
  }
  return { show, reset, printingPlaced };
})();
