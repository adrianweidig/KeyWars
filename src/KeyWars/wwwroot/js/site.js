import { attachTypingApps } from "./typing.js";
import { attachArenaPages } from "./arena.js";

function attachRoomCodeInputs() {
  document.querySelectorAll("[data-room-code-input]").forEach((input) => {
    input.addEventListener("input", () => {
      input.value = input.value
        .toUpperCase()
        .replace(/[^A-HJ-NP-Z2-9]/g, "")
        .slice(0, 6);
    });
  });
}

function attachCopyButtons() {
  document.querySelectorAll("[data-copy-text]").forEach((button) => {
    button.addEventListener("click", async () => {
      const status = button.parentElement?.querySelector("[data-copy-status]");
      button.disabled = true;
      try {
        await copyText(button.dataset.copyText || "");
        setCopyStatus(status, button.dataset.copySuccess || "Kopiert.");
      } catch {
        setCopyStatus(status, "Kopieren nicht möglich. Code markieren und kopieren.");
      } finally {
        window.setTimeout(() => {
          button.disabled = false;
        }, 600);
      }
    });
  });
}

function attachShareButtons() {
  document.querySelectorAll("[data-share-title]").forEach((button) => {
    button.addEventListener("click", async () => {
      const status = button.parentElement?.querySelector("[data-copy-status]");
      button.disabled = true;
      try {
        if (navigator.share) {
          await navigator.share({
            title: button.dataset.shareTitle || "KeyWars-Raum",
            text: button.dataset.shareText || "",
            url: absoluteUrl(button.dataset.shareUrl || window.location.href)
          });
          setCopyStatus(status, "Einladung geteilt.");
        } else {
          await copyText(button.dataset.shareFallback || button.dataset.shareText || "");
          setCopyStatus(status, "Raumcode kopiert.");
        }
      } catch (error) {
        if (error?.name !== "AbortError") {
          setCopyStatus(status, "Teilen nicht möglich. Code markieren und kopieren.");
        }
      } finally {
        window.setTimeout(() => {
          button.disabled = false;
        }, 600);
      }
    });
  });
}

function attachSubmitGuards() {
  document.querySelectorAll("form[data-submit-guard]").forEach((form) => {
    form.addEventListener("submit", (event) => {
      if (form.dataset.submitting === "true") {
        event.preventDefault();
        return;
      }

      form.dataset.submitting = "true";
      form.querySelectorAll("button[type='submit'], input[type='submit']").forEach((button) => {
        button.disabled = true;
        if (button.tagName === "BUTTON" && form.dataset.submitBusyText) {
          button.textContent = form.dataset.submitBusyText;
        }
      });
    });
  });
}

function attachMobileMenu() {
  const toggles = document.querySelectorAll("[data-mobile-menu-toggle]");
  if (toggles.length === 0) {
    return;
  }

  const closeMenu = () => document.body.classList.remove("mobile-menu-open");
  const toggleMenu = () => document.body.classList.toggle("mobile-menu-open");
  toggles.forEach((toggle) => {
    toggle.addEventListener("click", toggleMenu);
  });
  document.querySelectorAll("[data-mobile-menu] a").forEach((link) => {
    link.addEventListener("click", closeMenu);
  });
  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape") {
      closeMenu();
    }
  });
}

function attachArenaCreateForms() {
  document.querySelectorAll("[data-arena-create-form]").forEach((form) => {
    const select = form.querySelector("[data-arena-text-select]");
    const title = form.querySelector("[data-text-preview-title]");
    const stats = form.querySelector("[data-text-preview-stats]");
    const body = form.querySelector("[data-text-preview-body]");
    if (!select || !title || !stats || !body) {
      return;
    }

    const updatePreview = () => {
      const option = select.selectedOptions?.[0];
      if (!option) {
        title.textContent = "Kein Text verfügbar";
        stats.textContent = "";
        body.textContent = "Lege zuerst einen Trainingstext an.";
        return;
      }

      const words = option.dataset.words || "0";
      const characters = option.dataset.characters || "0";
      const duration = option.dataset.duration || "0";
      title.textContent = option.dataset.title || option.textContent.trim();
      stats.textContent = `${words} Wörter · ${characters} Zeichen · ca. ${duration} s`;
      body.textContent = option.dataset.preview || "";
    };

    select.addEventListener("change", updatePreview);
    updatePreview();
  });
}

async function copyText(text) {
  if (navigator.clipboard && window.isSecureContext) {
    await navigator.clipboard.writeText(text);
    return;
  }

  const textarea = document.createElement("textarea");
  textarea.value = text;
  textarea.setAttribute("readonly", "");
  textarea.className = "visually-hidden";
  document.body.append(textarea);
  textarea.select();
  const copied = document.execCommand("copy");
  textarea.remove();
  if (!copied) {
    throw new Error("Copy command failed.");
  }
}

function setCopyStatus(status, text) {
  if (!status) {
    return;
  }

  status.textContent = text;
  window.clearTimeout(Number(status.dataset.copyStatusTimer || 0));
  status.dataset.copyStatusTimer = String(window.setTimeout(() => {
    status.textContent = "";
  }, 3000));
}

function absoluteUrl(value) {
  return new URL(value, window.location.origin).toString();
}

attachTypingApps();
attachArenaPages();
attachArenaCreateForms();
attachRoomCodeInputs();
attachCopyButtons();
attachShareButtons();
attachSubmitGuards();
attachMobileMenu();
