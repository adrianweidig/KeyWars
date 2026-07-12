const pendingContainers = new Set();
let pendingFrame = 0;

export function scrollCurrentCharacterIntoView(container) {
  pendingContainers.add(container);
  if (pendingFrame) {
    return;
  }

  pendingFrame = window.requestAnimationFrame(() => {
    pendingFrame = 0;
    const documentScrollX = window.scrollX;
    const documentScrollY = window.scrollY;
    const containers = [...pendingContainers];
    pendingContainers.clear();
    containers.forEach((pendingContainer) => {
      if (pendingContainer.isConnected) {
        alignCurrentCharacterInView(pendingContainer);
      }
    });

    if (window.scrollX !== documentScrollX || window.scrollY !== documentScrollY) {
      window.scrollTo(documentScrollX, documentScrollY);
    }
  });
}

export function resetTypingScroll(container) {
  pendingContainers.delete(container);
  if (pendingFrame && pendingContainers.size === 0) {
    window.cancelAnimationFrame(pendingFrame);
    pendingFrame = 0;
  }

  container.scrollTop = 0;
}

function alignCurrentCharacterInView(container) {
  const current = container.querySelector(".current");
  if (!current || container.scrollHeight <= container.clientHeight) {
    return;
  }

  const containerBounds = container.getBoundingClientRect();
  const currentBounds = current.getBoundingClientRect();
  const margin = Math.min(36, Math.max(12, container.clientHeight * 0.18));

  if (currentBounds.top < containerBounds.top + margin) {
    container.scrollTop -= (containerBounds.top + margin) - currentBounds.top;
  } else if (currentBounds.bottom > containerBounds.bottom - margin) {
    container.scrollTop += currentBounds.bottom - (containerBounds.bottom - margin);
  }
}
