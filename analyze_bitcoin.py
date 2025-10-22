#!/usr/bin/env python3
"""Comprehensive Bitcoin analysis script for multiple exchanges.

This module fetches recent candlestick data for BTC trading pairs from
Coinbase, Binance, OKX, and Bybit. It calculates a collection of widely-used
technical indicators (SMAs, EMAs, MACD, RSI) together with simple volume and
candlestick diagnostics in order to generate trading suggestions.

The script is intentionally dependency-free (only the Python standard library
is required) so it can run in constrained environments. Network access to the
exchange REST APIs is required for real analysis.
"""

from __future__ import annotations

import argparse
import json
from dataclasses import dataclass
from datetime import datetime, timezone
from statistics import mean
from typing import Callable, Dict, Iterable, List, Optional, Sequence, Tuple
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import Request, urlopen


@dataclass
class Candle:
    """Represents a single OHLCV candle."""

    timestamp: datetime
    open: float
    high: float
    low: float
    close: float
    volume: float


@dataclass
class ExchangeResult:
    """Container for indicator results per exchange."""

    name: str
    candles: List[Candle]
    sma20: Optional[float]
    sma50: Optional[float]
    ema12: Optional[float]
    ema26: Optional[float]
    macd: Optional[float]
    signal: Optional[float]
    histogram: Optional[float]
    rsi: Optional[float]
    volume_average20: Optional[float]
    volume_ratio: Optional[float]
    candle_summary: str
    candle_bias: int
    score: int
    score_breakdown: List[str]


TIMEFRAMES: Dict[str, Dict[str, str]] = {
    "1h": {"label": "1 час", "coinbase": "3600", "binance": "1h", "okx": "1H", "bybit": "60"},
    "4h": {"label": "4 часа", "coinbase": "14400", "binance": "4h", "okx": "4H", "bybit": "240"},
    "1d": {"label": "1 день", "coinbase": "86400", "binance": "1d", "okx": "1D", "bybit": "D"},
}

DEFAULT_LIMIT = 200
USER_AGENT = "Mozilla/5.0 (compatible; BitcoinAnalysisBot/1.0)"


class ExchangeFetchError(RuntimeError):
    """Raised when an exchange request fails or returns malformed data."""


def _load_json(url: str) -> object:
    request = Request(url, headers={"User-Agent": USER_AGENT, "Accept": "application/json"})
    try:
        with urlopen(request, timeout=20) as response:
            payload = response.read()
    except (HTTPError, URLError) as exc:  # pragma: no cover - network errors are runtime concerns
        raise ExchangeFetchError(f"Ошибка при запросе {url}: {exc}") from exc

    try:
        return json.loads(payload.decode("utf-8"))
    except json.JSONDecodeError as exc:  # pragma: no cover - network errors are runtime concerns
        raise ExchangeFetchError(f"Не удалось декодировать ответ от {url}: {exc}") from exc


def fetch_coinbase(limit: int, timeframe_value: str) -> List[Candle]:
    params = {"granularity": timeframe_value}
    if limit:
        params["limit"] = str(limit)
    url = "https://api.exchange.coinbase.com/products/BTC-USD/candles?" + urlencode(params)
    raw = _load_json(url)
    if not isinstance(raw, list):
        raise ExchangeFetchError("Неожиданный формат ответа Coinbase")

    candles: List[Candle] = []
    for entry in raw:
        if not isinstance(entry, Sequence) or len(entry) < 6:
            continue
        timestamp, low, high, open_, close, volume = entry[:6]
        candles.append(
            Candle(
                timestamp=datetime.fromtimestamp(float(timestamp), tz=timezone.utc),
                open=float(open_),
                high=float(high),
                low=float(low),
                close=float(close),
                volume=float(volume),
            )
        )
    if not candles:
        raise ExchangeFetchError("Coinbase вернул пустой набор свечей")
    candles.sort(key=lambda candle: candle.timestamp)
    return candles


def fetch_binance(limit: int, timeframe_value: str) -> List[Candle]:
    params = {"symbol": "BTCUSDT", "interval": timeframe_value, "limit": str(limit)}
    url = "https://api.binance.com/api/v3/klines?" + urlencode(params)
    raw = _load_json(url)
    if not isinstance(raw, list):
        raise ExchangeFetchError("Неожиданный формат ответа Binance")

    candles: List[Candle] = []
    for entry in raw:
        if not isinstance(entry, Sequence) or len(entry) < 6:
            continue
        open_time, open_, high, low, close, volume = entry[:6]
        candles.append(
            Candle(
                timestamp=datetime.fromtimestamp(float(open_time) / 1000, tz=timezone.utc),
                open=float(open_),
                high=float(high),
                low=float(low),
                close=float(close),
                volume=float(volume),
            )
        )
    if not candles:
        raise ExchangeFetchError("Binance вернул пустой набор свечей")
    candles.sort(key=lambda candle: candle.timestamp)
    return candles


