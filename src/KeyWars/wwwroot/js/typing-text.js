let graphemeSegmenter;
const renderedCharacters = new WeakMap();

export function normalizeTypingText(value) {
  return String(value || "")
    .replace(/\r\n/g, "\n")
    .replace(/\r/g, "\n")
    .normalize("NFC");
}

export function splitGraphemes(value) {
  const normalized = normalizeTypingText(value);
  if (window.Intl && typeof window.Intl.Segmenter === "function") {
    graphemeSegmenter ||= new window.Intl.Segmenter("de", { granularity: "grapheme" });
    return Array.from(graphemeSegmenter.segment(normalized), (segment) => segment.segment);
  }

  return Array.from(normalized);
}

export function buildTypingInputDelta(previousElements, nextValue, baseRevision, resync = false) {
  const nextElements = splitGraphemes(nextValue);
  const revision = baseRevision + 1;
  if (resync) {
    return {
      elements: nextElements,
      payload: {
        baseRevision,
        revision,
        backspaceCount: 0,
        appendText: "",
        resyncInput: nextElements.join("")
      }
    };
  }

  let retained = 0;
  const maximumRetained = Math.min(previousElements.length, nextElements.length);
  while (retained < maximumRetained && previousElements[retained] === nextElements[retained]) {
    retained += 1;
  }

  return {
    elements: nextElements,
    payload: {
      baseRevision,
      revision,
      backspaceCount: previousElements.length - retained,
      appendText: nextElements.slice(retained).join("")
    }
  };
}

export function renderTypingCharacters(container, expected, classForIndex, options = {}) {
  let state = renderedCharacters.get(container);
  const cacheAttached = state && (state.characterNodes.length === 0
    ? container.childNodes.length === 0
    : state.characterNodes[0].parentNode === container &&
      state.characterNodes[state.characterNodes.length - 1].parentNode === container);
  const expectedChanged = !cacheAttached || (options.rebuild !== false && !sameCharacters(state.expected, expected));
  if (expectedChanged || options.rebuild === true) {
    const nodes = [];
    const characterNodes = [];
    expected.forEach((char) => {
      const span = document.createElement("span");
      if (char === "\n") {
        span.textContent = "\u21b5";
        span.title = "Absatz: Enter drücken";
        span.setAttribute("aria-label", "Absatz: Enter drücken");
        nodes.push(span, document.createElement("br"));
      } else {
        span.textContent = char;
        nodes.push(span);
      }

      characterNodes.push(span);
    });
    container.replaceChildren(...nodes);
    state = { expected: expected.slice(), characterNodes };
    renderedCharacters.set(container, state);
  }

  const startIndex = expectedChanged || options.rebuild === true
    ? 0
    : Math.max(0, Math.min(expected.length, Number(options.startIndex) || 0));
  const endIndex = expectedChanged || options.rebuild === true
    ? expected.length
    : Math.max(startIndex, Math.min(
      expected.length,
      options.endIndex === undefined ? expected.length : Number(options.endIndex) || 0));

  for (let index = startIndex; index < endIndex; index += 1) {
    const char = expected[index];
    const stateClass = classForIndex(char, index) || "";
    const className = char === "\n"
      ? `${stateClass} typing-newline`.trim()
      : stateClass;
    const span = state.characterNodes[index];
    if (span.className !== className) {
      span.className = className;
    }
  }
}

function sameCharacters(left, right) {
  if (left.length !== right.length) {
    return false;
  }

  for (let index = 0; index < left.length; index += 1) {
    if (left[index] !== right[index]) {
      return false;
    }
  }

  return true;
}
