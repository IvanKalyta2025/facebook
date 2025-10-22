const form = document.getElementById('analysis-form');
const timeframeSelect = document.getElementById('timeframe-select');
const limitInput = document.getElementById('limit-input');
const analyzeButton = document.getElementById('analyze-button');
const statusEl = document.getElementById('form-status');
const summaryContent = document.getElementById('summary-content');
const portfolioContent = document.getElementById('portfolio-content');
const historyBody = document.querySelector('#history-table tbody');
const aiContent = document.getElementById('ai-content');
const exchangeTemplate = document.getElementById('exchange-template');

const state = {
  timeframes: [],
  defaultTimeframe: '1h',
  defaultLimit: 200,
  summary: null,
  portfolio: null,
};

document.addEventListener('DOMContentLoaded', () => {
  initialize().catch((error) => {
    setStatus(error.message || 'Не удалось загрузить данные', 'error');
  });
});

async function initialize() {
  setStatus('Загрузка данных...');
  const dashboard = await fetchJson('/api/dashboard');
  state.timeframes = dashboard.timeframes ?? [];
  state.defaultTimeframe = dashboard.defaultTimeframe ?? '1h';
  state.defaultLimit = dashboard.defaultLimit ?? 200;
  state.summary = dashboard.lastSummary ?? null;
  state.portfolio = dashboard.portfolio ?? null;

  populateTimeframes(state.timeframes, state.summary?.timeframeKey ?? state.defaultTimeframe);
  limitInput.value = state.summary?.limit ?? state.defaultLimit;

  renderSummary(state.summary);
  renderPortfolio(state.portfolio);
  renderHistory(state.portfolio?.history ?? []);
  renderAi(state.summary);
  setStatus('');

  form.addEventListener('submit', onFormSubmit);
}

async function onFormSubmit(event) {
  event.preventDefault();
  const timeframe = timeframeSelect.value || state.defaultTimeframe;
  let limit = parseInt(limitInput.value, 10);
  if (!Number.isFinite(limit)) {
    limit = state.defaultLimit;
  }
  limit = Math.max(50, Math.min(limit, 500));

  setFormEnabled(false);
  setStatus('Анализ выполняется...');

  try {
    const payload = { timeframe, limit };
    const summary = await fetchJson('/api/analyze', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
    });

    state.summary = summary;
    state.portfolio = summary.portfolio ?? state.portfolio;
    limitInput.value = summary.limit ?? limit;

    renderSummary(state.summary);
    renderPortfolio(state.summary?.portfolio ?? state.portfolio);
    renderHistory((state.summary?.portfolio ?? state.portfolio)?.history ?? []);
    renderAi(state.summary);
    setStatus('Анализ обновлён', 'success');
  } catch (error) {
    setStatus(error.message || 'Ошибка при выполнении анализа', 'error');
  } finally {
    setFormEnabled(true);
  }
}

async function fetchJson(url, options = {}) {
  const response = await fetch(url, options);
  const contentType = response.headers.get('content-type') ?? '';
  let data = null;
  if (contentType.includes('application/json')) {
    data = await response.json();
  } else {
    data = await response.text();
  }

  if (!response.ok) {
    const message = data && typeof data === 'object' && data.error
      ? data.error
      : typeof data === 'string' && data
      ? data
      : `HTTP ${response.status}`;
    throw new Error(message);
  }

  return data;
}

function populateTimeframes(timeframes, selectedKey) {
  timeframeSelect.innerHTML = '';
  timeframes.forEach(({ key, label }) => {
    const option = document.createElement('option');
    option.value = key;
    option.textContent = `${label} (${key})`;
    if (key === selectedKey) {
      option.selected = true;
    }
    timeframeSelect.appendChild(option);
  });

  if (timeframes.length === 0) {
    const option = document.createElement('option');
    option.value = state.defaultTimeframe;
    option.textContent = state.defaultTimeframe;
    option.selected = true;
    timeframeSelect.appendChild(option);
  }
}

