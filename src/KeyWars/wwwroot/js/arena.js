export function attachArenaPages() {
  document.querySelectorAll("[data-room-snapshot]").forEach(() => {
    const refreshButton = document.querySelector("[data-refresh-room]");
    if (!refreshButton) {
      return;
    }

    refreshButton.addEventListener("click", () => {
      window.location.reload();
    });
  });
}
