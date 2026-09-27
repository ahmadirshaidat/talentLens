// TalentLens front-end helpers (no framework — plain DOM).
(function () {
  "use strict";

  // Show a spinner on a button and disable it.
  function busy(button, on) {
    if (!button) return;
    button.disabled = on;
    const spinner = button.querySelector(".spinner-border");
    if (spinner) spinner.classList.toggle("d-none", !on);
  }

  // --- Confirm dangerous forms: <form data-confirm="Are you sure?"> ---
  document.querySelectorAll("form[data-confirm]").forEach(function (form) {
    form.addEventListener("submit", function (e) {
      if (!window.confirm(form.dataset.confirm)) e.preventDefault();
    });
  });

  // --- Auto-refresh while CVs are processing: <p data-auto-refresh="5000"> ---
  const refresh = document.querySelector("[data-auto-refresh]");
  if (refresh) {
    setTimeout(function () { window.location.reload(); }, parseInt(refresh.dataset.autoRefresh, 10) || 5000);
  }

  // --- Upload page: drag & drop + file list ---
  const dropzone = document.getElementById("dropzone");
  const input = document.getElementById("files");
  const list = document.getElementById("file-list");
  const uploadButton = document.getElementById("upload-button");
  if (dropzone && input) {
    const render = function () {
      list.innerHTML = "";
      Array.from(input.files).forEach(function (f) {
        const li = document.createElement("li");
        li.className = "list-group-item d-flex justify-content-between";
        const name = document.createElement("span");
        name.textContent = f.name;
        name.dir = "auto";
        const size = document.createElement("span");
        size.className = "text-muted small";
        size.textContent = (f.size / 1024).toFixed(0) + " KB";
        li.append(name, size);
        list.appendChild(li);
      });
      uploadButton.disabled = input.files.length === 0;
    };
    input.addEventListener("change", render);
    ["dragenter", "dragover"].forEach(function (ev) {
      dropzone.addEventListener(ev, function (e) { e.preventDefault(); dropzone.classList.add("dragover"); });
    });
    ["dragleave", "drop"].forEach(function (ev) {
      dropzone.addEventListener(ev, function (e) { e.preventDefault(); dropzone.classList.remove("dragover"); });
    });
    dropzone.addEventListener("drop", function (e) {
      input.files = e.dataTransfer.files;
      render();
    });
    document.getElementById("upload-form").addEventListener("submit", function () { busy(uploadButton, true); });
  }

  // --- Search page: spinner on submit ---
  const searchButton = document.getElementById("search-button");
  if (searchButton) {
    searchButton.form.addEventListener("submit", function () { busy(searchButton, true); });
  }

  // --- Forms that take a while (e.g. "Rank with AI"): <form data-busy> shows a spinner ---
  document.querySelectorAll("form[data-busy]").forEach(function (form) {
    form.addEventListener("submit", function () { busy(form.querySelector("button"), true); });
  });

  // --- AJAX buttons: <button data-ajax-post="/url" data-ajax-target="#id"> ---
  // POSTs with the anti-forgery token and swaps the returned HTML fragment into the target.
  // Used for "Why this candidate?" and "Check my match".
  document.querySelectorAll("[data-ajax-post]").forEach(function (button) {
    button.addEventListener("click", async function () {
      const target = document.querySelector(button.dataset.ajaxTarget);
      const token = document.querySelector("input[name='__RequestVerificationToken']");
      busy(button, true);
      try {
        const response = await fetch(button.dataset.ajaxPost, {
          method: "POST",
          headers: token ? { "RequestVerificationToken": token.value } : {},
        });
        const html = await response.text();
        if (response.ok) {
          target.innerHTML = html;
        } else {
          const alert = document.createElement("div");
          alert.className = "alert alert-warning py-1 px-2 small mt-2";
          alert.textContent = html || response.statusText;
          target.appendChild(alert);
        }
      } finally {
        busy(button, false);
      }
    });
  });
})();