def fetch_okx(limit: int, timeframe_value: str) -> List[Candle]:
    params = {"instId": "BTC-USDT", "bar": timeframe_value, "limit": str(limit)}
    url = "https://www.okx.com/api/v5/market/candles?" + urlencode(params)
    raw = _load_json(url)
    if not isinstance(raw, dict) or "data" not in raw:
        raise ExchangeFetchError("Неожиданный формат ответа OKX")

    entries = raw.get("data", [])
    candles: List[Candle] = []
    for entry in entries:
        if not isinstance(entry, Sequence) or len(entry) < 6:
            continue
        timestamp = entry[0]
        open_, high, low, close = entry[1:5]
        volume = entry[5]
        candles.append(
            Candle(
                timestamp=datetime.fromtimestamp(float(timestamp) / 1000, tz=timezone.utc),
                open=float(open_),
                high=float(high),
                low=float(low),
                close=float(close),
                volume=float(volume),
            )
        )
    if not candles:
        raise ExchangeFetchError("OKX вернул пустой набор свечей")
    candles.sort(key=lambda candle: candle.timestamp)
    return candles


def fetch_bybit(limit: int, timeframe_value: str) -> List[Candle]:
    params = {
        "category": "linear",
        "symbol": "BTCUSDT",
        "interval": timeframe_value,
        "limit": str(limit),
    }
    url = "https://api.bybit.com/v5/market/kline?" + urlencode(params)
    raw = _load_json(url)
    if not isinstance(raw, dict) or raw.get("retCode") not in (0, "0"):
        raise ExchangeFetchError("Неожиданный код ответа Bybit")

    result = raw.get("result")
    if not isinstance(result, dict) or "list" not in result:
        raise ExchangeFetchError("Некорректный ответ Bybit")

    candles: List[Candle] = []
    for entry in result.get("list", []):
        if not isinstance(entry, Sequence) or len(entry) < 6:
            continue
        timestamp = entry[0]
        open_, high, low, close, volume = entry[:6]
        candles.append(
            Candle(
                timestamp=datetime.fromtimestamp(float(timestamp) / 1000, tz=timezone.utc),
                open=float(open_),
                high=float(high),
                low=float(low),
                close=float(close),
                volume=float(volume),
            )
        )
    if not candles:
        raise ExchangeFetchError("Bybit вернул пустой набор свечей")
    candles.sort(key=lambda candle: candle.timestamp)
    return candles


def simple_moving_average(values: Sequence[float], period: int) -> Optional[float]:
    if len(values) < period:
        return None
    return mean(values[-period:])


def ema_series(values: Sequence[float], period: int) -> List[Optional[float]]:
    if len(values) < period:
        return [None] * len(values)

    multiplier = 2 / (period + 1)
    ema_prev = mean(values[:period])
    ema_values: List[Optional[float]] = [None] * (period - 1)
    ema_values.append(ema_prev)

    for price in values[period:]:
        ema_prev = (price - ema_prev) * multiplier + ema_prev
        ema_values.append(ema_prev)

    return ema_values


def last_valid(values: Sequence[Optional[float]]) -> Optional[float]:
    for value in reversed(values):
        if value is not None:
            return value
    return None


def compute_macd(values: Sequence[float], short_period: int = 12, long_period: int = 26, signal_period: int = 9) -> Tuple[Optional[float], Optional[float], Optional[float]]:
    if len(values) < long_period:
        return None, None, None

    ema_short = ema_series(values, short_period)
    ema_long = ema_series(values, long_period)
    macd_line = [
        (short_val - long_val) if short_val is not None and long_val is not None else None
        for short_val, long_val in zip(ema_short, ema_long)
    ]

    macd_values = [value for value in macd_line if value is not None]
    if len(macd_values) < signal_period:
        return None, None, None

    signal_series = ema_series(macd_values, signal_period)
    macd_value = macd_values[-1]
    signal_value = last_valid(signal_series)
    histogram = macd_value - signal_value if signal_value is not None else None
    return macd_value, signal_value, histogram


def compute_rsi(values: Sequence[float], period: int = 14) -> Optional[float]:
    if len(values) <= period:
        return None

    gains: List[float] = []
    losses: List[float] = []
    for i in range(1, period + 1):
        delta = values[i] - values[i - 1]
        if delta >= 0:
            gains.append(delta)
            losses.append(0.0)
        else:
            gains.append(0.0)
            losses.append(-delta)

    average_gain = sum(gains) / period
    average_loss = sum(losses) / period

    rs: float
    if average_loss == 0:
        rs = float("inf")
    else:
        rs = average_gain / average_loss

    rsi = 100 - (100 / (1 + rs))

    for i in range(period + 1, len(values)):
        delta = values[i] - values[i - 1]
        gain = max(delta, 0.0)
        loss = max(-delta, 0.0)
        average_gain = (average_gain * (period - 1) + gain) / period
        average_loss = (average_loss * (period - 1) + loss) / period

        if average_loss == 0:
            rs = float("inf")
        else:
            rs = average_gain / average_loss
        rsi = 100 - (100 / (1 + rs))

    return rsi


