// Angaza Care: small progressive enhancements. Every page works without this file.
(function () {
  "use strict";

  // ---- Response clocks ----
  function span(ms) {
    var min = Math.max(1, Math.round(Math.abs(ms) / 60000));
    if (min < 60) return min + " min";
    var h = Math.floor(min / 60), m = min % 60;
    if (h < 48) return h + " h" + (m ? " " + m + " min" : "");
    return Math.round(h / 24) + " days";
  }
  function tickClocks() {
    var now = Date.now();
    document.querySelectorAll(".clock[data-due]").forEach(function (el) {
      if (el.dataset.done === "true") return;
      var due = Date.parse(el.dataset.due), start = Date.parse(el.dataset.start);
      var left = due - now, total = Math.max(1, due - start);
      var frac = Math.max(0, Math.min(1, left / total));
      el.style.setProperty("--left", (frac * 100).toFixed(1) + "%");
      el.classList.toggle("over", left <= 0);
      el.classList.toggle("late", left > 0 && frac < 0.25);
      var text = el.querySelector(".clock-text");
      if (text) text.textContent = left <= 0 ? "Overdue by " + span(left) : span(left) + " left to respond";
    });
  }
  tickClocks();
  setInterval(tickClocks, 30000);

  // ---- Toast ----
  var toast = document.querySelector(".toast");
  if (toast) setTimeout(function () { toast.remove(); }, 6000);

  // ---- Offline: cache pages for reading, queue CHW form posts until signal returns ----
  var QUEUE = "angaza-outbox";
  function readQueue() { try { return JSON.parse(localStorage.getItem(QUEUE) || "[]"); } catch (e) { return []; } }
  function writeQueue(q) { try { localStorage.setItem(QUEUE, JSON.stringify(q)); } catch (e) { /* storage full or blocked */ } }
  function showQueue() {
    var n = readQueue().length, el = document.querySelector("[data-queue-count]");
    if (el) { el.textContent = n ? n + " saved update" + (n === 1 ? "" : "s") + " will send when you are back online." : ""; }
  }
  function setOnline() { document.body.classList.toggle("is-offline", !navigator.onLine); showQueue(); }
  async function flush() {
    if (!navigator.onLine) return;
    var q = readQueue(), keep = [];
    for (var i = 0; i < q.length; i++) {
      try {
        var res = await fetch(q[i].action, { method: "POST", body: new URLSearchParams(q[i].data), credentials: "same-origin" });
        if (!res.ok && res.status >= 500) keep.push(q[i]);
      } catch (e) { keep.push(q[i]); }
    }
    writeQueue(keep);
    if (q.length && !keep.length) location.reload();
    showQueue();
  }
  window.addEventListener("online", function () { setOnline(); flush(); });
  window.addEventListener("offline", setOnline);
  setOnline();
  flush();

  document.addEventListener("submit", function (e) {
    var form = e.target;
    if (!form.matches("form[data-offline]") || navigator.onLine) return;
    e.preventDefault();
    var q = readQueue();
    q.push({ action: form.action, data: Array.from(new FormData(form).entries()), at: new Date().toISOString() });
    writeQueue(q);
    var note = form.querySelector("[data-saved]");
    if (note) note.hidden = false;
    showQueue();
  });

  if ("serviceWorker" in navigator) navigator.serviceWorker.register("/sw.js").catch(function () { /* not fatal */ });

  // ---- Small helpers ----
  document.querySelectorAll("[data-toggle-basis]").forEach(function (group) {
    function sync() {
      var basis = group.querySelector("input:checked").value;
      document.querySelectorAll("[data-basis]").forEach(function (el) { el.hidden = el.dataset.basis !== basis; });
    }
    group.addEventListener("change", sync); sync();
  });

  document.querySelectorAll("[data-decision]").forEach(function (group) {
    function sync() {
      var v = (group.querySelector("input:checked") || {}).value;
      document.querySelectorAll("[data-when]").forEach(function (el) { el.hidden = el.dataset.when !== v; });
    }
    group.addEventListener("change", sync); sync();
  });

  // Rule tester on the Rules page
  var tester = document.querySelector("[data-tester]");
  if (tester) {
    tester.addEventListener("submit", async function (e) {
      e.preventDefault();
      var out = tester.querySelector(".tester-result");
      var res = await fetch(tester.action, { method: "POST", body: new FormData(tester), credentials: "same-origin" });
      var r = await res.json();
      var label = { well: "Well, no flag", danger: "Danger sign, flag raised", unclear: "Unclear, a person reads it" }[r.outcome];
      out.innerHTML = "";
      var b = document.createElement("b"); b.textContent = label; out.appendChild(b);
      var detail = document.createElement("div"); detail.className = "muted";
      detail.textContent = (r.signs.length ? "Signs: " + r.signs.join(", ") + ". " : "") +
        (r.matched.length ? "Matched: " + r.matched.join(", ") + ". " : "") +
        (r.needsModel ? "The language model would be asked to read this one." : "Decided by the rules alone.");
      out.appendChild(detail);
    });
  }

  // USSD simulator
  var ussd = document.querySelector("[data-ussd]");
  if (ussd) {
    var screen = ussd.querySelector(".ussd"), input = ussd.querySelector("input[name=entry]"), path = [];
    var token = ussd.querySelector("input[name=__RequestVerificationToken]").value;
    async function send(text) {
      var body = new URLSearchParams({ id: ussd.dataset.mother, text: text, __RequestVerificationToken: token });
      var res = await fetch(ussd.dataset.action, { method: "POST", body: body, credentials: "same-origin" });
      var r = await res.json();
      screen.textContent = r.screen;
      if (r.done) { path = []; ussd.querySelector("[data-ussd-done]").hidden = false; }
    }
    ussd.querySelector("[data-ussd-dial]").addEventListener("click", function () {
      path = []; ussd.querySelector("[data-ussd-done]").hidden = true; send("");
    });
    ussd.querySelector("form").addEventListener("submit", function (e) {
      e.preventDefault();
      if (!input.value.trim()) return;
      path.push(input.value.trim()); input.value = ""; send(path.join("*"));
    });
  }

  document.querySelectorAll("[data-fill]").forEach(function (chip) {
    chip.addEventListener("click", function () {
      var target = document.querySelector(chip.dataset.target);
      target.value = chip.dataset.fill; target.focus();
    });
  });

  var thread = document.querySelector(".phone-body");
  if (thread) thread.scrollTop = thread.scrollHeight;
})();
