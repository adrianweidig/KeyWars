export function attachPersonPickers(root = document) {
  root.querySelectorAll("[data-person-picker]").forEach((picker) => attachPersonPicker(picker));
}

function attachPersonPicker(picker) {
  if (picker.dataset.personPickerAttached === "true") {
    return;
  }

  picker.dataset.personPickerAttached = "true";
  const queryInput = picker.querySelector("[data-person-query]");
  const departmentInput = picker.querySelector("[data-person-department]");
  const results = picker.querySelector("[data-person-results]");
  const selection = picker.querySelector("[data-person-selection]");
  const emptySelection = picker.querySelector("[data-person-empty]");
  const moreButton = picker.querySelector("[data-person-more]");
  const status = picker.querySelector("[data-person-status]");
  const inputName = picker.dataset.personInputName || "Input.ParticipantIds";
  const selected = new Map();
  let currentPage = 1;
  let totalPages = 1;
  let searchTimer;
  let searchController;

  if (!results || !selection || !moreButton || !status || !picker.dataset.searchUrl) {
    return;
  }

  const updateEmptyState = () => {
    if (emptySelection) {
      emptySelection.hidden = selected.size > 0;
    }
  };

  const attachRemove = (chip) => {
    chip.querySelector("[data-person-remove]")?.addEventListener("click", () => {
      selected.delete(chip.dataset.personId);
      chip.remove();
      updateEmptyState();
      void search(1);
    });
  };

  selection.querySelectorAll("[data-person-id]").forEach((chip) => {
    selected.set(chip.dataset.personId, {
      id: chip.dataset.personId,
      displayName: chip.dataset.personLabel,
      samAccountName: chip.dataset.personAccount
    });
    attachRemove(chip);
  });
  updateEmptyState();

  const addPerson = (person) => {
    if (!person?.id || selected.has(person.id)) {
      return;
    }

    selected.set(person.id, person);
    const chip = document.createElement("span");
    chip.className = "person-picker-chip";
    chip.dataset.personId = person.id;
    chip.dataset.personLabel = person.displayName || person.label || "Person";
    chip.dataset.personAccount = person.samAccountName || "";

    const label = document.createElement("span");
    label.textContent = person.samAccountName
      ? `${person.displayName || person.label} (${person.samAccountName})`
      : person.displayName || person.label || "Person";
    chip.append(label);

    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "secondary";
    remove.dataset.personRemove = "";
    remove.textContent = "Entfernen";
    remove.setAttribute("aria-label", `${person.displayName || person.label || "Person"} entfernen`);
    chip.append(remove);

    const hidden = document.createElement("input");
    hidden.type = "hidden";
    hidden.name = inputName;
    hidden.value = person.id;
    chip.append(hidden);
    selection.append(chip);
    attachRemove(chip);
    updateEmptyState();
  };

  const renderResults = (items, append) => {
    if (!append) {
      results.replaceChildren();
    }

    for (const person of items) {
      if (!person?.id || !person?.label) {
        continue;
      }

      const button = document.createElement("button");
      button.type = "button";
      button.className = "person-picker-result";
      button.setAttribute("role", "option");
      button.disabled = selected.has(person.id);
      button.textContent = person.department ? `${person.label} · ${person.department}` : person.label;
      button.addEventListener("click", () => {
        addPerson(person);
        button.disabled = true;
      });
      results.append(button);
    }
  };

  const search = async (page) => {
    searchController?.abort();
    searchController = new AbortController();
    const url = new URL(picker.dataset.searchUrl, window.location.origin);
    const query = queryInput?.value.trim() || "";
    const department = departmentInput?.value || "";
    url.searchParams.set("q", query);
    url.searchParams.set("page", String(page));
    url.searchParams.set("pageSize", "10");
    if (picker.dataset.personPurpose) {
      url.searchParams.set("purpose", picker.dataset.personPurpose);
    }
    if (department) {
      url.searchParams.set("department", department);
    }

    moreButton.disabled = true;
    status.textContent = "Personen werden geladen …";
    try {
      const response = await fetch(url, {
        headers: { Accept: "application/json" },
        signal: searchController.signal
      });
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      const pageResult = await response.json();
      if (!pageResult || !Array.isArray(pageResult.items) ||
          !Number.isInteger(pageResult.page) || !Number.isInteger(pageResult.totalPages) ||
          !Number.isInteger(pageResult.totalCount)) {
        throw new Error("Ungültige Suchantwort");
      }

      currentPage = pageResult.page;
      totalPages = pageResult.totalPages;
      renderResults(pageResult.items, currentPage > 1);
      moreButton.hidden = currentPage >= totalPages;
      status.textContent = pageResult.totalCount === 0
        ? "Keine passenden Personen gefunden."
        : `${pageResult.totalCount} Personen gefunden · Seite ${currentPage} von ${totalPages}`;
    } catch (error) {
      if (error.name === "AbortError") {
        return;
      }

      moreButton.hidden = true;
      status.textContent = "Die Personensuche ist gerade nicht erreichbar. Bitte versuche es erneut.";
    } finally {
      moreButton.disabled = false;
    }
  };

  const scheduleSearch = () => {
    window.clearTimeout(searchTimer);
    searchTimer = window.setTimeout(() => void search(1), 250);
  };

  queryInput?.addEventListener("input", scheduleSearch);
  departmentInput?.addEventListener("change", () => void search(1));
  moreButton.addEventListener("click", () => {
    if (currentPage < totalPages) {
      void search(currentPage + 1);
    }
  });
  void search(1);
}
