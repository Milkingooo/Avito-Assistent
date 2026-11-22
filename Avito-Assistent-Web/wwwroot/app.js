(function () {
  "use strict";

  const state = {
    messages: [],
    catalog: [],
    catalogById: {},
    cities: [],
    categories: []
  };

  const els = {
    brandHome: document.getElementById("brand-home"),
    toolsDropdown: document.getElementById("tools-dropdown"),
    btnToolsToggle: document.getElementById("btn-tools-toggle"),
    toolsMenu: document.getElementById("tools-menu"),
    menuItems: document.querySelectorAll(".tools-item[data-tab]"),
    backButtons: document.querySelectorAll(".js-back-chat"),
    panels: {
      chat: document.getElementById("panel-chat"),
      catalog: document.getElementById("panel-catalog"),
      inspector: document.getElementById("panel-inspector")
    },
    engineStatus: document.getElementById("engine-status"),
    btnNewDialog: document.getElementById("btn-new-dialog"),
    messagesList: document.getElementById("messages-list"),
    chatForm: document.getElementById("chat-form"),
    chatInput: document.getElementById("chat-input"),
    btnSend: document.getElementById("btn-send"),
    statusBadge: document.getElementById("status-badge"),
    condCity: document.getElementById("cond-city"),
    condBudget: document.getElementById("cond-budget"),
    condCategories: document.getElementById("cond-categories"),
    condConstraints: document.getElementById("cond-constraints"),
    condKeywords: document.getElementById("cond-keywords"),
    pipelineLogs: document.getElementById("pipeline-logs"),
    catalogCount: document.getElementById("catalog-count"),
    filterCity: document.getElementById("filter-city"),
    filterCategory: document.getElementById("filter-category"),
    filterSearch: document.getElementById("filter-search"),
    catalogFilteredCount: document.getElementById("catalog-filtered-count"),
    catalogGrid: document.getElementById("catalog-grid"),
    rawRequest: document.getElementById("raw-request"),
    rawResponse: document.getElementById("raw-response"),
    rawStatus: document.getElementById("raw-status"),
    btnRunRaw: document.getElementById("btn-run-raw"),
    modal: document.getElementById("item-modal"),
    modalClose: document.getElementById("modal-close"),
    modalCategory: document.getElementById("modal-category"),
    modalTitle: document.getElementById("modal-title"),
    modalPrice: document.getElementById("modal-price"),
    modalMeta: document.getElementById("modal-meta"),
    modalAttrs: document.getElementById("modal-attrs"),
    modalDesc: document.getElementById("modal-desc")
  };

  const CATEGORY_LABELS = { table: "Стол", chair: "Кресло", lamp: "Лампа" };
  const ATTR_LABELS = {
    width_cm: "ширина, см",
    adjustable_height: "регулировка высоты",
    color_temperature_k: "температура, К"
  };

  function nowTime() {
    return new Date().toLocaleTimeString("ru-RU", { hour: "2-digit", minute: "2-digit" });
  }

  function formatRub(n) {
    return Number(n).toLocaleString("ru-RU") + " ₽";
  }

  function escapeHtml(str) {
    return String(str ?? "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  function formatAttributes(attrs) {
    if (!attrs || Object.keys(attrs).length === 0) return "Характеристики не указаны";
    return Object.entries(attrs)
      .map(([k, v]) => {
        if (k === "width_cm") return `Ширина: ${v} см`;
        if (k === "color_temperature_k") return `Свет: ${v} К`;
        if (k === "adjustable_height") return v ? "Регулировка высоты: есть" : "Регулировка высоты: нет";
        return `${k}: ${v}`;
      })
      .join(" · ");
  }

  // Навигация между скрытыми разделами
  function switchPanel(target) {
    Object.entries(els.panels).forEach(([key, panel]) => {
      panel.classList.toggle("active", key === target);
    });
    els.menuItems.forEach((btn) => {
      btn.classList.toggle("active", btn.dataset.tab === target);
    });
    closeToolsMenu();
  }

  function closeToolsMenu() {
    els.toolsMenu.hidden = true;
    els.btnToolsToggle.setAttribute("aria-expanded", "false");
  }

  els.btnToolsToggle.addEventListener("click", (e) => {
    e.stopPropagation();
    const isHidden = els.toolsMenu.hidden;
    els.toolsMenu.hidden = !isHidden;
    els.btnToolsToggle.setAttribute("aria-expanded", isHidden ? "true" : "false");
  });

  document.addEventListener("click", (e) => {
    if (!els.toolsDropdown.contains(e.target)) {
      closeToolsMenu();
    }
  });

  els.menuItems.forEach((btn) => {
    btn.addEventListener("click", () => switchPanel(btn.dataset.tab));
  });

  els.backButtons.forEach((btn) => {
    btn.addEventListener("click", () => switchPanel("chat"));
  });

  els.brandHome.addEventListener("click", () => switchPanel("chat"));

  // Делегирование кликов по быстрым примерам, кнопкам «Инфо» и копированию ID
  document.addEventListener("click", (e) => {
    const promptBtn = e.target.closest(".prompt-chip[data-prompt]");
    if (promptBtn) {
      els.chatInput.value = promptBtn.dataset.prompt;
      els.chatForm.requestSubmit();
      return;
    }

    const infoBtn = e.target.closest(".js-item-info");
    if (infoBtn) {
      const id = infoBtn.dataset.id;
      const item = state.catalogById[id];
      if (item) openItemModal(item);
      return;
    }

    const copyBtn = e.target.closest(".js-copy-id");
    if (copyBtn) {
      const id = copyBtn.dataset.id;
      navigator.clipboard?.writeText(id);
      const prev = copyBtn.textContent;
      copyBtn.textContent = "✓";
      setTimeout(() => { copyBtn.textContent = prev; }, 900);
    }
  });

  // Модальное окно товара
  function openItemModal(item) {
    els.modalCategory.className = `cat-tag cat-${item.category}`;
    els.modalCategory.textContent = item.category_name || CATEGORY_LABELS[item.category] || item.category;
    els.modalTitle.textContent = item.title;
    els.modalPrice.textContent = formatRub(item.price_rub);
    els.modalMeta.textContent = `Город: ${item.city_name} · ID: ${item.id}`;
    els.modalAttrs.textContent = formatAttributes(item.attributes);
    els.modalDesc.textContent = item.description || "Описание отсутствует.";
    els.modal.hidden = false;
  }

  els.modalClose.addEventListener("click", () => { els.modal.hidden = true; });
  els.modal.addEventListener("click", (e) => {
    if (e.target === els.modal) els.modal.hidden = true;
  });

  // Сброс диалога
  els.btnNewDialog.addEventListener("click", () => {
    state.messages = [];
    els.messagesList.innerHTML = `
      <div class="welcome-state">
        <div class="welcome-title">Новый диалог начат</div>
        <div class="welcome-sub">Опишите нужные предметы (стол, кресло, лампа), город и бюджет:</div>
        <div class="quick-prompts">
          <button type="button" class="prompt-chip" data-prompt="Лампа с нейтральным светом 4000 К и стол в Азове до 12 000 ₽">Лампа 4000 К и стол в Азове до 12 000 ₽</button>
          <button type="button" class="prompt-chip" data-prompt="Стол не шире метра, кресло и лампа. Таганрог. До 15 000 ₽ за всё.">Полный комплект в Таганроге до 15 000 ₽</button>
          <button type="button" class="prompt-chip" data-prompt="Компьютерный стол в Таганроге 15000">Компьютерный стол (по ключевому слову)</button>
        </div>
      </div>`;
    els.statusBadge.className = "status-tag neutral";
    els.statusBadge.textContent = "ожидание";
    els.condCity.textContent = "—";
    els.condBudget.textContent = "—";
    els.condCategories.textContent = "—";
    els.condConstraints.textContent = "—";
    els.condKeywords.textContent = "—";
    els.pipelineLogs.textContent = "Диалог очищен.";
    syncRawInspector(null);
    els.chatInput.focus();
  });

  function syncRawInspector(responseObj) {
    const reqPayload = {
      messages: state.messages.length
        ? state.messages
        : [{ role: "user", text: "Стол не шире метра, кресло и лампа. Таганрог. До 15 000 ₽ за всё." }]
    };
    els.rawRequest.value = JSON.stringify(reqPayload, null, 2);
    if (responseObj) {
      els.rawResponse.textContent = JSON.stringify(responseObj, null, 2);
      els.rawStatus.className = `status-tag ${responseObj.status}`;
      els.rawStatus.textContent = responseObj.status;
    }
  }

  function appendUserBubble(text) {
    const welcome = els.messagesList.querySelector(".welcome-state");
    if (welcome) welcome.remove();

    const wrap = document.createElement("div");
    wrap.className = "msg msg-user";
    wrap.innerHTML = `
      <div class="msg-meta"><strong>Вы</strong> · ${nowTime()}</div>
      <div class="msg-bubble">${escapeHtml(text)}</div>
    `;
    els.messagesList.appendChild(wrap);
    els.messagesList.scrollTop = els.messagesList.scrollHeight;
  }

  function appendAssistantResponse(data) {
    const resp = data.contract_response;
    const wrap = document.createElement("div");
    wrap.className = "msg msg-assistant";

    let innerHtml = "";

    if (resp.status === "clarify") {
      const qCards = resp.questions
        .map(
          (q) => `
          <div class="clarify-card">
            <span class="clarify-field">${escapeHtml(q.field)}</span>
            <span>${escapeHtml(q.text)}</span>
          </div>`
        )
        .join("");

      innerHtml = `
        <div class="assistant-summary-bubble">Уточните недостающие параметры, чтобы я подобрал подходящий комплект:</div>
        <div class="clarify-list">${qCards}</div>`;
    } else if (resp.status === "no_match") {
      innerHtml = `<div class="no-match-bubble">${escapeHtml(resp.message)}</div>`;
    } else if (resp.status === "ok") {
      const options = data.enriched_options || resp.options;
      const minPrice = Math.min(...options.map((o) => o.total_rub));

      const bundlesHtml = options
        .map((opt, idx) => {
          const listings = opt.listings || [];
          listings.forEach((it) => { state.catalogById[it.id] = it; });

          let highlightBadge = "";
          if (idx === 0 && opt.explanation.includes("по ключевым словам")) {
            highlightBadge = `<span class="badge-highlight">Лучшее совпадение</span>`;
          } else if (opt.total_rub === minPrice) {
            highlightBadge = `<span class="badge-highlight">Лучшая цена</span>`;
          }

          const itemsRowsHtml = listings
            .map((item) => {
              const catClass = `cat-${escapeHtml(item.category)}`;
              return `
                <div class="item-row">
                  <div class="item-row-top">
                    <div class="item-meta-left">
                      <span class="cat-tag ${catClass}">${escapeHtml(item.category_name)}</span>
                      <span>📍 ${escapeHtml(item.city_name)}</span>
                      <span class="item-id-code">ID: ${escapeHtml(item.id)}</span>
                    </div>
                    <span class="item-price">${formatRub(item.price_rub)}</span>
                  </div>
                  <div class="item-row-bottom">
                    <div class="item-title">${escapeHtml(item.title)}</div>
                    <div class="item-actions">
                      <button type="button" class="btn-tiny js-item-info" data-id="${escapeHtml(item.id)}">↗ Инфо</button>
                      <button type="button" class="btn-tiny js-copy-id" data-id="${escapeHtml(item.id)}" title="Скопировать ID">⧉</button>
                    </div>
                  </div>
                </div>`;
            })
            .join("");

          return `
            <div class="bundle-card">
              <div class="bundle-top">
                <div class="bundle-badges">
                  <span class="badge-variant">Вариант ${idx + 1}</span>
                  ${highlightBadge}
                </div>
                <div class="bundle-total">Итого: ${formatRub(opt.total_rub)}</div>
              </div>
              <div class="bundle-explanation">${escapeHtml(opt.explanation)}</div>
              <div class="bundle-items">${itemsRowsHtml}</div>
            </div>`;
        })
        .join("");

      innerHtml = `
        <div class="assistant-summary-bubble">Подобрано вариантов: ${options.length}. Ниже представлены комплекты с учётом ваших требований.</div>
        <div class="found-header">
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
            <circle cx="12" cy="12" r="10"></circle>
            <path d="m9 12 2 2 4-4"></path>
          </svg>
          Подобрано комплектов: ${options.length}
        </div>
        <div class="bundles-list">${bundlesHtml}</div>`;
    }

    wrap.innerHTML = `
      <div class="msg-meta"><strong>Помощник</strong> · ${nowTime()}</div>
      ${innerHtml}
    `;
    els.messagesList.appendChild(wrap);
    els.messagesList.scrollTop = els.messagesList.scrollHeight;
  }

  function updateSidebar(data) {
    const resp = data.contract_response;
    const ex = data.extracted;

    els.statusBadge.className = `status-tag ${resp.status}`;
    els.statusBadge.textContent = resp.status;

    els.condCity.textContent = ex.city_name
      ? `${ex.city_name}`
      : ex.city_problem
      ? "Не из списка"
      : "Не указан";

    els.condBudget.textContent = ex.budget_rub !== null && ex.budget_rub !== undefined
      ? formatRub(ex.budget_rub)
      : "Не указан";

    const catsText = (ex.categories || []).map((c) => CATEGORY_LABELS[c] || c).join(", ");
    els.condCategories.textContent = ex.category_problem
      ? `${catsText ? catsText + " + " : ""}неподдерживаемая`
      : catsText || "Не указаны";

    if (ex.constraints && ex.constraints.length > 0) {
      els.condConstraints.textContent = ex.constraints
        .map((c) => `${ATTR_LABELS[c.attribute] || c.attribute}: ${c.op} ${c.value}`)
        .join("; ");
    } else {
      els.condConstraints.textContent = "Нет";
    }

    els.condKeywords.textContent = ex.keywords && ex.keywords.length > 0
      ? ex.keywords.join(", ")
      : "Нет";

    els.pipelineLogs.textContent = (data.logs && data.logs.length > 0)
      ? data.logs.join("\n")
      : "ОК (без предупреждений)";
  }

  els.chatForm.addEventListener("submit", async (e) => {
    e.preventDefault();
    const text = els.chatInput.value.trim();
    if (!text) return;

    els.chatInput.value = "";
    els.btnSend.disabled = true;

    appendUserBubble(text);
    state.messages.push({ role: "user", text });

    if (state.messages.length > 19) {
      state.messages = state.messages.slice(state.messages.length - 19);
      if (state.messages[0].role !== "user") {
        state.messages.shift();
      }
    }

    try {
      const res = await fetch("api/chat", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ messages: state.messages })
      });
      const data = await res.json();
      if (!res.ok) {
        state.messages.pop();
        alert(data.error || "Ошибка обработки запроса");
        return;
      }

      appendAssistantResponse(data);
      updateSidebar(data);
      syncRawInspector(data.contract_response);
      state.messages.push({ role: "assistant", text: data.assistant_text });
    } catch (err) {
      state.messages.pop();
      alert("Ошибка соединения с сервером: " + err.message);
    } finally {
      els.btnSend.disabled = false;
      els.chatInput.focus();
    }
  });

  // Прямая проверка строгого контракта
  els.btnRunRaw.addEventListener("click", async () => {
    els.btnRunRaw.disabled = true;
    try {
      const res = await fetch("api/contract", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: els.rawRequest.value
      });
      const body = await res.json();
      els.rawResponse.textContent = JSON.stringify(body, null, 2);
      if (res.ok && body.status) {
        els.rawStatus.className = `status-tag ${body.status}`;
        els.rawStatus.textContent = `200 · ${body.status}`;
      } else {
        els.rawStatus.className = "status-tag no_match";
        els.rawStatus.textContent = `HTTP ${res.status}`;
      }
    } catch (err) {
      els.rawResponse.textContent = "Ошибка: " + err.message;
    } finally {
      els.btnRunRaw.disabled = false;
    }
  });

  async function loadInitialData() {
    syncRawInspector(null);

    try {
      const healthRes = await fetch("api/health");
      const health = await healthRes.json();
      if (health.llm_configured) {
        els.engineStatus.className = "model-pill";
        els.engineStatus.innerHTML = `<span class="dot"></span> Gemma 4 31B`;
      } else {
        els.engineStatus.className = "model-pill fallback";
        els.engineStatus.innerHTML = `<span class="dot"></span> Резервный парсер`;
      }
    } catch (_) {
      els.engineStatus.textContent = "Офлайн";
    }

    try {
      const catRes = await fetch("api/catalog");
      const catData = await catRes.json();
      state.catalog = catData.items || [];
      state.catalog.forEach((it) => { state.catalogById[it.id] = it; });
      state.cities = catData.cities || [];
      state.categories = catData.categories || [];
      els.catalogCount.textContent = String(state.catalog.length);

      state.cities.forEach((c) => {
        const opt = document.createElement("option");
        opt.value = c.id;
        opt.textContent = c.name;
        els.filterCity.appendChild(opt);
      });

      state.categories.forEach((c) => {
        const opt = document.createElement("option");
        opt.value = c.id;
        opt.textContent = c.name;
        els.filterCategory.appendChild(opt);
      });

      renderCatalog();
    } catch (_) {
      els.catalogGrid.innerHTML = "<p>Не удалось загрузить каталог.</p>";
    }
  }

  function renderCatalog() {
    const city = els.filterCity.value;
    const category = els.filterCategory.value;
    const q = els.filterSearch.value.trim().toLowerCase();

    const filtered = state.catalog.filter((item) => {
      if (city && item.city_id !== city) return false;
      if (category && item.category !== category) return false;
      if (q && !item.id.toLowerCase().includes(q) && !item.title.toLowerCase().includes(q)) return false;
      return true;
    });

    els.catalogFilteredCount.textContent = `Показано: ${filtered.length} из ${state.catalog.length}`;
    els.catalogGrid.innerHTML = filtered
      .map(
        (item) => `
        <article class="cat-card">
          <div class="cat-card-top">
            <span class="cat-tag cat-${escapeHtml(item.category)}">${escapeHtml(item.category_name)}</span>
            <span class="item-id-code">ID: ${escapeHtml(item.id)}</span>
          </div>
          <div class="cat-title">${escapeHtml(item.title)}</div>
          <div class="cat-price">${formatRub(item.price_rub)}</div>
          <div class="cat-attrs">${escapeHtml(formatAttributes(item.attributes))}</div>
          ${item.description ? `<div class="cat-desc">${escapeHtml(item.description)}</div>` : ""}
          <div class="cat-meta">
            <span>📍 ${escapeHtml(item.city_name)}</span>
            <span><code>${escapeHtml(item.city_id)}</code></span>
          </div>
        </article>`
      )
      .join("");
  }

  els.filterCity.addEventListener("change", renderCatalog);
  els.filterCategory.addEventListener("change", renderCatalog);
  els.filterSearch.addEventListener("input", renderCatalog);

  loadInitialData();
})();
