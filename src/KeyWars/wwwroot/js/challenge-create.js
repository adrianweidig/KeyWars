import { attachPersonPickers } from "./person-picker.js";

attachPersonPickers();

document.querySelectorAll("[data-challenge-create]").forEach((form) => {
  const modeInput = form.querySelector("[data-challenge-mode]");
  const roundsField = form.querySelector("[data-challenge-rounds]");
  const roundsInput = roundsField?.querySelector("select");

  const updateRounds = () => {
    if (!modeInput || !roundsField || !roundsInput) {
      return;
    }

    const bestOf = modeInput.value === "BestOf";
    roundsField.hidden = !bestOf;
    if (!bestOf) {
      roundsInput.value = "1";
    } else if (roundsInput.value !== "3" && roundsInput.value !== "5") {
      roundsInput.value = "3";
    }
  };

  modeInput?.addEventListener("change", updateRounds);
  updateRounds();
});
