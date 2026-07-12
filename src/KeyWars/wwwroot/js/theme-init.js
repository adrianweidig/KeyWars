(() => {
  const storageKey = "keywars.theme";
  const supportedThemes = new Set(["dark", "light"]);

  function readStoredTheme() {
    try {
      const value = window.localStorage?.getItem(storageKey);
      return supportedThemes.has(value) ? value : null;
    } catch {
      return null;
    }
  }

  function readSystemTheme() {
    return window.matchMedia?.("(prefers-color-scheme: light)")?.matches ? "light" : "dark";
  }

  function normalizeTheme(value) {
    return supportedThemes.has(value) ? value : "dark";
  }

  function resolveTheme() {
    return readStoredTheme() || readSystemTheme();
  }

  function applyTheme(value) {
    const theme = normalizeTheme(value);
    document.documentElement.dataset.theme = theme;
    document.documentElement.style.colorScheme = theme;
    return theme;
  }

  function storeTheme(value) {
    try {
      window.localStorage?.setItem(storageKey, normalizeTheme(value));
      return true;
    } catch {
      return false;
    }
  }

  window.keyWarsTheme = Object.freeze({
    applyTheme,
    normalizeTheme,
    readStoredTheme,
    readSystemTheme,
    resolveTheme,
    storeTheme
  });
  applyTheme(resolveTheme());
})();