@dataclass
class CandlePattern:
    description: str
    bias: int


def analyse_candle_pattern(latest: Candle, previous: Optional[Candle]) -> CandlePattern:
    direction: str
    if latest.close > latest.open:
        direction = "бычья"
    elif latest.close < latest.open:
        direction = "медвежья"
    else:
        direction = "нейтральная"

    total_range = max(latest.high - latest.low, 1e-9)
    body = abs(latest.close - latest.open)
    upper_wick = latest.high - max(latest.open, latest.close)
    lower_wick = min(latest.open, latest.close) - latest.low

    description_parts = [
        f"Последняя свеча {direction} (тело {body:.2f}, диапазон {latest.high - latest.low:.2f})."
    ]
    bias = 0

    if previous is not None:
        if (
            latest.close > latest.open
            and previous.close < previous.open
            and latest.close >= previous.open
            and latest.open <= previous.close
        ):
            description_parts.append("Наблюдается бычье поглощение.")
            bias += 1
        elif (
            latest.close < latest.open
            and previous.close > previous.open
            and latest.close <= previous.open
            and latest.open >= previous.close
        ):
            description_parts.append("Наблюдается медвежье поглощение.")
            bias -= 1

    wick_ratio_high = upper_wick / total_range
    wick_ratio_low = lower_wick / total_range

    if wick_ratio_low > 0.6 and latest.close > latest.open:
        description_parts.append("Длинная нижняя тень – потенциал отскока вверх.")
        bias += 1
    if wick_ratio_high > 0.6 and latest.close < latest.open:
        description_parts.append("Длинная верхняя тень – давление продавцов.")
        bias -= 1
    if body / total_range < 0.2:
        description_parts.append("Небольшое тело – неопределённость рынка.")

    bias = max(min(bias, 2), -2)
    return CandlePattern(description=" ".join(description_parts), bias=bias)


def analyse_exchange(name: str, candles: List[Candle]) -> ExchangeResult:
    closes = [candle.close for candle in candles]
    volumes = [candle.volume for candle in candles]
    latest = candles[-1]
    previous = candles[-2] if len(candles) > 1 else None

    sma20 = simple_moving_average(closes, 20)
    sma50 = simple_moving_average(closes, 50)
    ema12 = last_valid(ema_series(closes, 12))
    ema26 = last_valid(ema_series(closes, 26))
    macd, signal, histogram = compute_macd(closes)
    rsi = compute_rsi(closes)
    volume_average20 = simple_moving_average(volumes, 20)
    volume_ratio = (volumes[-1] / volume_average20) if volume_average20 else None
    candle_pattern = analyse_candle_pattern(latest, previous)

    score = 0
    score_breakdown: List[str] = []

    if sma20 is not None:
        if latest.close > sma20:
            score += 1
            score_breakdown.append("Цена выше SMA20 (+1)")
        else:
            score -= 1
            score_breakdown.append("Цена ниже SMA20 (-1)")
    if sma50 is not None:
        if latest.close > sma50:
            score += 1
            score_breakdown.append("Цена выше SMA50 (+1)")
        else:
            score -= 1
            score_breakdown.append("Цена ниже SMA50 (-1)")
    if macd is not None and signal is not None:
        if macd > signal:
            score += 1
            score_breakdown.append("MACD выше сигнальной линии (+1)")
        else:
            score -= 1
            score_breakdown.append("MACD ниже сигнальной линии (-1)")
    if rsi is not None:
        if rsi >= 60:
            score += 1
            score_breakdown.append(f"RSI {rsi:.1f} – сила покупателей (+1)")
        elif rsi <= 40:
            score -= 1
            score_breakdown.append(f"RSI {rsi:.1f} – сила продавцов (-1)")
        else:
            score_breakdown.append(f"RSI {rsi:.1f} – нейтральная зона (0)")
    if volume_ratio is not None:
        if volume_ratio >= 1.2:
            score += 1
            score_breakdown.append("Объём выше среднего (+1)")
        elif volume_ratio <= 0.8:
            score -= 1
            score_breakdown.append("Объём ниже среднего (-1)")
        else:
            score_breakdown.append("Объём рядом со средним (0)")

    score += candle_pattern.bias
    if candle_pattern.bias > 0:
        score_breakdown.append(f"Свечной анализ добавляет {candle_pattern.bias} к оценке")
    elif candle_pattern.bias < 0:
        score_breakdown.append(f"Свечной анализ вычитает {abs(candle_pattern.bias)} из оценки")
    else:
        score_breakdown.append("Свечной анализ нейтрален (0)")

    return ExchangeResult(
        name=name,
        candles=candles,
        sma20=sma20,
        sma50=sma50,
        ema12=ema12,
        ema26=ema26,
        macd=macd,
        signal=signal,
        histogram=histogram,
        rsi=rsi,
        volume_average20=volume_average20,
        volume_ratio=volume_ratio,
        candle_summary=candle_pattern.description,
        candle_bias=candle_pattern.bias,
        score=score,
        score_breakdown=score_breakdown,
    )


