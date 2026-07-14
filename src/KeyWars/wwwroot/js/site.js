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

function attachThemeToggle() {
  const toggles = document.querySelectorAll("[data-theme-toggle]");
  const theme = window.keyWarsTheme;
  if (toggles.length === 0 || !theme) {
    return;
  }

  const root = document.documentElement;
  const renderTheme = (theme, persist = false) => {
    const currentTheme = window.keyWarsTheme.applyTheme(theme);
    const nextTheme = currentTheme === "dark" ? "light" : "dark";
    const nextLabel = nextTheme === "light" ? "Helles Design aktivieren" : "Dunkles Design aktivieren";
    const currentLabel = currentTheme === "light" ? "Helles Design" : "Dunkles Design";

    if (persist) {
      window.keyWarsTheme.storeTheme(currentTheme);
    }

    toggles.forEach((toggle) => {
      toggle.setAttribute("aria-pressed", currentTheme === "light" ? "true" : "false");
      toggle.classList.toggle("active", currentTheme === "light");
      toggle.dataset.theme = currentTheme;
      toggle.title = nextLabel;
      toggle.setAttribute("aria-label", `${nextLabel} (aktuell ${currentLabel})`);
    });
  };

  renderTheme(theme.readStoredTheme() || root.dataset.theme || theme.readSystemTheme());
  toggles.forEach((toggle) => {
    toggle.addEventListener("click", () => {
      renderTheme(theme.normalizeTheme(root.dataset.theme) === "dark" ? "light" : "dark", true);
    });
  });

  const media = window.matchMedia?.("(prefers-color-scheme: light)");
  media?.addEventListener?.("change", () => {
    if (!theme.readStoredTheme()) {
      renderTheme(theme.readSystemTheme());
    }
  });
}

function attachMobileMenu() {
  const toggles = document.querySelectorAll("[data-mobile-menu-toggle]");
  const panel = document.querySelector("[data-mobile-menu]");
  if (toggles.length === 0 || !panel) {
    return;
  }

  const opener = document.querySelector("[data-mobile-menu-opener]");
  const renderMenu = (open, restoreFocus = false) => {
    document.body.classList.toggle("mobile-menu-open", open);
    panel.setAttribute("aria-hidden", open ? "false" : "true");
    panel.inert = !open;
    toggles.forEach((toggle) => {
      if (toggle.hasAttribute("aria-expanded")) {
        toggle.setAttribute("aria-expanded", open ? "true" : "false");
      }
    });

    if (open) {
      panel.querySelector("button, a")?.focus({ preventScroll: true });
    } else if (restoreFocus) {
      opener?.focus({ preventScroll: true });
    }
  };

  const closeMenu = (restoreFocus = false) => renderMenu(false, restoreFocus);
  toggles.forEach((toggle) => {
    toggle.addEventListener("click", () => {
      const open = !document.body.classList.contains("mobile-menu-open");
      renderMenu(open, !open);
    });
  });
  document.querySelectorAll("[data-mobile-menu] a").forEach((link) => {
    link.addEventListener("click", () => closeMenu());
  });
  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && document.body.classList.contains("mobile-menu-open")) {
      closeMenu(true);
    }
  });

  renderMenu(false);
}

function attachDesktopSidebar() {
  const toggles = document.querySelectorAll("[data-sidebar-toggle]");
  const layout = window.keyWarsLayout;
  if (toggles.length === 0 || !layout) {
    return;
  }

  const renderSidebar = (state, persist = false) => {
    const collapsed = state === "collapsed";
    layout.applySidebar(collapsed ? "collapsed" : "expanded");
    document.body.classList.toggle("sidebar-collapsed", collapsed);
    if (persist) {
      layout.storeSidebar(collapsed ? "collapsed" : "expanded");
    }

    const label = collapsed ? "Sidebar ausklappen" : "Sidebar einklappen";
    toggles.forEach((toggle) => {
      toggle.setAttribute("aria-expanded", collapsed ? "false" : "true");
      toggle.setAttribute("aria-label", label);
      toggle.title = label;
    });
  };

  renderSidebar(layout.readSidebar());
  toggles.forEach((toggle) => {
    toggle.addEventListener("click", () => {
      renderSidebar(document.documentElement.dataset.sidebar === "collapsed" ? "expanded" : "collapsed", true);
    });
  });
}

