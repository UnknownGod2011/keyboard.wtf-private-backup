(() => {
  if (globalThis.__keyboardWtfBrowserHelperV2) return;
  globalThis.__keyboardWtfBrowserHelperV2 = true;

const VERSION = "2.1.21";
  const MAX_CONTROLS = 350;
  const MAX_PAGE_TEXT = 14000;
  const controlSelectors = [
    "a[href]",
    "button",
    "input:not([type='hidden'])",
    "textarea",
    "select",
    "summary",
    "[contenteditable='true']",
    "[role='button']",
    "[role='link']",
    "[role='tab']",
    "[role='checkbox']",
    "[role='radio']",
    "[role='combobox']",
    "[role='listbox']",
    "[role='textbox']",
    "[role='menuitem']",
    "[role='option']",
    "[tabindex]:not([tabindex='-1'])"
  ].join(",");

  let activeTaskId = "";
  let graphVersion = 0;
  let domEpoch = `${Math.round(performance.timeOrigin)}:${location.href}`;
  let controlsById = new Map();
  let lastSnapshot = null;
  const fieldIds = new WeakMap();
  let nextFieldId = 0;
  let fieldFrameId = 0;
  let prepared = null;
  let indicator = null;
  let highlight = null;
  const bridgeHeartbeatTimer = setInterval(() => {
    chrome.runtime.sendMessage({
      type: "helper_content_heartbeat",
      version: VERSION
    }).catch(() => {});
  }, 2500);
  chrome.runtime.sendMessage({
    type: "helper_content_heartbeat",
    version: VERSION
  }).catch(() => {});

  chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    Promise.resolve(handleMessage(message))
      .then(sendResponse)
      .catch(error => sendResponse({
        ok: false,
        error: String(error?.message || error)
      }));
    return true;
  });

  async function handleMessage(message) {
    switch (message?.type) {
      case "helper_status":
        return {
          ok: true,
          version: VERSION,
          graphVersion,
          domEpoch,
          controlCount: controlsById.size,
          activeTaskId
        };
      case "begin_task":
        activeTaskId = message.taskId || "";
        showIndicator(message.action || "Jarvis is controlling this tab");
        return { ok: true, version: VERSION };
      case "scan":
        requireTask(message.taskId);
        return scan(message.taskId, Number(message.frameId || 0));
      case "prepare_action":
        return prepareAction(message.command);
      case "commit_action":
        return commitAction(message.command, message.resolutionToken);
      case "end_task":
        if (!message.taskId || message.taskId === activeTaskId) cleanup();
        return { ok: true };
      default:
        return { ok: false, error: "Unsupported Chrome Helper message." };
    }
  }

  function scan(taskId, frameId = fieldFrameId) {
    requireTask(taskId);
    fieldFrameId = frameId;
    graphVersion += 1;
    controlsById = new Map();

    const candidates = [...document.querySelectorAll(controlSelectors)]
      .map(element => ({ element, rect: element.getBoundingClientRect() }))
      .filter(({ element, rect }) => isUsefulControl(element, rect))
      .sort((a, b) => {
        const aViewport = inViewport(a.rect) ? 0 : 1;
        const bViewport = inViewport(b.rect) ? 0 : 1;
        if (aViewport !== bViewport) return aViewport - bViewport;
        return (a.rect.width * a.rect.height) - (b.rect.width * b.rect.height);
      })
      .slice(0, MAX_CONTROLS);

    const controls = candidates.map(({ element, rect }, index) => {
      const id = `g${graphVersion}c${index + 1}`;
      controlsById.set(id, element);
      return describeControl(id, element, rect, frameId);
    });

    const pageText = truncate(visibleText(document.body), MAX_PAGE_TEXT);
    const fingerprint = hashString(
      `${location.href}|${document.title}|${pageText.slice(0, 3000)}|`
      + controls.map(control =>
        `${control.role}:${control.accessibleName || control.text}:${control.selected}:${control.checked}`)
        .join("|"));
    const activeEditor = activeEditableElement();
    const activeEditorId = activeEditor
      ? [...controlsById.entries()].find(([, element]) => element === activeEditor)?.[0] || ""
      : "";
    const activeIdentity = activeEditor ? semanticIdentity(activeEditor) : { role: "", name: "" };
    lastSnapshot = {
      taskId,
      windowId: 0,
      tabId: 0,
      url: location.href,
      title: document.title,
      graphVersion,
      domEpoch,
      fingerprint,
      pageText,
      selectedText: truncate(String(getSelection()?.toString() || ""), 3000),
      activeEditable: Boolean(activeEditor),
      activeEditorId,
      activeEditorRole: activeIdentity.role,
      activeEditorName: activeIdentity.name,
      frameId,
      controls
    };
    return lastSnapshot;
  }

  function prepareAction(command) {
    requireTask(command?.taskId);
    updateIndicator(actionLabel(command));
    if (command?.type === "focus_active_cursor" || command?.type === "verify_active_text") {
      return prepareActiveCursor(command);
    }
    let element = controlsById.get(command.controlId);
    if (!element || !element.isConnected || !semanticMatch(element, command)) {
      scan(command.taskId);
      element = findSemanticControl(command);
    }
    if (!element) {
      return {
        ok: false,
        stale: true,
        error: "The target control is no longer present in the live page."
      };
    }
    if (isRestrictedInput(element)
        && command?.type !== "focus") {
      return {
        ok: false,
        error: "Passwords and file uploads require direct user action."
      };
    }

    element.scrollIntoView({ block: "center", inline: "center", behavior: "instant" });
    const rect = element.getBoundingClientRect();
    if (!isVisible(element, rect)) {
      return { ok: false, error: "The target control is not visible after scrolling." };
    }

    const identity = semanticIdentity(element);
    const token = crypto.randomUUID();
    prepared = {
      token,
      taskId: command.taskId,
      element,
      identity,
      before: captureElementState(element),
      beforeFingerprint: pageFingerprint(),
      beforeUrl: location.href,
      rect
    };
    drawHighlight(rect);
    return {
      ok: true,
      resolutionToken: token,
      targetId: command.controlId || "",
      targetRole: identity.role,
      targetName: identity.name,
      bounds: rectangle(rect)
    };
  }

  function prepareActiveCursor(command) {
    const element = activeEditableElement();
    if (!element) {
      return {
        ok: false,
        stale: true,
        error: "No verified editable cursor is active in this tab."
      };
    }
    const rect = element.getBoundingClientRect();
    const visible = isVisible(element, rect);
    // Canvas-backed editors often use a hidden textarea as the active native
    // input proxy. It is safe to preserve that cursor only when it is already
    // the document's focused editable element; we never discover it by
    // clicking an invisible target.
    if (!visible && !document.hasFocus()) {
      return {
        ok: false,
        stale: true,
        error: "The active editor is not ready to receive native keyboard input."
      };
    }
    const identity = semanticIdentity(element);
    const token = crypto.randomUUID();
    prepared = {
      token,
      taskId: command.taskId,
      element,
      identity,
      before: captureElementState(element),
      beforeFingerprint: pageFingerprint(),
      beforeUrl: location.href,
      rect
    };
    if (visible) drawHighlight(rect);
    return {
      ok: true,
      resolutionToken: token,
      targetId: command.controlId || "",
      targetRole: identity.role,
      targetName: identity.name,
      bounds: rectangle(rect)
    };
  }

  async function commitAction(command, token) {
    requireTask(command?.taskId);
    if (!prepared
      || prepared.token !== token
      || prepared.taskId !== command.taskId
      || !prepared.element?.isConnected
      || !sameIdentity(prepared.identity, semanticIdentity(prepared.element))) {
      clearHighlight();
      return {
        commandId: command.commandId || "",
        taskId: command.taskId || "",
        ok: false,
        stale: true,
        error: "The live target changed before the action could run.",
        action: command.type || ""
      };
    }

    const target = prepared.element;
    const targetIdentity = prepared.identity;
    const before = prepared.before;
    const beforeFingerprint = prepared.beforeFingerprint;
    const beforeUrl = prepared.beforeUrl;
    let targetBounds = rectangle(target.getBoundingClientRect());
    try {
      let liveRect = target.getBoundingClientRect();
      if (!isVisible(target, liveRect)) {
        return {
          commandId: command.commandId || "",
          taskId: command.taskId || "",
          ok: false,
          stale: true,
          action: command.type || "",
          error: "The live target moved out of view before the action could run.",
          graphVersion,
          domEpoch,
          targetBounds: rectangle(liveRect)
        };
      }
      drawHighlight(liveRect);
      await delay(110);

      requireTask(command?.taskId);
      if (!target.isConnected
        || !sameIdentity(targetIdentity, semanticIdentity(target))) {
        return {
          commandId: command.commandId || "",
          taskId: command.taskId || "",
          ok: false,
          stale: true,
          action: command.type || "",
          error: "The live target changed while the action highlight was displayed.",
          graphVersion,
          domEpoch,
          targetBounds: rectangle(liveRect)
        };
      }

      // Responsive pages can reflow between prepare_action and commit_action.
      // Re-read the exact same live element immediately before mutating it so
      // the visible box, receipt bounds, and action never use stale geometry.
      liveRect = target.getBoundingClientRect();
      if (!isVisible(target, liveRect)) {
        return {
          commandId: command.commandId || "",
          taskId: command.taskId || "",
          ok: false,
          stale: true,
          action: command.type || "",
          error: "The live target moved out of view before the action could run.",
          graphVersion,
          domEpoch,
          targetBounds: rectangle(liveRect)
        };
      }
      targetBounds = rectangle(liveRect);
      drawHighlight(liveRect);
      await performAction(target, command);
      await delay(command.type === "click" ? 450 : 180);
      const afterState = target.isConnected ? captureElementState(target) : null;
      const afterFingerprint = pageFingerprint();
      const afterUrl = location.href;
      const changed = beforeFingerprint !== afterFingerprint
        || beforeUrl !== afterUrl
        || (afterState && before.valueSignature !== afterState.valueSignature)
        || (afterState && before.selected !== afterState.selected)
        || (afterState && before.checked !== afterState.checked);
      graphVersion += 1;
      return {
        commandId: command.commandId || "",
        taskId: command.taskId || "",
        ok: true,
        action: command.type || "",
        targetId: command.controlId || "",
        targetRole: targetIdentity.role,
        targetName: targetIdentity.name,
        targetFieldId: targetIdentity.fieldId,
        beforeUrl,
        afterUrl,
        beforeFingerprint,
        afterFingerprint,
        valueMatches: command.type === "type" || command.type === "select_option"
          ? valueMatches(
              target,
              command.type === "select_option"
                ? (command.option || command.text || "")
                : (command.text || ""))
          : command.type === "verify_active_text"
            ? valueContains(target, command.text || "")
          : false,
        selectedChanged: Boolean(afterState && (
          before.selected !== afterState.selected
          || (command.type === "select_option"
            && before.valueSignature !== afterState.valueSignature))),
        checkedChanged: Boolean(afterState && before.checked !== afterState.checked),
        navigation: beforeUrl !== afterUrl,
        noVisibleChange: !changed,
        graphVersion,
        domEpoch,
        targetBounds
      };
    } catch (error) {
      return {
        commandId: command.commandId || "",
        taskId: command.taskId || "",
        ok: false,
        action: command.type || "",
        targetId: command.controlId || "",
        targetRole: targetIdentity.role,
        targetName: targetIdentity.name,
        beforeUrl,
        afterUrl: location.href,
        beforeFingerprint,
        afterFingerprint: pageFingerprint(),
        error: String(error?.message || error),
        graphVersion,
        domEpoch,
        targetBounds
      };
    } finally {
      prepared = null;
      clearHighlight();
    }
  }

  async function performAction(element, command) {
    switch (command.type) {
      case "click":
        activateElement(element);
        return;
      case "focus":
        element.focus({ preventScroll: true });
        return;
      case "focus_active_cursor":
        element.focus({ preventScroll: true });
        return;
      case "verify_active_text":
        return;
      case "type":
        setEditableValue(element, command.text || "", Boolean(command.replace));
        return;
      case "press_key":
        pressKey(element, command.key || "Enter", command);
        return;
      case "select_option":
        selectOption(element, command.option || command.text || "");
        return;
      case "check":
        if (!element.checked) element.click();
        return;
      case "uncheck":
        if (element.checked) element.click();
        return;
      case "scroll":
        window.scrollBy({ top: Number(command.text || 600), behavior: "smooth" });
        return;
      default:
        throw new Error(`Unsupported page action: ${command.type}`);
    }
  }

  function activateElement(element) {
    element.focus({ preventScroll: true });
    if (element.matches("a[href],button,input,textarea,select,summary")) {
      element.click();
      return;
    }

    // Custom ARIA widgets commonly listen for the pointer/mouse sequence
    // rather than HTMLElement.click(). Dispatch the same ordered events a
    // user click produces, grounded to the already prepared live element.
    const rect = element.getBoundingClientRect();
    const clientX = rect.left + (rect.width / 2);
    const clientY = rect.top + (rect.height / 2);
    if (typeof PointerEvent === "function") {
      element.dispatchEvent(new PointerEvent("pointerdown", {
        bubbles: true,
        cancelable: true,
        composed: true,
        pointerId: 1,
        pointerType: "mouse",
        isPrimary: true,
        button: 0,
        buttons: 1,
        clientX,
        clientY
      }));
    }
    element.dispatchEvent(new MouseEvent("mousedown", {
      bubbles: true,
      cancelable: true,
      composed: true,
      button: 0,
      buttons: 1,
      clientX,
      clientY
    }));
    if (typeof PointerEvent === "function") {
      element.dispatchEvent(new PointerEvent("pointerup", {
        bubbles: true,
        cancelable: true,
        composed: true,
        pointerId: 1,
        pointerType: "mouse",
        isPrimary: true,
        button: 0,
        buttons: 0,
        clientX,
        clientY
      }));
    }
    element.dispatchEvent(new MouseEvent("mouseup", {
      bubbles: true,
      cancelable: true,
      composed: true,
      button: 0,
      buttons: 0,
      clientX,
      clientY
    }));
    element.dispatchEvent(new MouseEvent("click", {
      bubbles: true,
      cancelable: true,
      composed: true,
      button: 0,
      buttons: 0,
      clientX,
      clientY,
      detail: 1
    }));
  }

  function setEditableValue(element, text, replace) {
    element.focus({ preventScroll: true });
    if (element.isContentEditable) {
      if (replace) document.execCommand("selectAll", false);
      const inserted = document.execCommand("insertText", false, text);
      if (!inserted) {
        if (replace) element.textContent = "";
        element.textContent = `${element.textContent || ""}${text}`;
        element.dispatchEvent(new InputEvent("input", {
          bubbles: true,
          inputType: "insertText",
          data: text
        }));
      }
      return;
    }

    if (!("value" in element)) throw new Error("The target is not editable.");
    text = normalizeNativeInputValue(element, text);
    const previous = element.value || "";
    const value = replace ? text : `${previous}${text}`;
    const prototype = element instanceof HTMLTextAreaElement
      ? HTMLTextAreaElement.prototype
      : HTMLInputElement.prototype;
    const setter = Object.getOwnPropertyDescriptor(prototype, "value")?.set;
    if (setter) setter.call(element, value);
    else element.value = value;
    element.dispatchEvent(new InputEvent("input", {
      bubbles: true,
      inputType: replace ? "insertReplacementText" : "insertText",
      data: text
    }));
    element.dispatchEvent(new Event("change", { bubbles: true }));
  }

  function normalizeNativeInputValue(element, text) {
    if (!(element instanceof HTMLInputElement)
        || (element.type || "").toLowerCase() !== "date") {
      return text;
    }

    const value = `${text || ""}`.trim();
    if (/^\d{4}-\d{2}-\d{2}$/.test(value)) return value;

    // Native date inputs accept ISO values only. Support explicit month-name
    // dates as a defensive executor boundary, but never guess ambiguous
    // numeric dates such as 07/08/2026.
    const monthNames = {
      january: 1, jan: 1,
      february: 2, feb: 2,
      march: 3, mar: 3,
      april: 4, apr: 4,
      may: 5,
      june: 6, jun: 6,
      july: 7, jul: 7,
      august: 8, aug: 8,
      september: 9, sep: 9, sept: 9,
      october: 10, oct: 10,
      november: 11, nov: 11,
      december: 12, dec: 12
    };
    const normalized = value.replace(/,/g, " ").replace(/\s+/g, " ").trim();
    let match = normalized.match(/^([A-Za-z]+)\s+(\d{1,2})\s+(\d{4})$/);
    let monthName;
    let day;
    let year;
    if (match) {
      [, monthName, day, year] = match;
    } else {
      match = normalized.match(/^(\d{1,2})\s+([A-Za-z]+)\s+(\d{4})$/);
      if (match) [, day, monthName, year] = match;
    }
    const month = monthNames[(monthName || "").toLowerCase()];
    const dayNumber = Number(day);
    const yearNumber = Number(year);
    const candidate = month
      ? new Date(Date.UTC(yearNumber, month - 1, dayNumber))
      : null;
    if (!candidate
        || candidate.getUTCFullYear() !== yearNumber
        || candidate.getUTCMonth() !== month - 1
        || candidate.getUTCDate() !== dayNumber) {
      throw new Error("Native date fields require an explicit date such as 2026-07-29 or July 29 2026.");
    }
    return `${yearNumber.toString().padStart(4, "0")}-${month.toString().padStart(2, "0")}-${dayNumber.toString().padStart(2, "0")}`;
  }

  function pressKey(element, key, command) {
    element.focus({ preventScroll: true });
    const options = {
      key,
      code: key === "Enter" ? "Enter" : key,
      bubbles: true,
      cancelable: true
    };
    element.dispatchEvent(new KeyboardEvent("keydown", options));
    if (key === "Enter" && !element.isContentEditable) {
      if (element.form && typeof element.form.requestSubmit === "function") {
        element.form.requestSubmit();
      } else if (/search/i.test(
        `${command?.targetName || ""} ${element.getAttribute("role") || ""} ${element.getAttribute("placeholder") || ""}`)) {
        const root = element.closest("form,[role='search']") || element.parentElement;
        const candidates = [...(root || document).querySelectorAll(
          "button,input[type='submit'],[role='button']"
        )].filter(candidate => {
          const name = `${accessibleName(candidate)} ${visibleText(candidate)}`;
          return isVisible(candidate, candidate.getBoundingClientRect())
            && /search|go|submit/i.test(name);
        });
        if (candidates.length === 1) candidates[0].click();
      }
    }
    element.dispatchEvent(new KeyboardEvent("keyup", options));
  }

  function selectOption(element, requested) {
    if (!(element instanceof HTMLSelectElement)) {
      throw new Error("The target is not a native select control.");
    }
    const normalized = normalize(requested);
    const option = [...element.options].find(item =>
      normalize(item.textContent) === normalized
      || normalize(item.value) === normalized
      || normalize(item.textContent).includes(normalized));
    if (!option) throw new Error("The requested option was not found.");
    element.value = option.value;
    element.dispatchEvent(new Event("input", { bubbles: true }));
    element.dispatchEvent(new Event("change", { bubbles: true }));
  }

  function findSemanticControl(command) {
    const candidates = [...document.querySelectorAll(controlSelectors)]
      .map(element => ({ element, score: semanticScore(element, command) }))
      .filter(candidate => candidate.score > 0 && isVisible(
        candidate.element,
        candidate.element.getBoundingClientRect()))
      .sort((a, b) => b.score - a.score);
    if (!candidates.length || candidates[0].score < 45) return null;
    const wantedName = normalize(command.targetName);
    const topScore = candidates[0].score;
    const tied = candidates.filter(candidate => candidate.score === topScore);
    const hasExactIdentity = tied.some(candidate =>
      semanticIdentity(candidate.element).name === wantedName);
    if (wantedName
      && tied.length > 1
      && !hasExactIdentity
      && /^(?:\d{1,2}|next|previous|done|more)$/.test(wantedName)) {
      // A stale short label such as a calendar day is unsafe when several
      // live controls match. The planner must use the full accessible
      // identity, a fresh graph ID, or bounded visual recovery.
      return null;
    }
    return candidates[0].element;
  }

  function semanticScore(element, command) {
    if (command.targetFieldId && formField(element).fieldId !== command.targetFieldId) return -1000;
    const identity = semanticIdentity(element);
    const wantedName = normalize(command.targetName);
    const wantedRole = normalize(command.targetRole);
    let score = 0;
    if (wantedRole && identity.role === wantedRole) score += 30;
    if (wantedName) {
      if (identity.name === wantedName) score += 80;
      else if (identity.name.startsWith(wantedName) || wantedName.startsWith(identity.name)) score += 45;
      else if (identity.name.includes(wantedName) || wantedName.includes(identity.name)) score += 25;
    }
    if (element.matches("button,input,textarea,select,a,[role='button'],[role='tab'],[contenteditable='true']")) {
      score += 12;
    }
    const rect = element.getBoundingClientRect();
    if (rect.width * rect.height > 180000) score -= 40;
    if (element.disabled || element.getAttribute("aria-disabled") === "true") score -= 100;
    return score;
  }

  function semanticMatch(element, command) {
    if (command.targetFieldId && formField(element).fieldId !== command.targetFieldId) return false;
    if (!command.targetName && !command.targetRole) return true;
    return semanticScore(element, command) >= 45;
  }

  function semanticIdentity(element) {
    return {
      role: normalize(roleOf(element)),
      name: normalize(accessibleName(element) || visibleText(element)),
      fieldId: formField(element).fieldId || ""
    };
  }

  function sameIdentity(a, b) {
    return a.role === b.role && a.name === b.name && a.fieldId === b.fieldId;
  }

  function describeControl(id, element, rect, frameId = 0) {
    const role = roleOf(element);
    const name = accessibleName(element);
    const type = String(element.getAttribute("type") || "").toLowerCase();
    return {
      id,
      role,
      text: truncate(visibleText(element), 180),
      accessibleName: truncate(name, 180),
      ariaLabel: truncate(element.getAttribute("aria-label") || "", 180),
      title: truncate(element.getAttribute("title") || "", 180),
      placeholder: truncate(element.getAttribute("placeholder") || "", 180),
      inputType: type,
      ...formField(element, frameId),
      value: safeControlValue(element),
      nearbyLabel: truncate(nearbyLabel(element), 180),
      enabled: !element.disabled && element.getAttribute("aria-disabled") !== "true",
      selected: Boolean(element.selected || element.getAttribute("aria-selected") === "true"),
      checked: Boolean(element.checked || element.getAttribute("aria-checked") === "true"),
      editable: isEditable(element),
      visible: isVisible(element, rect),
      frameId,
      bounds: rectangle(rect),
      safeActions: safeActions(element)
    };
  }

  function isUsefulControl(element, rect) {
    if (!isVisible(element, rect)) return false;
    if (element.closest("[aria-hidden='true']")) return false;
    const role = roleOf(element);
    const name = accessibleName(element) || visibleText(element);
    return Boolean(name || isEditable(element) || ["checkbox", "radio", "combobox"].includes(role));
  }

  function isVisible(element, rect) {
    const style = getComputedStyle(element);
    return rect.width >= 2
      && rect.height >= 2
      && style.display !== "none"
      && style.visibility !== "hidden"
      && Number(style.opacity || 1) > 0.02;
  }

  function inViewport(rect) {
    return rect.bottom >= 0
      && rect.right >= 0
      && rect.top <= innerHeight
      && rect.left <= innerWidth;
  }

  function roleOf(element) {
    const explicit = element.getAttribute("role");
    if (explicit) return explicit.toLowerCase();
    if (element instanceof HTMLButtonElement) return "button";
    if (element instanceof HTMLAnchorElement) return "link";
    if (element instanceof HTMLTextAreaElement) return "textbox";
    if (element instanceof HTMLSelectElement) return "combobox";
    if (element.isContentEditable) return "textbox";
    if (element instanceof HTMLInputElement) {
      if (element.type === "checkbox") return "checkbox";
      if (element.type === "radio") return "radio";
      if (["button", "submit", "reset"].includes(element.type)) return "button";
      return "textbox";
    }
    return element.tagName.toLowerCase();
  }

  function accessibleName(element) {
    const labelledBy = element.getAttribute("aria-labelledby");
    const labelledText = labelledBy
      ? labelledBy.split(/\s+/).map(id => referencedLabelText(document.getElementById(id))).join(" ")
      : "";
    const contextualLabel = contextualAccessibleLabel(element);
    return [
      element.getAttribute("aria-label"),
      labelledText,
      element.getAttribute("title"),
      element.getAttribute("alt"),
      element.getAttribute("placeholder"),
      element.labels ? [...element.labels].map(label => visibleText(label)).join(" ") : "",
      contextualLabel,
      visibleText(element)
    ].map(value => String(value || "").trim()).find(Boolean) || "";
  }

  function nearbyLabel(element) {
    const field = formField(element);
    if (field.fieldLabel) return field.fieldLabel;
    if (element.labels?.length) return [...element.labels].map(label => visibleText(label)).join(" ");
    const parentLabel = element.closest("label");
    if (parentLabel) return visibleText(parentLabel);
    const contextualLabel = contextualAccessibleLabel(element);
    if (contextualLabel) return contextualLabel;
    const describedBy = element.getAttribute("aria-describedby");
    if (describedBy) {
      return describedBy.split(/\s+/)
        .map(id => document.getElementById(id)?.innerText || "")
        .join(" ");
    }
    const question = element.closest(
      "fieldset,[role='group'],[role='radiogroup'],[role='listitem'],[data-question-id]"
    );
    if (question) {
      const heading = question.querySelector(
        "legend,[role='heading'],h1,h2,h3,h4,[aria-level]"
      );
      if (heading && !heading.contains(element))
        return visibleText(heading);
    }
    return "";
  }

  // Choice text (for example "4" or "Yes") is not the question identity.
  // Preserve the page's native/ARIA grouping instead of guessing from text.
  function formField(element, frameId = fieldFrameId) {
    const role = roleOf(element);
    if (!["radio", "checkbox", "textbox", "combobox"].includes(role)) return {};
    let owner = element;
    let labelOwner = element;
    let members = [element];
    if (role === "radio" || role === "checkbox") {
      const group = element.closest("[role='radiogroup'],[role='group'],[role='list'],fieldset");
      if (group && group !== element) {
        owner = group;
        labelOwner = group;
        members = [...group.querySelectorAll(role === "radio"
          ? "[role='radio'],input[type='radio']" : "[role='checkbox'],input[type='checkbox']")];
      }
      if (element instanceof HTMLInputElement && role === "radio" && element.name) {
        members = [...document.getElementsByName(element.name)].filter(other =>
          other instanceof HTMLInputElement && other.type === "radio" && other.form === element.form);
        owner = members[0] || element;
      }
      // A native required checkbox is required individually, not satisfied by
      // some other checked box in the surrounding question.
      if (element instanceof HTMLInputElement && role === "checkbox" && element.required) {
        owner = element;
        members = [element];
      }
    }
    const question = element.closest("fieldset,[role='listitem'],[data-question-id]");
    const labelSource = labelOwner !== element ? labelOwner : question || element;
    const labelledBy = labelSource.getAttribute("aria-labelledby") || "";
    const groupLabel = labelSource.getAttribute("aria-label")
      || labelledBy.split(/\s+/).filter(Boolean).map(id => referencedLabelText(document.getElementById(id))).join(" ")
      || visibleText(labelSource.querySelector("legend,[role='heading'],h1,h2,h3,h4"));
    const fieldLabel = groupLabel || accessibleName(element);
    if (!fieldIds.has(owner)) fieldIds.set(owner, `field${++nextFieldId}`);
    const declaresRequired = member => member.required === true || member.getAttribute("aria-required") === "true";
    const required = declaresRequired(owner) || members.some(declaresRequired)
      ? true : typeof owner.required === "boolean" || owner.getAttribute("aria-required") === "false"
        ? false : null; // Missing ARIA metadata is unknown, not permission to skip.
    const answered = role === "radio" || role === "checkbox"
      ? members.some(member => member.checked === true || member.getAttribute("aria-checked") === "true")
      : String(safeControlValue(element) || "").trim().length > 0;
    return { fieldId: `${frameId}:${hashString(domEpoch)}:${fieldIds.get(owner)}`, fieldLabel: truncate(fieldLabel, 240),
      fieldRequired: required, fieldAnswered: answered };
  }

  function referencedLabelText(element) {
    if (!element) return "";
    return element.getAttribute("aria-label")
      || element.getAttribute("title")
      || visibleText(element);
  }

  function contextualAccessibleLabel(element) {
    const labelledAncestor = element.closest(
      "[role='gridcell'][aria-label],td[aria-label],[role='option'][aria-label],[role='row'][aria-label]"
    );
    if (!labelledAncestor || labelledAncestor === element) return "";
    return labelledAncestor.getAttribute("aria-label") || "";
  }

  function safeActions(element) {
    const actions = ["focus"];
    if (isRestrictedInput(element)) return actions;
    if (isEditable(element)) actions.push("type", "press_key");
    if (element instanceof HTMLSelectElement) actions.push("select_option");
    if (roleOf(element) === "checkbox") actions.push("check", "uncheck", "click");
    else actions.push("click");
    return [...new Set(actions)];
  }

  function isEditable(element) {
    return element.isContentEditable
      || element instanceof HTMLTextAreaElement
      || (element instanceof HTMLInputElement
        && !["button", "submit", "reset", "checkbox", "radio", "file", "hidden", "password"].includes(element.type));
  }

  function isRestrictedInput(element) {
    if (!(element instanceof HTMLInputElement)) return false;
    return ["password", "file"].includes(
      String(element.getAttribute("type") || "").toLowerCase());
  }

  function visibleText(element) {
    if (!element) return "";
    const text = element.innerText ?? element.textContent ?? "";
    return String(text).replace(/\s+/g, " ").trim();
  }

  function captureElementState(element) {
    return {
      valueSignature: valueSignature(element),
      selected: Boolean(element.selected || element.getAttribute("aria-selected") === "true"),
      checked: Boolean(element.checked || element.getAttribute("aria-checked") === "true")
    };
  }

  function valueSignature(element) {
    if (isRestrictedInput(element)) return "restricted:redacted";
    const value = element.isContentEditable ? element.textContent : element.value;
    return hashString(String(value || ""));
  }

  function valueMatches(element, expected) {
    if (isRestrictedInput(element)) return false;
    const value = element.isContentEditable ? element.textContent : element.value;
    return String(value || "") === String(expected || "");
  }

  function valueContains(element, expected) {
    if (isRestrictedInput(element)) return false;
    const value = element.isContentEditable ? element.textContent : element.value;
    return String(value || "").includes(String(expected || ""));
  }

  function activeEditableElement() {
    const active = document.activeElement;
    if (!active) return null;
    if (isEditable(active)) return active;
    const ancestor = active.closest?.("[contenteditable='true'],[role='textbox']");
    return ancestor && isEditable(ancestor) ? ancestor : null;
  }

  function safeControlValue(element) {
    if (isRestrictedInput(element)) return "";
    const value = element.isContentEditable ? element.textContent : element.value;
    return truncate(String(value || ""), 240);
  }

  function pageFingerprint() {
    const text = visibleText(document.body).slice(0, 5000);
    const states = [...document.querySelectorAll(
      "input:not([type='password']):not([type='file']),textarea,select,[aria-selected],[aria-checked],[contenteditable='true']"
    )].slice(0, 120).map(element => {
      const state = captureElementState(element);
      return `${state.valueSignature}:${state.selected ? 1 : 0}:${state.checked ? 1 : 0}`;
    }).join("|");
    return hashString(`${location.href}|${document.title}|${text}|${states}`);
  }

  function rectangle(rect) {
    return {
      x: Math.round(rect.x * 10) / 10,
      y: Math.round(rect.y * 10) / 10,
      width: Math.round(rect.width * 10) / 10,
      height: Math.round(rect.height * 10) / 10
    };
  }

  function drawHighlight(rect) {
    clearHighlight();
    highlight = document.createElement("div");
    highlight.id = "__keyboard_wtf_target_highlight";
    Object.assign(highlight.style, {
      position: "fixed",
      left: `${rect.x - 3}px`,
      top: `${rect.y - 3}px`,
      width: `${rect.width + 6}px`,
      height: `${rect.height + 6}px`,
      border: "3px solid #f59e0b",
      borderRadius: "7px",
      boxSizing: "border-box",
      pointerEvents: "none",
      zIndex: "2147483647",
      boxShadow: "0 0 0 2px rgba(17,17,17,.35),0 0 16px rgba(245,158,11,.45)",
      transition: "none"
    });
    document.documentElement.appendChild(highlight);
  }

  function clearHighlight() {
    highlight?.remove();
    highlight = null;
  }

  function showIndicator(action) {
    if (!indicator) {
      indicator = document.createElement("div");
      indicator.id = "__keyboard_wtf_control_indicator";
      Object.assign(indicator.style, {
        position: "fixed",
        right: "12px",
        top: "12px",
        maxWidth: "300px",
        padding: "6px 10px",
        borderRadius: "999px",
        background: "rgba(20,20,20,.9)",
        color: "#fff",
        border: "1px solid rgba(245,158,11,.9)",
        font: "600 12px/1.3 system-ui,-apple-system,Segoe UI,sans-serif",
        boxShadow: "0 4px 16px rgba(0,0,0,.25)",
        pointerEvents: "none",
        zIndex: "2147483646",
        whiteSpace: "nowrap",
        overflow: "hidden",
        textOverflow: "ellipsis"
      });
      document.documentElement.appendChild(indicator);
    }
    updateIndicator(action);
  }

  function updateIndicator(action) {
    if (indicator) indicator.textContent = `Jarvis · ${action || "controlling this tab"}`;
  }

  function cleanup() {
    clearInterval(bridgeHeartbeatTimer);
    activeTaskId = "";
    controlsById.clear();
    prepared = null;
    clearHighlight();
    indicator?.remove();
    indicator = null;
  }

  function requireTask(taskId) {
    if (!taskId || !activeTaskId || taskId !== activeTaskId) {
      throw new Error("This tab is not leased to the requested Jarvis browser task.");
    }
  }

  function actionLabel(command) {
    const name = command.targetName || command.targetRole || "control";
    switch (command.type) {
      case "click": return `Clicking ${name}`;
      case "type": return `Filling ${name}`;
      case "focus_active_cursor": return "Using active editor cursor";
      case "verify_active_text": return "Verifying editor text";
      case "press_key": return `Pressing ${command.key || "key"}`;
      case "select_option": return `Selecting ${command.option || name}`;
      default: return `Using ${name}`;
    }
  }

  function normalize(value) {
    return String(value || "").replace(/\s+/g, " ").trim().toLowerCase();
  }

  function truncate(value, length) {
    const text = String(value || "");
    const elements = Array.from(text);
    return elements.length <= length ? text : elements.slice(0, length).join("");
  }

  function hashString(value) {
    let hash = 2166136261;
    for (let index = 0; index < value.length; index += 1) {
      hash ^= value.charCodeAt(index);
      hash = Math.imul(hash, 16777619);
    }
    return (hash >>> 0).toString(16).padStart(8, "0");
  }

  function delay(ms) {
    return new Promise(resolve => setTimeout(resolve, ms));
  }

  addEventListener("pagehide", cleanup, { once: true });
})();
