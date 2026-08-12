function normalize(value) {
  return String(value || "")
    .normalize("NFKD")
    .replace(/\p{Diacritic}/gu, "")
    .toLocaleLowerCase("de-DE")
    .trim();
}

function statusGroup(value) {
  const status = normalize(value);
  if (["eingeladen", "beigetreten", "bereit"].includes(status)) {
    return "lobby";
  }

  if (status === "lauft") {
    return "running";
  }

  if (status === "fertig") {
    return "finished";
  }

  return "issues";
}

export function attachArenaRosterControls() {
  document.querySelectorAll("[data-arena-room]").forEach((root) => {
    const body = root.querySelector("[data-arena-participants]");
    const search = root.querySelector("[data-arena-roster-search]");
    const filter = root.querySelector("[data-arena-roster-filter]");
    const expand = root.querySelector("[data-arena-roster-expand]");
    const status = root.querySelector("[data-arena-roster-status]");
    if (!body || (!search && !filter && !expand)) {
      return;
    }

    let scheduled = false;

    const renderStatus = (visible, total) => {
      if (!status) {
        return;
      }

      const next = total === 0
        ? "Noch keine Teilnehmenden sichtbar."
        : `${visible} von ${total} ${total === 1 ? "Person" : "Personen"} angezeigt.`;
      if (status.textContent !== next) {
        status.textContent = next;
      }
    };

    const applyFilters = () => {
      scheduled = false;
      const query = normalize(search?.value);
      const selectedStatus = filter?.value || "all";
      const rows = [...body.querySelectorAll("tr[data-participant-id]")];
      let visible = 0;

      rows.forEach((row) => {
        const name = normalize(row.cells[0]?.textContent);
        const group = statusGroup(row.cells[2]?.textContent);
        const matches = (!query || name.includes(query)) &&
          (selectedStatus === "all" || selectedStatus === group);
        row.hidden = !matches;
        visible += matches ? 1 : 0;
      });

      renderStatus(visible, rows.length);
    };

    const scheduleFilters = () => {
      if (scheduled) {
        return;
      }

      scheduled = true;
      window.queueMicrotask(applyFilters);
    };

    const setExpanded = (expanded) => {
      root.dataset.arenaRosterExpanded = String(expanded);
      if (expand) {
        expand.setAttribute("aria-expanded", String(expanded));
        expand.textContent = expanded ? "Fokusansicht" : "Alle anzeigen";
      }

      root.dispatchEvent(new CustomEvent("keywars:arena-roster-display-change", {
        bubbles: true,
        detail: { expanded }
      }));
    };

    const expandForFilter = () => {
      if (root.dataset.arenaRosterExpanded !== "true") {
        setExpanded(true);
      }
      scheduleFilters();
    };

    search?.addEventListener("input", expandForFilter);
    filter?.addEventListener("change", expandForFilter);
    expand?.addEventListener("click", () => {
      const expanded = root.dataset.arenaRosterExpanded !== "true";
      if (!expanded) {
        if (search) {
          search.value = "";
        }
        if (filter) {
          filter.value = "all";
        }
      }
      setExpanded(expanded);
      scheduleFilters();
    });

    new MutationObserver(scheduleFilters).observe(body, { childList: true, subtree: true });
    applyFilters();
  });
}
