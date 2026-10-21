"use strict";
const ScanQuestions = (() => {
  let generation = 0, currentJob, request, pending = false;
  const get = id => document.getElementById(id);
  const safeUrl = text => {
    try {
      const url = new URL(text);
      return url.protocol === "https:" && url.hostname === "scryfall.com"
        && !url.username && !url.password && !url.port ? url.href : null;
    } catch { return null; }
  };
  function reset() {
    generation++;
    currentJob = null;
    pending = false;
    get("scanQuestions").hidden = true;
    get("scanQuestions").open = false;
    get("scanQuestion").value = "";
    get("scanAnswer").replaceChildren();
    get("questionSuggestions").replaceChildren();
    get("questionStatus").textContent = "";
    get("askQuestion").disabled = false;
  }
  function show(jobId, enabled, api, result) {
    reset();
    currentJob = jobId;
    request = api;
    get("scanQuestions").hidden = !enabled;
    const names = [...new Set((result?.cards || []).filter(c => c.game === "mtg" && c.name).map(c => c.name))];
    const suggestions = names.slice(0, 2).map(name => `What does ${name} do?`);
    if (names.length > 1) suggestions.push(`Compare ${names[0]} and ${names[1]}.`);
    suggestions.push("Which card has the highest recorded price?", "What does my collection snapshot say I own?", "Explain the identification evidence for card 1.");
    for (const question of suggestions) {
      const button = document.createElement("button");
      button.type = "button"; button.className = "secondary";
      button.textContent = question;
      button.onclick = () => { get("scanQuestion").value = question; get("scanQuestion").focus(); };
      get("questionSuggestions").append(button);
    }
  }
  get("questionForm").onsubmit = async event => {
    event.preventDefault();
    if (pending || !currentJob) return;
    const question = get("scanQuestion").value.trim();
    if (!question || question.length > 1000) {
      get("questionStatus").textContent = "Enter a question between 1 and 1000 characters.";
      return;
    }
    const version = generation;
    pending = true;
    get("askQuestion").disabled = true;
    get("questionStatus").textContent = "Checking recorded scan facts or retrieving public card knowledge...";
    get("scanAnswer").replaceChildren();
    try {
      const answer = await (await request(`/jobs/${currentJob}/questions`, {
        method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ question })
      })).json();
      if (version !== generation) return;
      const sources = new Map(answer.sources.map(source => [source.id, source]));
      const paragraph = document.createElement("p");
      paragraph.className = "scan-answer-text";
      for (const part of answer.answer.split(/(\[ref_id:\d+\])/g)) {
        const match = /^\[ref_id:(\d+)\]$/.exec(part);
        const source = match && sources.get(match[1]);
        const url = source && safeUrl(source.url);
        if (url) {
          const link = document.createElement("a");
          link.href = url; link.target = "_blank"; link.rel = "noopener noreferrer";
          link.textContent = `[${Number(source.id) + 1}]`;
          link.setAttribute("aria-label", `Source: ${source.title}`);
          paragraph.append(link);
        } else paragraph.append(document.createTextNode(part));
      }
      get("scanAnswer").append(paragraph);
      if (answer.grounded && answer.sources.length) {
        const list = document.createElement("ul");
        for (const source of answer.sources) {
          const url = safeUrl(source.url);
          if (!url) throw new Error("The answer contains an invalid source link.");
          const item = document.createElement("li"), link = document.createElement("a");
          link.href = url; link.target = "_blank"; link.rel = "noopener noreferrer";
          link.textContent = `[${Number(source.id) + 1}] ${source.title} (Scryfall)`;
          item.append(link); list.append(item);
        }
        get("scanAnswer").append(list);
      }
      get("questionStatus").textContent = answer.kind === "scanEvidence"
        ? "Answered from this scan's recorded facts. Private collection and price data were not sent to Foundry IQ."
        : answer.kind === "missingKnowledge" ? "Missing card knowledge. The public catalogue needs refreshing."
        : answer.kind === "unsupported" ? "This scan has no supported Magic card identities."
        : answer.grounded ? "Answered using retrieved card knowledge. Check the cited sources; AI answers can be wrong."
        : "Insufficient cited evidence.";
    } catch (error) {
      if (version !== generation) return;
      console.error("Scan question failed.", error);
      get("scanAnswer").replaceChildren();
      get("questionStatus").textContent = error.status === 429
        ? "Question limit reached. Wait one minute before retrying."
        : `Could not answer: ${error.message || "knowledge service unavailable"}`;
    } finally {
      if (version === generation) {
        pending = false;
        get("askQuestion").disabled = false;
      }
    }
  };
  return { show, reset };
})();