function renderSummary(summary) {
  summaryContent.innerHTML = '';
  summaryContent.classList.remove('muted');

  if (!summary) {
    summaryContent.classList.add('muted');
    summaryContent.textContent = 'Нет выполненных анализов.';
    return;
  }

  const header = document.createElement('div');
  header.className = 'summary-header';
  const timeframeLine = document.createElement('p');
  timeframeLine.innerHTML = `<strong>${summary.timeframeLabel}</strong> — последние ${summary.limit} свечей.`;
  header.appendChild(timeframeLine);

  if (summary.latestTimestamp) {
    const timestamp = document.createElement('p');
    timestamp.textContent = `Последняя свеча: ${formatTimestamp(summary.latestTimestamp)}`;
    header.appendChild(timestamp);
  }

  if (summary.averageClosePrice !== null && summary.averageClosePrice !== undefined) {
    const priceLine = document.createElement('p');
    priceLine.textContent = `Средняя цена закрытия по биржам: ${formatCurrency(summary.averageClosePrice)} USD`;
    header.appendChild(priceLine);
  }

  summaryContent.appendChild(header);

  if (summary.aggregate) {
    const aggregateBlock = document.createElement('div');
    aggregateBlock.className = 'aggregate-block';
    const suggestion = document.createElement('p');
    suggestion.innerHTML = `<strong>${summary.aggregate.suggestion}</strong>`;
    aggregateBlock.appendChild(suggestion);

    const scoreLine = document.createElement('p');
    scoreLine.textContent = `Средний балл: ${formatNumber(summary.aggregate.averageScore, 2)} | Смещение: ${formatBias(summary.aggregate.bias)}`;
    aggregateBlock.appendChild(scoreLine);

    summaryContent.appendChild(aggregateBlock);
  }

  if (summary.closedTrade) {
    const closedLine = document.createElement('p');
    const sign = summary.closedTrade.profitLoss >= 0 ? '+' : '';
    closedLine.textContent = `Закрыта ${formatBias(summary.closedTrade.bias)} позиция (${formatTimestamp(summary.closedTrade.closedAt)}): ${sign}${formatNumber(summary.closedTrade.profitLoss)} USD (ROI ${formatPercent(summary.closedTrade.returnOnMargin)})`;
    summaryContent.appendChild(closedLine);
  }

  if (summary.openedPosition) {
    const openLine = document.createElement('p');
    openLine.textContent = `Текущая позиция: ${formatBias(summary.openedPosition.bias)} по ${formatCurrency(summary.openedPosition.entryPrice)} USD | маржа ${formatCurrency(summary.openedPosition.margin)} USD | плечо ${formatNumber(summary.openedPosition.leverage, 1)}x.`;
    summaryContent.appendChild(openLine);
  } else if (summary.aggregate && summary.aggregate.bias && summary.aggregate.bias !== 'Neutral') {
    const info = document.createElement('p');
    info.textContent = 'Позиция не открыта: недоступно достаточно свободных средств или сигнал нейтрален.';
    summaryContent.appendChild(info);
  }

  if (Array.isArray(summary.errors) && summary.errors.length > 0) {
    const errorList = document.createElement('ul');
    errorList.className = 'error-list';
    summary.errors.forEach((message) => {
      const item = document.createElement('li');
      item.textContent = message;
      errorList.appendChild(item);
    });
    summaryContent.appendChild(errorList);
  }

  if (Array.isArray(summary.exchanges) && summary.exchanges.length > 0) {
    const grid = document.createElement('div');
    grid.className = 'exchange-grid';
    summary.exchanges.forEach((exchange) => {
      grid.appendChild(renderExchangeCard(exchange));
    });
    summaryContent.appendChild(grid);
  }
}

function renderExchangeCard(exchange) {
  const fragment = exchangeTemplate.content.cloneNode(true);
  const card = fragment.querySelector('.exchange-card');
  card.querySelector('.exchange-name').textContent = exchange.name;
  card.querySelector('.exchange-score').textContent = `Балл: ${exchange.score}`;

  const metricsList = card.querySelector('.exchange-metrics');
  const latest = Array.isArray(exchange.candles) && exchange.candles.length > 0 ? exchange.candles[exchange.candles.length - 1] : null;
  const prev = Array.isArray(exchange.candles) && exchange.candles.length > 1 ? exchange.candles[exchange.candles.length - 2] : null;

  if (latest) {
    metricsList.appendChild(createMetric(`Цена закрытия: ${formatCurrency(latest.close)} USD`));
    metricsList.appendChild(createMetric(`Время: ${formatTimestamp(latest.timestamp)}`));
    if (prev && prev.close) {
      const delta = ((latest.close - prev.close) / prev.close) * 100;
      metricsList.appendChild(createMetric(`Изменение: ${formatNumber(delta, 2)}%`));
    }
  }

  metricsList.appendChild(createMetric(`RSI: ${formatNullable(exchange.rsi)}`));
  metricsList.appendChild(createMetric(`MACD: ${formatNullable(exchange.macd)} | Сигнал: ${formatNullable(exchange.signal)}`));
  metricsList.appendChild(createMetric(`Объём x${formatNullable(exchange.volumeRatio)}`));

  if (Array.isArray(exchange.scoreBreakdown) && exchange.scoreBreakdown.length > 0) {
    metricsList.appendChild(createMetric(`Разбивка: ${exchange.scoreBreakdown.join('; ')}`));
  }

  const candleSummary = card.querySelector('.exchange-candles');
  candleSummary.textContent = exchange.candleSummary ?? 'Без описания свечей.';

  return card;
}

function createMetric(text) {
  const li = document.createElement('li');
  li.textContent = text;
  return li;
}