function attachZenMode() {
  const toggles = document.querySelectorAll("[data-zen-toggle]");
  const layout = window.keyWarsLayout;
  if (toggles.length === 0 || !layout || !document.body.classList.contains("typing-focus-page")) {
    return;
  }

  const closeMobileMenu = () => {
    const panel = document.querySelector("[data-mobile-menu]");
    document.body.classList.remove("mobile-menu-open");
    if (panel) {
      panel.setAttribute("aria-hidden", "true");
      panel.inert = true;
    }
    document.querySelectorAll("[data-mobile-menu-toggle][aria-expanded]").forEach((toggle) => {
      toggle.setAttribute("aria-expanded", "false");
    });
  };

  const renderZen = (active, persist = false) => {
    const enabled = layout.applyZen(active);
    document.body.classList.toggle("zen-mode", enabled);
    if (enabled) {
      closeMobileMenu();
    }
    if (persist) {
      layout.storeZen(enabled);
    }

    const label = enabled ? "Zen-Modus beenden" : "Zen-Modus";
    const title = enabled ? "Zen-Modus beenden (Escape)" : "Zen-Modus aktivieren (Alt+Z)";
    toggles.forEach((toggle) => {
      toggle.setAttribute("aria-pressed", enabled ? "true" : "false");
      toggle.setAttribute("aria-label", label);
      toggle.title = title;
      const text = toggle.querySelector("[data-zen-label]");
      if (text) {
        text.textContent = label;
      }
    });
  };

  const toggleZen = () => renderZen(document.documentElement.dataset.zen !== "true", true);
  renderZen(layout.readZen());
  toggles.forEach((toggle) => toggle.addEventListener("click", toggleZen));
  document.addEventListener("keydown", (event) => {
    if (event.altKey && !event.ctrlKey && !event.metaKey && event.key.toLowerCase() === "z") {
      event.preventDefault();
      toggleZen();
      return;
    }

    if (event.key === "Escape" && document.documentElement.dataset.zen === "true") {
      renderZen(false, true);
      toggles[0]?.focus({ preventScroll: true });
    }
  });
}

const overflowTitleSelector = [
  "[data-full-text]",
  "[data-overflow-title]",
  ".sidebar-nav a",
  ".sidebar-nav a span",
  ".mobile-bottom-nav a",
  ".mobile-bottom-nav a span",
  ".profile-menu",
  ".quickstart-card",
  ".quickstart-card strong",
  ".quickstart-card small",
  ".quest-card strong",
  ".quest-card p",
  ".result-row strong",
  ".result-row small",
  ".leaderboard-row span",
  ".mini-podium-player strong",
  ".race-lane-meta span",
  ".live-typing-meta strong",
  ".motivation-event-copy strong",
  ".motivation-event-copy span",
  ".table th",
  ".table td",
  ".badge",
  ".pill"
].join(",");

function normalizeTooltipText(value) {
  return (value || "").replace(/\s+/g, " ").trim();
}

function hasVisualOverflow(element) {
  return element.scrollWidth > element.clientWidth + 1 || element.scrollHeight > element.clientHeight + 1;
}

function applyOverflowTitles(root = document) {
  const elements = root instanceof Element
    ? [root, ...root.querySelectorAll(overflowTitleSelector)]
    : [...root.querySelectorAll(overflowTitleSelector)];

  elements.forEach((element) => {
    if (!(element instanceof HTMLElement)) {
      return;
    }

    const authoredTitle = element.getAttribute("title");
    if (authoredTitle && element.dataset.autoTitle !== "true") {
      return;
    }

    const text = normalizeTooltipText(element.dataset.fullText || element.textContent);
    if (!text) {
      if (element.dataset.autoTitle === "true") {
        element.removeAttribute("title");
        delete element.dataset.autoTitle;
      }
      return;
    }

    if (element.dataset.fullText || element.dataset.overflowTitle === "always" || hasVisualOverflow(element)) {
      element.title = text;
      element.dataset.autoTitle = "true";
    } else if (element.dataset.autoTitle === "true") {
      element.removeAttribute("title");
      delete element.dataset.autoTitle;
    }
  });
}

function attachOverflowTitles() {
  if (!document.body) {
    return;
  }

  let scheduled = false;
  const schedule = () => {
    if (scheduled) {
      return;
    }

    scheduled = true;
    window.requestAnimationFrame(() => {
      scheduled = false;
      applyOverflowTitles();
    });
  };

  applyOverflowTitles();
  window.addEventListener("resize", schedule);

  const observer = new MutationObserver(schedule);
  observer.observe(document.body, {
    subtree: true,
    childList: true,
    characterData: true,
    attributes: true,
    attributeFilter: ["class", "style", "data-full-text", "data-overflow-title"]
  });
}

function attachArenaCreateForms() {
  document.querySelectorAll("[data-arena-create-form]").forEach((form) => {
    const select = form.querySelector("[data-arena-text-select]");
    const title = form.querySelector("[data-text-preview-title]");
    const stats = form.querySelector("[data-text-preview-stats]");
    const body = form.querySelector("[data-text-preview-body]");
    const modeInputs = [...form.querySelectorAll("[data-arena-mode-input]")];
    const roundCount = form.querySelector("[data-arena-round-count]");
    const roundCountGroup = form.querySelector("[data-arena-round-count-group]");
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
    const updateMode = () => {
      const selectedMode = modeInputs.find((input) => input.checked)?.value || "Classic";
      form.querySelectorAll(".arena-mode-card").forEach((card) => {
        card.classList.toggle("selected", card.querySelector("[data-arena-mode-input]")?.checked === true);
      });
      roundCountGroup?.classList.toggle("is-hidden", selectedMode !== "Series");
      if (roundCount && selectedMode !== "Series") {
        roundCount.value = "1";
      } else if (roundCount && !["3", "5"].includes(roundCount.value)) {
        roundCount.value = "3";
      }
    };

    modeInputs.forEach((input) => input.addEventListener("change", updateMode));
    updatePreview();
    updateMode();
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
attachThemeToggle();
attachMobileMenu();
attachDesktopSidebar();
attachZenMode();
attachOverflowTitles();
