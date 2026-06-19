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

attachTypingApps();
attachArenaPages();
attachRoomCodeInputs();