def format_float(value: Optional[float], precision: int = 2) -> str:
    if value is None:
        return "—"
    return f"{value:.{precision}f}"


def describe_result(result: ExchangeResult) -> str:
    latest = result.candles[-1]
    previous = result.candles[-2] if len(result.candles) > 1 else None

    lines = [f"Биржа: {result.name}"]
    lines.append(f"Последняя цена закрытия: {latest.close:.2f} USD ({latest.timestamp.isoformat()})")
    if previous is not None and previous.close:
        change = (latest.close - previous.close) / previous.close * 100
        lines.append(f"Изменение к предыдущей свече: {change:+.2f}%")
    lines.append(f"SMA20: {format_float(result.sma20)}, SMA50: {format_float(result.sma50)}")
    lines.append(
        "EMA12: "
        f"{format_float(result.ema12)} | EMA26: {format_float(result.ema26)} | MACD: {format_float(result.macd)} | "
        f"Сигнал: {format_float(result.signal)} | Гистограмма: {format_float(result.histogram)}"
    )
    lines.append(f"RSI(14): {format_float(result.rsi)}")
    if result.volume_average20 is not None:
        lines.append(
            f"Объём: {result.candles[-1].volume:.2f} против среднего {result.volume_average20:.2f} (коэффициент {format_float(result.volume_ratio)})"
        )
    else:
        lines.append(f"Объём последней свечи: {result.candles[-1].volume:.2f}")
    lines.append(f"Свечной анализ: {result.candle_summary}")
    lines.append(f"Итоговый балл: {result.score} ({'; '.join(result.score_breakdown)})")
    return "\n".join(lines)


def aggregate_suggestion(results: Iterable[ExchangeResult]) -> Tuple[str, float]:
    scores = [result.score for result in results]
    if not scores:
        return "Нет данных для рекомендации", 0.0

    average_score = sum(scores) / len(scores)
    if average_score >= 2:
        suggestion = "Преимущество быков – стоит рассмотреть длинную позицию (лонг)."
    elif average_score <= -2:
        suggestion = "Преимущество медведей – стоит рассмотреть короткую позицию (шорт)."
    else:
        suggestion = "Сигналы смешанные – уместно подождать подтверждения перед входом."

    return suggestion, average_score


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Анализ курса BTC по четырём биржам с техническими индикаторами и рекомендацией сделки."
    )
    parser.add_argument(
        "--timeframe",
        choices=TIMEFRAMES.keys(),
        default="1h",
        help="Таймфрейм свечей (по умолчанию 1h)",
    )
    parser.add_argument(
        "--limit",
        type=int,
        default=DEFAULT_LIMIT,
        help=f"Количество загружаемых свечей (по умолчанию {DEFAULT_LIMIT}).",
    )
    args = parser.parse_args()

    timeframe_config = TIMEFRAMES[args.timeframe]
    limit = max(50, args.limit)

    fetchers: List[Tuple[str, Callable[[int, str], List[Candle]]]] = [
        ("Coinbase", fetch_coinbase),
        ("Binance", fetch_binance),
        ("OKX", fetch_okx),
        ("Bybit", fetch_bybit),
    ]

    results: List[ExchangeResult] = []
    errors: List[str] = []

    for name, fetcher in fetchers:
        try:
            candles = fetcher(limit, timeframe_config[name.lower()])
            results.append(analyse_exchange(name, candles))
        except ExchangeFetchError as exc:
            errors.append(f"{name}: {exc}")
        except Exception as exc:  # pragma: no cover - safeguard for unforeseen data issues
            errors.append(f"{name}: непредвиденная ошибка {exc}")

    print(f"Анализ BTC/USD для таймфрейма {timeframe_config['label']} (последние {limit} свечей):\n")

    if results:
        for result in results:
            print(describe_result(result))
            print("-" * 80)

        suggestion, average_score = aggregate_suggestion(results)
        print(f"Средний балл по биржам: {average_score:.2f}")
        print(f"Рекомендация: {suggestion}")
    else:
        print("Не удалось получить данные ни от одной из бирж.")

    if errors:
        print("\nВозникшие ошибки:")
        for message in errors:
            print(f"  - {message}")


if __name__ == "__main__":
    main()
