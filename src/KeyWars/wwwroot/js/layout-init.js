(() => {
  const root = document.documentElement;
  const sidebarKey = "keywars.sidebar";
  const zenKey = "keywars.zen";

  function read(storageName, key, fallback) {
    try {
      const storage = window[storageName];
      return storage?.getItem(key) || fallback;
    } catch {
      return fallback;
    }
  }

  function store(storageName, key, value) {
    try {
      const storage = window[storageName];
      storage?.setItem(key, value);
      return true;
    } catch {
      return false;
    }
  }

  function applySidebar(value) {
    const state = value === "collapsed" ? "collapsed" : "expanded";
    root.dataset.sidebar = state;
    return state;
  }

  function applyZen(value) {
    const active = value === true || value === "true";
    root.dataset.zen = active ? "true" : "false";
    return active;
  }

  function readSidebar() {
    return read("localStorage", sidebarKey, "expanded") === "collapsed" ? "collapsed" : "expanded";
  }

  function readZen() {
    return read("sessionStorage", zenKey, "false") === "true";
  }

  window.keyWarsLayout = Object.freeze({
    applySidebar,
    applyZen,
    readSidebar,
    readZen,
    storeSidebar(value) {
      return store("localStorage", sidebarKey, applySidebar(value));
    },
    storeZen(value) {
      return store("sessionStorage", zenKey, applyZen(value) ? "true" : "false");
    }
  });

  applySidebar(readSidebar());
  applyZen(readZen());
})();