function renderPortfolio(portfolio) {
  portfolioContent.innerHTML = '';
  portfolioContent.classList.remove('muted');

  if (!portfolio) {
    portfolioContent.classList.add('muted');
    portfolioContent.textContent = 'Портфель ещё не инициализирован.';
    return;
  }

  const lines = [];
  lines.push(`Свободный баланс: ${formatCurrency(portfolio.balance)} USD`);
  lines.push(`Реализованная прибыль: ${formatCurrency(portfolio.statistics?.realizedPnl ?? 0)} USD`);

  const stats = portfolio.statistics ?? {};
  if (stats.tradesTotal > 0) {
    const winRate = stats.tradesTotal ? (stats.tradesWon / stats.tradesTotal) * 100 : 0;
    lines.push(`Сделок: ${stats.tradesTotal} (побед ${stats.tradesWon}, поражений ${stats.tradesLost}, win rate ${formatNumber(winRate, 2)}%)`);
  } else {
    lines.push('Сделки ещё не выполнены.');
  }

  if (stats.biggestWin > 0) {
    lines.push(`Максимальная прибыль: ${formatCurrency(stats.biggestWin)} USD`);
  }
  if (stats.biggestLoss < 0) {
    lines.push(`Максимальный убыток: ${formatCurrency(stats.biggestLoss)} USD`);
  }

  if (portfolio.openPosition) {
    lines.push(
      `Открытая позиция: ${formatBias(portfolio.openPosition.bias)} по ${formatCurrency(portfolio.openPosition.entryPrice)} USD, маржа ${formatCurrency(portfolio.openPosition.margin)} USD, плечо ${formatNumber(portfolio.openPosition.leverage, 1)}x.`,
    );
    if (portfolio.openPosition.rationale) {
      lines.push(`Комментарий: ${portfolio.openPosition.rationale}`);
    }
  }

  portfolioContent.append(
    ...lines.map((text) => {
      const p = document.createElement('p');
      p.textContent = text;
      return p;
    }),
  );
}

function renderHistory(history) {
  historyBody.innerHTML = '';
  if (!Array.isArray(history) || history.length === 0) {
    const row = document.createElement('tr');
    const cell = document.createElement('td');
    cell.colSpan = 7;
    cell.textContent = 'Закрытых сделок пока нет.';
    row.appendChild(cell);
    historyBody.appendChild(row);
    return;
  }

  const rows = history.slice().reverse().slice(0, 25);
  rows.forEach((trade) => {
    const row = document.createElement('tr');
    row.innerHTML = `
      <td>${formatTimestamp(trade.openedAt)}</td>
      <td>${formatTimestamp(trade.closedAt)}</td>
      <td>${formatBias(trade.bias)}</td>
      <td>${formatCurrency(trade.entryPrice)}</td>
      <td>${formatCurrency(trade.exitPrice)}</td>
      <td>${formatPercent(trade.returnOnMargin)}</td>
      <td class="${trade.profitLoss >= 0 ? 'positive' : 'negative'}">${formatNumber(trade.profitLoss)}</td>
    `;
    historyBody.appendChild(row);
  });
}

function renderAi(summary) {
  aiContent.innerHTML = '';
  aiContent.classList.remove('muted');

  if (!summary) {
    aiContent.classList.add('muted');
    aiContent.textContent = 'Советы появятся после выполнения анализа.';
    return;
  }

  if (summary.aiRecommendation) {
    const provider = document.createElement('p');
    provider.innerHTML = `<strong>${summary.aiRecommendation.provider}</strong>`;
    const message = document.createElement('pre');
    message.textContent = summary.aiRecommendation.message;
    aiContent.appendChild(provider);
    aiContent.appendChild(message);
    return;
  }

  if (summary.aiStatus) {
    aiContent.textContent = summary.aiStatus;
    aiContent.classList.add('muted');
  } else {
    aiContent.classList.add('muted');
    aiContent.textContent = 'Советы не доступны для данного анализа.';
  }
}

function setStatus(message, type) {
  statusEl.textContent = message ?? '';
  statusEl.classList.remove('error', 'success');
  if (type) {
    statusEl.classList.add(type);
  }
}

function setFormEnabled(enabled) {
  timeframeSelect.disabled = !enabled;
  limitInput.disabled = !enabled;
  analyzeButton.disabled = !enabled;
}

function formatNumber(value, decimals = 2) {
  if (value === null || value === undefined || Number.isNaN(value)) {
    return '—';
  }
  return Number(value).toFixed(decimals);
}

function formatCurrency(value) {
  if (value === null || value === undefined || Number.isNaN(value)) {
    return '—';
  }
  return Number(value).toFixed(2);
}

function formatPercent(value) {
  if (value === null || value === undefined || Number.isNaN(value)) {
    return '—';
  }
  return `${formatNumber(Number(value) * 100, 2)}%`;
}

function formatNullable(value, decimals = 2) {
  if (value === null || value === undefined) {
    return '—';
  }
  return formatNumber(value, decimals);
}

function formatBias(value) {
  if (value === null || value === undefined) {
    return 'Ожидание';
  }
  const normalized = typeof value === 'string' ? value.toLowerCase() : value;
  if (normalized === 'long' || normalized === 1 || normalized === '1') {
    return 'Лонг';
  }
  if (normalized === 'short' || normalized === -1 || normalized === '-1') {
    return 'Шорт';
  }
  return 'Ожидание';
}

function formatTimestamp(value) {
  if (!value) {
    return '—';
  }
  try {
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) {
      return value;
    }
    return date.toLocaleString('ru-RU', { hour12: false });
  } catch {
    return value;
  }
}
