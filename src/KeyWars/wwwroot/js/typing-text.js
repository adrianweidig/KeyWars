export function normalizeTypingText(value) {
  return String(value || "")
    .replace(/\r\n/g, "\n")
    .replace(/\r/g, "\n")
    .normalize("NFC");
}

export function splitGraphemes(value) {
  const normalized = normalizeTypingText(value);
  if (window.Intl && typeof window.Intl.Segmenter === "function") {
    const segmenter = new window.Intl.Segmenter("de", { granularity: "grapheme" });
    return Array.from(segmenter.segment(normalized), (segment) => segment.segment);
  }

  return Array.from(normalized);
}

export function renderTypingCharacters(container, expected, classForIndex) {
  const nodes = [];
  expected.forEach((char, index) => {
    const span = document.createElement("span");
    const stateClass = classForIndex(char, index);
    if (stateClass) {
      span.className = stateClass;
    }

    if (char === "\n") {
      span.textContent = "\u21b5";
      span.classList.add("typing-newline");
      span.title = "Absatz: Enter drücken";
      span.setAttribute("aria-label", "Absatz: Enter drücken");
      nodes.push(span, document.createElement("br"));
      return;
    }

    span.textContent = char;
    nodes.push(span);
  });
  container.replaceChildren(...nodes);
}
