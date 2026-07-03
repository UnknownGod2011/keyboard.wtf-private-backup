(() => {
  if (window.__keyboardWtfCompanionLoaded) return;
  window.__keyboardWtfCompanionLoaded = true;

  const MAX_CONTROLS = 260;
  const MAX_TEXT = 180;
  const MAX_SHADOW_ROOTS = 80;
  const CONTENT_SCRIPT_VERSION = chrome?.runtime?.getManifest?.()?.version || "0.1.7";
  const controlIds = new Map();
  let idCounter = 1;
  let graphVersion = 0;
  let lastUrl = location.href;
  let postTimer = 0;
  let controlsById = new Map();
  let observer = null;
  let extensionContextInvalid = false;

  function browserName() {
    const brands = navigator.userAgentData?.brands?.map((brand) => brand.brand).join(" ") || "";
    const ua = `${brands} ${navigator.userAgent || ""}`;
    if (/Edg\//i.test(ua) || /Microsoft Edge/i.test(ua)) return "Edge";
    if (/Chrome|Chromium|Google Chrome/i.test(ua)) return /Chromium/i.test(ua) && !/Chrome/i.test(ua) ? "Chromium" : "Chrome";
    return "Unknown";
  }

  function isKeyboardWtfInternalPage() {
    const host = location.hostname.toLowerCase();
    if (host !== "localhost" && host !== "127.0.0.1") return false;
    const port = Number(location.port || 80);
    if (port < 8090 || port > 8289) return false;
    const path = location.pathname.toLowerCase();
    if (path === "/" || path.startsWith("/api/")) return true;
    if (path.includes("settings") || path.includes("bridge")) return true;
    return false;
  }

  function canUseRuntime() {
    return !extensionContextInvalid
      && typeof chrome !== "undefined"
      && chrome.runtime
      && chrome.runtime.id;
  }

  function isRuntimeInvalidation(error) {
    const message = String(error?.message || error || "");
    return /extension context invalidated|receiving end does not exist|context invalidated|invalid extension context|could not establish connection/i.test(message);
  }

  function stopBackgroundGraphPosting(reason = "") {
    extensionContextInvalid = true;
    if (postTimer) {
      clearTimeout(postTimer);
      postTimer = 0;
    }
    try { observer?.disconnect?.(); } catch { }
    window.__keyboardWtfCompanionInvalidated = reason || "Extension context invalidated.";
  }

  async function safeSendMessage(message) {
    if (!canUseRuntime()) {
      stopBackgroundGraphPosting("Browser Companion was reloaded. Refresh the target tab if controls look stale.");
      return { ok: false, message: "Browser Companion was reloaded. Refresh the target tab if controls look stale." };
    }
    try {
      return await chrome.runtime.sendMessage(message);
    } catch (error) {
      if (isRuntimeInvalidation(error)) {
        stopBackgroundGraphPosting("Browser Companion was reloaded. Refresh the target tab if controls look stale.");
        return { ok: false, message: "Browser Companion was reloaded. Refresh the target tab if controls look stale." };
      }
      return { ok: false, message: error?.message || "Browser Companion message failed." };
    }
  }

  function clean(value, max = MAX_TEXT) {
    const text = String(value || "").replace(/\s+/g, " ").trim();
    return text.length <= max ? text : text.slice(0, max).trim();
  }

  function isVisible(element) {
    if (!element || !(element instanceof Element)) return false;
    const rect = element.getBoundingClientRect();
    if (rect.width < 2 || rect.height < 2) return false;
    const style = getComputedStyle(element);
    if (style.visibility === "hidden" || style.display === "none" || Number(style.opacity) === 0) return false;
    if (rect.bottom < 0 || rect.right < 0 || rect.top > innerHeight + 250 || rect.left > innerWidth + 250) return false;
    return true;
  }

  function rootFor(element) {
    const root = element?.getRootNode?.();
    return root && typeof root.querySelector === "function" ? root : document;
  }

  function parentElementDeep(element) {
    if (!element) return null;
    if (element.parentElement) return element.parentElement;
    const root = element.getRootNode?.();
    return root instanceof ShadowRoot ? root.host : null;
  }

  function closestDeep(element, selector) {
    let current = element;
    let depth = 0;
    while (current && depth++ < 16) {
      if (current.matches?.(selector)) return current;
      current = parentElementDeep(current);
    }
    return null;
  }

  function isInShadowDom(element) {
    return element?.getRootNode?.() instanceof ShadowRoot;
  }

  function roleFor(element) {
    const explicit = element.getAttribute("role");
    if (explicit) return explicit.toLowerCase();
    const tag = element.tagName.toLowerCase();
    const type = (element.getAttribute("type") || "").toLowerCase();
    if (tag === "a") return "link";
    if (tag === "button") return "button";
    if (tag === "textarea") return "textbox";
    if (tag === "select") return "combobox";
    if (tag === "input") {
      if (["button", "submit", "reset", "image"].includes(type)) return "button";
      if (["checkbox"].includes(type)) return "checkbox";
      if (["radio"].includes(type)) return "radio";
      if (["search"].includes(type)) return "searchbox";
      return "textbox";
    }
    if (element.isContentEditable) return "textbox";
    if (/YTD-(RICH-GRID-MEDIA|VIDEO-RENDERER|GRID-VIDEO-RENDERER|COMPACT-VIDEO-RENDERER)/i.test(element.tagName)) return "video-card";
    if (closestDeep(element, "[data-component-type='s-search-result'],[data-testid*='product' i],article[data-card],article")) {
      const href = safeHref(element);
      const text = elementText(element).toLowerCase();
      if (href.includes("/dp/") || text.includes("rating") || text.includes("price")) return "product-card";
    }
    if (closestDeep(element, "form")) return "form-control";
    return "clickable";
  }

  function elementText(element) {
    const tag = element.tagName.toLowerCase();
    if (tag === "input" || tag === "textarea") return clean(element.value || element.getAttribute("value") || "");
    return clean(element.innerText || element.textContent || "");
  }

  function safeHref(element) {
    const href = element.href || element.getAttribute("href") || element.querySelector?.("a[href]")?.href || "";
    if (!href) return "";
    try {
      const url = new URL(href, location.href);
      if (!["http:", "https:"].includes(url.protocol)) return "";
      return clean(url.href, 240);
    } catch {
      return "";
    }
  }

  function cssEscape(value) {
    return CSS.escape ? CSS.escape(value) : String(value).replace(/["\\]/g, "\\$&");
  }

  function nthPath(element) {
    const parts = [];
    let current = element;
    while (current && current.nodeType === Node.ELEMENT_NODE && parts.length < 7) {
      const tag = current.tagName.toLowerCase();
      let index = 1;
      let previous = current.previousElementSibling;
      while (previous) {
        if (previous.tagName === current.tagName) index++;
        previous = previous.previousElementSibling;
      }
      parts.unshift(`${tag}:nth-of-type(${index})`);
      if (current.parentElement) {
        current = current.parentElement;
      } else {
        const root = current.getRootNode?.();
        if (root instanceof ShadowRoot && root.host) {
          parts.unshift(":shadow");
          current = root.host;
        } else {
          current = null;
        }
      }
    }
    return parts.join(">");
  }

  function selectorCandidates(element) {
    const selectors = [];
    const id = element.id;
    if (id && !looksSensitive(id)) selectors.push(`#${cssEscape(id)}`);
    const testId = element.getAttribute("data-testid") || element.getAttribute("data-test-id");
    if (testId && !looksSensitive(testId)) selectors.push(`[data-testid="${cssEscape(testId)}"]`);
    const stableAttrs = ["data-test", "data-cy", "data-qa", "data-a-target", "data-automation-id", "data-component-type"];
    for (const attr of stableAttrs) {
      const value = element.getAttribute(attr);
      if (value && !looksSensitive(value)) selectors.push(`[${attr}="${cssEscape(clean(value, 80))}"]`);
    }
    const aria = element.getAttribute("aria-label");
    if (aria && !looksSensitive(aria)) selectors.push(`[aria-label="${cssEscape(clean(aria, 80))}"]`);
    const name = element.getAttribute("name");
    if (name && !looksSensitive(name)) selectors.push(`${element.tagName.toLowerCase()}[name="${cssEscape(name)}"]`);
    const href = element.getAttribute("href");
    if (href && href.length < 160) selectors.push(`a[href="${cssEscape(href)}"]`);
    const role = element.getAttribute("role");
    if (role && !looksSensitive(role)) selectors.push(`[role="${cssEscape(clean(role, 60))}"]`);
    selectors.push(nthPath(element));
    return [...new Set(selectors)].slice(0, 5);
  }

  function nearbyText(element) {
    const root = rootFor(element);
    const label = element.id ? root.querySelector?.(`label[for="${cssEscape(element.id)}"]`) || document.querySelector(`label[for="${cssEscape(element.id)}"]`) : null;
    const labelledBy = element.getAttribute("aria-labelledby");
    const labelled = labelledBy ? labelledBy.split(/\s+/).map((id) => root.getElementById?.(id)?.innerText || document.getElementById(id)?.innerText).join(" ") : "";
    const parent = closestDeep(element, "label,li,article,section,form,[role='listitem'],[role='option'],[data-component-type='s-search-result'],ytd-rich-grid-media,ytd-video-renderer");
    const previous = element.previousElementSibling?.innerText || "";
    return clean([label?.innerText, labelled, previous, parent?.innerText].filter(Boolean).join(" "), 220);
  }

  function parentSection(element) {
    const section = closestDeep(element, "article,section,main,nav,aside,header,footer,form,li,[role='region'],[role='listitem'],[data-component-type='s-search-result'],ytd-rich-grid-media,ytd-video-renderer,ytd-compact-video-renderer");
    return clean(section?.innerText || "", 240);
  }

  function cardContext(element) {
    const card = closestDeep(element, [
      "article",
      "li",
      "[role='listitem']",
      "[data-component-type='s-search-result']",
      "[data-testid*='card' i]",
      "[data-testid*='product' i]",
      "[class*='card' i]",
      "ytd-rich-grid-media",
      "ytd-video-renderer",
      "ytd-grid-video-renderer",
      "ytd-compact-video-renderer"
    ].join(","));
    return clean(card?.innerText || "", 260);
  }

  function formContext(element) {
    const form = closestDeep(element, "form,[role='form'],[data-testid*='form' i]");
    if (!form) return "";
    return clean(form.getAttribute("aria-label") || form.getAttribute("name") || form.innerText, 180);
  }

  function menuContext(element) {
    const menu = closestDeep(element, "[role='menu'],[role='listbox'],[role='combobox'],[aria-expanded='true'],[class*='menu' i],[class*='dropdown' i]");
    return clean(menu?.getAttribute("aria-label") || menu?.innerText || "", 180);
  }

  function modalContext(element) {
    const modal = closestDeep(element, "dialog,[role='dialog'],[aria-modal='true'],[class*='modal' i],[class*='drawer' i],[class*='overlay' i]");
    return clean(modal?.getAttribute("aria-label") || modal?.innerText || "", 220);
  }

  function tabContext(element) {
    const tab = closestDeep(element, "[role='tablist'],[role='tabpanel'],nav,[aria-selected]");
    return clean(tab?.getAttribute("aria-label") || tab?.innerText || "", 180);
  }

  function repeatingGroup(element) {
    const container = closestDeep(element, "[role='list'],[role='grid'],[role='table'],ul,ol,section,main");
    if (!container) return "";
    const items = container.querySelectorAll?.("article,li,[role='listitem'],[data-component-type='s-search-result'],ytd-rich-grid-media,ytd-video-renderer").length || 0;
    if (items < 2) return "";
    return clean(`${container.tagName?.toLowerCase?.() || "group"} with ${items} repeated item(s): ${container.getAttribute("aria-label") || container.innerText}`, 220);
  }

  function pageRegion(element) {
    if (closestDeep(element, "header,[role='banner'],nav,[role='navigation']")) return "navigation";
    if (closestDeep(element, "aside,[role='complementary']")) return "sidebar";
    if (closestDeep(element, "footer,[role='contentinfo']")) return "footer";
    if (closestDeep(element, "dialog,[role='dialog'],[aria-modal='true']")) return "modal";
    if (closestDeep(element, "form,[role='form']")) return "form";
    if (closestDeep(element, "main,[role='main']")) return "main";
    return "page";
  }

  function positionKind(element) {
    const style = getComputedStyle(element);
    const stickyParent = closestDeep(element, "header,nav,aside,[style*='sticky'],[style*='fixed']");
    if (style.position === "fixed" || style.position === "sticky" || stickyParent) return "sticky-or-fixed";
    return "normal";
  }

  function disabledReason(element) {
    if (element.disabled) return "disabled attribute";
    if (element.getAttribute("aria-disabled") === "true") return "aria-disabled";
    if (element.getAttribute("disabled") != null) return "disabled";
    const style = getComputedStyle(element);
    if (style.pointerEvents === "none") return "pointer-events none";
    const unavailable = clean(`${element.className || ""} ${element.getAttribute("aria-label") || ""} ${element.innerText || ""}`, 240);
    if (/(unavailable|out of stock|sold out|coming soon|notify me|disabled)/i.test(unavailable)) return "unavailable text";
    return "";
  }

  function controlTypeFor(element, role, text, href) {
    const combined = `${role} ${element.tagName} ${text} ${element.getAttribute("aria-label") || ""} ${element.getAttribute("placeholder") || ""} ${href}`.toLowerCase();
    if (role.includes("searchbox") || /search/.test(combined)) return "search";
    if (role.includes("tab") || element.getAttribute("aria-selected") != null) return "tab";
    if (role.includes("menu") || closestDeep(element, "[role='menu'],[role='listbox'],[class*='dropdown' i]")) return "menu-option";
    if (role.includes("checkbox") || role.includes("radio")) return "choice";
    if (role.includes("combobox") || element.tagName.toLowerCase() === "select") return "dropdown";
    if (/filter|sort|oldest|latest|popular|price|rating/.test(combined)) return "filter-or-sort";
    if (/notify me|coming soon|out of stock|sold out|currently unavailable/.test(combined)) return "unavailable-action";
    if (/add to cart|add to basket|basket|cart/.test(combined)) return "cart-action";
    if (/checkout|payment|pay now|place order/.test(combined)) return "protected-final-action";
    if (role.includes("video") || href.includes("/watch")) return "video-card";
    if (href.includes("/dp/") || role.includes("product")) return "product-card";
    if (role.includes("textbox")) return "text-input";
    if (role.includes("button")) return "button";
    if (role.includes("link")) return "link";
    return "control";
  }

  function looksSensitive(value) {
    const text = String(value || "").toLowerCase();
    return /(password|passcode|otp|2fa|captcha|cvv|cvc|card|credit|debit|payment|bank|routing|ssn|aadhaar|pin|secret|token|api[_-]?key)/.test(text);
  }

  function sensitive(element, text) {
    const fields = [
      element.getAttribute("type"),
      element.getAttribute("name"),
      element.getAttribute("id"),
      element.getAttribute("autocomplete"),
      element.getAttribute("aria-label"),
      element.getAttribute("placeholder"),
      text
    ].join(" ");
    return looksSensitive(fields);
  }

  function confidence(element, role, text, aria, href) {
    let score = 0.45;
    if (["button", "link", "tab", "menuitem", "searchbox", "textbox", "combobox", "checkbox", "radio", "video-card"].some((r) => role.includes(r))) score += 0.2;
    if (text || aria) score += 0.18;
    if (href) score += 0.08;
    if (element.id || element.getAttribute("data-testid")) score += 0.06;
    if (cardContext(element)) score += 0.03;
    if (isInShadowDom(element)) score -= 0.02;
    return Math.min(0.98, Number(score.toFixed(2)));
  }

  function signatureFor(control) {
    return clean([
      control.role,
      control.tagName,
      control.text,
      control.ariaLabel,
      control.placeholder,
      control.href,
      control.domPathSignature
    ].join("|"), 500);
  }

  function stableId(control) {
    const signature = signatureFor(control);
    if (!controlIds.has(signature)) controlIds.set(signature, `kc_${idCounter++}`);
    return controlIds.get(signature);
  }

  function candidateElements() {
    const selector = [
      "a[href]",
      "button",
      "input",
      "textarea",
      "select",
      "summary",
      "[role]",
      "[contenteditable='true']",
      "[tabindex]:not([tabindex='-1'])",
      "[onclick]",
      "[aria-label]",
      "[aria-controls]",
      "[aria-expanded]",
      "[aria-selected]",
      "[data-testid]",
      "[data-test]",
      "[data-cy]",
      "[data-qa]",
      "[data-a-target]",
      "[data-automation-id]",
      "[data-component-type]",
      "[jsaction]",
      "[class*='button' i]",
      "[class*='card' i]",
      "[class*='chip' i]",
      "[class*='tab' i]",
      "[class*='filter' i]",
      "[class*='sort' i]",
      "ytd-rich-grid-media",
      "ytd-video-renderer",
      "ytd-grid-video-renderer",
      "ytd-compact-video-renderer",
      "tp-yt-paper-tab",
      "yt-chip-cloud-chip-renderer"
    ].join(",");
    const roots = [document];
    const shadowRoots = [];
    const seenRoots = new Set(roots);
    for (let i = 0; i < roots.length && shadowRoots.length < MAX_SHADOW_ROOTS; i++) {
      const root = roots[i];
      for (const element of Array.from(root.querySelectorAll?.("*") || [])) {
        if (element.shadowRoot && !seenRoots.has(element.shadowRoot)) {
          seenRoots.add(element.shadowRoot);
          roots.push(element.shadowRoot);
          shadowRoots.push(element.shadowRoot);
        }
      }
    }
    const elements = [];
    const seenElements = new Set();
    const addElement = (element) => {
      if (!element || seenElements.has(element)) return;
      seenElements.add(element);
      elements.push(element);
    };
    for (const root of roots) {
      for (const element of Array.from(root.querySelectorAll?.(selector) || [])) {
        addElement(element);
      }
      for (const element of Array.from(root.querySelectorAll?.("body *") || [])) {
        if (looksLikeActionTextElement(element)) addElement(element);
      }
    }
    return { elements, shadowRootsScanned: shadowRoots.length };
  }

  function looksLikeActionTextElement(element) {
    if (!element || element.nodeType !== Node.ELEMENT_NODE) return false;
    const tag = element.tagName?.toLowerCase?.() || "";
    if (["html", "body", "script", "style", "svg", "path", "img", "video", "canvas"].includes(tag)) return false;
    const text = clean(element.innerText || element.textContent || "", 100);
    if (!text || text.length > 90) return false;
    if (looksSensitive(text)) return false;
    const style = getComputedStyle(element);
    if (style.visibility === "hidden" || style.display === "none" || style.pointerEvents === "none") return false;
    const classText = clean(`${element.className || ""} ${element.id || ""} ${element.getAttribute("aria-label") || ""} ${element.getAttribute("title") || ""}`, 160);
    const combined = `${text} ${classText}`.toLowerCase();
    if (/(add to cart|add to basket|go to cart|view cart|buy now|notify me|coming soon|out of stock|sold out)/i.test(combined)) {
      return true;
    }
    if (text.length > 60) return false;
    if (/(continue|submit|search|apply|sort|filter|oldest|latest|popular|close|cancel|next|back|show more|load more|select|choose|login|log in|sign in)/i.test(combined)) {
      return true;
    }
    if (style.cursor === "pointer" && text.length <= 70 && element.children.length <= 2) {
      const rect = element.getBoundingClientRect();
      return rect.width >= 24 && rect.height >= 14;
    }
    return false;
  }

  function classifyPage() {
    const host = location.hostname.toLowerCase();
    if (host.includes("youtube.com")) {
      if (location.pathname.includes("/watch")) return "youtube_watch";
      if (location.pathname.includes("/videos")) return "youtube_channel_videos";
      if (location.pathname.includes("/results")) return "youtube_search";
      return "youtube";
    }
    if (/amazon\./.test(host) || document.querySelector("[data-component-type='s-search-result']")) return "ecommerce";
    if (/devpost\.com/.test(host)) return "devpost";
    if (/google\..*\/travel|flights|booking|expedia|makemytrip|kayak|skyscanner/.test(`${host}${location.pathname}`)) return "travel_search";
    if (document.querySelector("form")) return "form_page";
    if (document.querySelector("table")) return "table_page";
    return "generic";
  }

  function pageSignals(controls) {
    const text = clean(document.body?.innerText || "", 1000).toLowerCase();
    const signals = [];
    if (controls.some((control) => control.controlType === "search")) signals.push("search_available");
    if (controls.some((control) => control.controlType === "filter-or-sort")) signals.push("filters_or_sort_available");
    if (controls.some((control) => control.controlType === "cart-action")) signals.push("cart_action_visible");
    if (controls.some((control) => control.controlType === "protected-final-action")) signals.push("protected_final_action_visible");
    if (controls.some((control) => control.modalContext)) signals.push("modal_visible");
    if (controls.some((control) => control.menuContext)) signals.push("menu_or_dropdown_visible");
    if (/captcha|cloudflare|verify you are human/.test(text)) signals.push("bot_protection_visible");
    if (/sign in|log in|password|otp|verification code/.test(text)) signals.push("login_or_auth_visible");
    if (/out of stock|sold out|unavailable|coming soon|notify me/.test(text)) signals.push("unavailable_item_visible");
    if (/added to cart|added to basket|go to cart|cart subtotal|basket subtotal/.test(text)) signals.push("added_to_cart_visible");
    return signals.slice(0, 12);
  }

  function buildGraph() {
    if (isKeyboardWtfInternalPage()) {
      return {
        sessionId: `${location.hostname}:${performance.timeOrigin}`,
        tabId: 0,
        url: location.href,
        title: document.title,
        extensionVersion: CONTENT_SCRIPT_VERSION,
        contentScriptVersion: CONTENT_SCRIPT_VERSION,
        browserName: browserName(),
        pageType: "keyboard_wtf_internal_ignored",
        routeKey: `${location.hostname}${location.pathname}`,
        graphVersion: ++graphVersion,
        capturedAt: new Date().toISOString(),
        candidateCount: 0,
        visibleCandidateCount: 0,
        shadowRootsScanned: 0,
        pageSignals: ["keyboard_wtf_internal_page_ignored"],
        controls: []
      };
    }
    const controls = [];
    controlsById = new Map();
    const candidates = candidateElements();
    let visibleCandidateCount = 0;
    for (const element of candidates.elements) {
      if (!isVisible(element)) continue;
      visibleCandidateCount += 1;
      const rect = element.getBoundingClientRect();
      const tagName = element.tagName.toLowerCase();
      const role = roleFor(element);
      const text = elementText(element);
      const ariaLabel = clean(element.getAttribute("aria-label") || "");
      const title = clean(element.getAttribute("title") || "");
      const placeholder = clean(element.getAttribute("placeholder") || "");
      const inputType = clean(element.getAttribute("type") || "");
      const href = safeHref(element);
      const domPathSignature = nthPath(element);
      const near = nearbyText(element);
      const parent = parentSection(element);
      const disabled = disabledReason(element);
      const isSensitive = sensitive(element, `${text} ${ariaLabel} ${placeholder} ${near}`);
      if (!text && !ariaLabel && !title && !placeholder && !href && !role) continue;
      const control = {
        role,
        tagName,
        controlType: controlTypeFor(element, role, text, href),
        pageRegion: pageRegion(element),
        text: isSensitive ? "" : text,
        ariaLabel: isSensitive ? "" : ariaLabel,
        title: isSensitive ? "" : title,
        placeholder: isSensitive ? "" : placeholder,
        inputType,
        selected: element.getAttribute("aria-selected") === "true" || element.selected === true,
        checked: element.checked === true || element.getAttribute("aria-checked") === "true",
        enabled: !disabled,
        visible: true,
        disabledReason: disabled,
        bounds: {
          x: Math.round(rect.x),
          y: Math.round(rect.y),
          width: Math.round(rect.width),
          height: Math.round(rect.height)
        },
        href,
        selectorCandidates: selectorCandidates(element),
        domPathSignature,
        nearbyText: isSensitive ? "" : near,
        parentSection: isSensitive ? "" : parent,
        cardContext: isSensitive ? "" : cardContext(element),
        formContext: isSensitive ? "" : formContext(element),
        menuContext: isSensitive ? "" : menuContext(element),
        modalContext: isSensitive ? "" : modalContext(element),
        tabContext: isSensitive ? "" : tabContext(element),
        repeatingGroup: isSensitive ? "" : repeatingGroup(element),
        positionKind: positionKind(element),
        confidenceScore: confidence(element, role, text, ariaLabel, href),
        sensitive: isSensitive,
        shadowDom: isInShadowDom(element)
      };
      control.controlId = stableId(control);
      controlsById.set(control.controlId, element);
      controls.push(control);
      if (controls.length >= MAX_CONTROLS) break;
    }
    graphVersion += 1;
    return {
      sessionId: `${location.hostname}:${performance.timeOrigin}`,
      tabId: 0,
      url: location.href,
      title: document.title,
      extensionVersion: CONTENT_SCRIPT_VERSION,
      contentScriptVersion: CONTENT_SCRIPT_VERSION,
      browserName: browserName(),
      pageType: classifyPage(),
      routeKey: `${location.hostname}${location.pathname}`,
      graphVersion,
      capturedAt: new Date().toISOString(),
      candidateCount: candidates.elements.length,
      visibleCandidateCount,
      shadowRootsScanned: candidates.shadowRootsScanned,
      pageSignals: pageSignals(controls),
      controls
    };
  }

  function postGraphSoon(delay = 250) {
    if (extensionContextInvalid || isKeyboardWtfInternalPage()) return;
    clearTimeout(postTimer);
    postTimer = setTimeout(() => {
      const graph = buildGraph();
      safeSendMessage({ type: "keyboardWtfGraph", graph });
    }, delay);
  }

  function findControl(controlId) {
    if (!controlsById.has(controlId)) buildGraph();
    const element = controlsById.get(controlId);
    if (!element) throw new Error(`Control not found: ${controlId}`);
    if (sensitive(element, "")) throw new Error("Refusing to automate a sensitive control.");
    return element;
  }

  function clickTarget(element) {
    const clickableSelector = [
      "button",
      "a[href]",
      "input[type='button']",
      "input[type='submit']",
      "input[type='image']",
      "[role='button']",
      "[role='link']",
      "summary",
      "[onclick]",
      "[tabindex]:not([tabindex='-1'])"
    ].join(",");
    return element.matches?.(clickableSelector)
      ? element
      : element.closest?.(clickableSelector)
        || element.querySelector?.(clickableSelector)
        || element;
  }

  function clickableBounds(element) {
    const target = clickTarget(element);
    const rect = target.getBoundingClientRect();
    return {
      x: Math.round(rect.x),
      y: Math.round(rect.y),
      width: Math.round(rect.width),
      height: Math.round(rect.height)
    };
  }

  async function prepareClickControl(controlId) {
    const element = findControl(controlId);
    const target = clickTarget(element);
    target.scrollIntoView({ block: "center", inline: "center", behavior: "auto" });
    await new Promise((resolve) => setTimeout(resolve, 140));
    const anchor = target.matches?.("a[href]") ? target : target.querySelector?.("a[href]");
    return {
      ok: true,
      controlId,
      href: anchor?.href || "",
      bounds: clickableBounds(target),
      tagName: target.tagName?.toLowerCase?.() || "",
      text: clean(target.innerText || target.textContent || target.getAttribute?.("aria-label") || target.getAttribute?.("value") || "", 120)
    };
  }

  async function clickControl(controlId) {
    const element = clickTarget(findControl(controlId));
    element.scrollIntoView({ block: "center", inline: "center", behavior: "auto" });
    await new Promise((resolve) => setTimeout(resolve, 120));
    const anchor = element.matches?.("a[href]") ? element : element.querySelector?.("a[href]");
    const href = anchor?.href || "";
    const rect = element.getBoundingClientRect();
    const eventInit = {
      bubbles: true,
      cancelable: true,
      view: window,
      clientX: Math.max(0, Math.round(rect.left + Math.min(rect.width / 2, 20))),
      clientY: Math.max(0, Math.round(rect.top + Math.min(rect.height / 2, 20))),
      button: 0,
    };
    for (const type of ["pointerdown", "mousedown", "pointerup", "mouseup"]) {
      try {
        element.dispatchEvent(type.startsWith("pointer")
          ? new PointerEvent(type, { ...eventInit, pointerType: "mouse", isPrimary: true })
          : new MouseEvent(type, eventInit));
      } catch {
        // Some pages override constructors; fall back to element.click below.
      }
    }
    element.click();
    if (href && /^https?:/i.test(href)) {
      setTimeout(() => {
        if (location.href !== href) location.assign(href);
      }, 120);
    }
    postGraphSoon(650);
    return { ok: true, message: `Clicked ${controlId}.`, bounds: clickableBounds(element), href };
  }

  async function fillControl(controlId, text) {
    const element = findControl(controlId);
    const tag = element.tagName.toLowerCase();
    if (!(tag === "input" || tag === "textarea" || element.isContentEditable)) {
      throw new Error("Target control is not fillable.");
    }
    element.scrollIntoView({ block: "center", inline: "center", behavior: "auto" });
    element.focus();
    if (element.isContentEditable) {
      element.textContent = text;
    } else {
      element.value = text;
    }
    element.dispatchEvent(new InputEvent("input", { bubbles: true, inputType: "insertText", data: text }));
    element.dispatchEvent(new Event("change", { bubbles: true }));
    postGraphSoon(250);
    return { ok: true, message: `Filled ${controlId}.` };
  }

  async function scrollPage(direction, amountText) {
    const amount = Number(amountText) || Math.max(420, Math.round(innerHeight * 0.78));
    const multiplier = String(direction || "down").toLowerCase().includes("up") ? -1 : 1;
    window.scrollBy({ top: amount * multiplier, left: 0, behavior: "auto" });
    await new Promise((resolve) => setTimeout(resolve, 350));
    postGraphSoon(250);
    return { ok: true, message: `Scrolled ${multiplier > 0 ? "down" : "up"}.` };
  }

  function inspectFocusedElement() {
    const element = document.activeElement;
    if (!element || element === document.body || element === document.documentElement) {
      return {
        exists: false,
        role: "",
        tagName: "",
        controlType: "",
        text: "",
        ariaLabel: "",
        title: "",
        placeholder: "",
        href: "",
        enabled: false,
        selected: false,
        checked: false,
        bounds: { x: 0, y: 0, width: 0, height: 0 },
        nearbyText: "",
        cardContext: "",
        formContext: "",
        menuContext: "",
        modalContext: "",
        sensitive: false,
        confidence: 0,
        enterLikelySafe: false,
        spaceLikelySafe: false
      };
    }

    const role = roleFor(element);
    const text = elementText(element);
    const ariaLabel = clean(element.getAttribute("aria-label") || "");
    const placeholder = clean(element.getAttribute("placeholder") || "");
    const title = clean(element.getAttribute("title") || "");
    const href = safeHref(element);
    const near = nearbyText(element);
    const isSensitive = sensitive(element, `${text} ${ariaLabel} ${placeholder} ${near}`);
    const disabled = disabledReason(element);
    const type = controlTypeFor(element, role, text, href);
    const rect = element.getBoundingClientRect();
    const combined = `${role} ${type} ${text} ${ariaLabel} ${placeholder} ${near}`.toLowerCase();
    const protectedFinal = /checkout|payment|pay now|place order|delete|archive|send|publish|submit|otp|captcha|password/.test(combined);
    const actionable = /button|link|tab|menu|checkbox|radio|choice|cart-action|filter-or-sort|video-card|product-card/.test(`${role} ${type}`);
    return {
      exists: true,
      role,
      tagName: element.tagName?.toLowerCase?.() || "",
      controlType: type,
      text: isSensitive ? "" : text,
      ariaLabel: isSensitive ? "" : ariaLabel,
      title: isSensitive ? "" : title,
      placeholder: isSensitive ? "" : placeholder,
      href,
      enabled: !disabled,
      selected: element.getAttribute("aria-selected") === "true" || element.selected === true,
      checked: element.checked === true || element.getAttribute("aria-checked") === "true",
      bounds: {
        x: Math.round(rect.x),
        y: Math.round(rect.y),
        width: Math.round(rect.width),
        height: Math.round(rect.height)
      },
      nearbyText: isSensitive ? "" : near,
      cardContext: isSensitive ? "" : cardContext(element),
      formContext: isSensitive ? "" : formContext(element),
      menuContext: isSensitive ? "" : menuContext(element),
      modalContext: isSensitive ? "" : modalContext(element),
      sensitive: isSensitive,
      confidence: confidence(element, role, text, ariaLabel, href),
      enterLikelySafe: !isSensitive && !protectedFinal && (actionable || role.includes("textbox") || type === "search"),
      spaceLikelySafe: !isSensitive && !protectedFinal && actionable
    };
  }

  chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    (async () => {
      if (message?.type === "keyboardWtfScan") {
        const graph = buildGraph();
        if (!isKeyboardWtfInternalPage())
          await safeSendMessage({ type: "keyboardWtfGraph", graph });
        sendResponse({ ok: true, graph });
      } else if (message?.type === "keyboardWtfPrepareClick") {
        sendResponse(await prepareClickControl(message.controlId));
      } else if (message?.type === "keyboardWtfClick") {
        sendResponse(await clickControl(message.controlId));
      } else if (message?.type === "keyboardWtfFill") {
        sendResponse(await fillControl(message.controlId, message.text || ""));
      } else if (message?.type === "keyboardWtfScroll") {
        sendResponse(await scrollPage(message.direction || "down", message.amount || message.text || ""));
      } else if (message?.type === "keyboardWtfInspectFocusedElement") {
        sendResponse({ ok: true, focusedElement: inspectFocusedElement() });
      }
    })().catch((error) => sendResponse({ ok: false, message: error?.message || "Companion content action failed." }));
    return true;
  });

  observer = new MutationObserver(() => postGraphSoon(500));
  if (!isKeyboardWtfInternalPage()) {
    observer.observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ["aria-label", "aria-selected", "aria-expanded", "disabled", "hidden", "style", "class"] });
  }

  for (const method of ["pushState", "replaceState"]) {
    const original = history[method];
    history[method] = function patchedHistoryMethod(...args) {
      const result = original.apply(this, args);
      setTimeout(() => {
        if (location.href !== lastUrl) {
          lastUrl = location.href;
          postGraphSoon(300);
        }
      }, 0);
      return result;
    };
  }
  addEventListener("popstate", () => postGraphSoon(300));
  addEventListener("hashchange", () => postGraphSoon(300));
  addEventListener("visibilitychange", () => {
    if (!document.hidden) postGraphSoon(200);
  });

  postGraphSoon(500);
})();
