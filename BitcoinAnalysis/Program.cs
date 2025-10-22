using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace BitcoinAnalysis;

internal static class Program
{
    private const int DefaultLimit = 200;
    private const string UserAgent = "Mozilla/5.0 (compatible; BitcoinAnalysisBot/1.0)";

    private static readonly Dictionary<string, TimeframeOption> Timeframes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1h"] = new TimeframeOption("1 час", "3600", "1h", "1H", "60"),
        ["4h"] = new TimeframeOption("4 часа", "14400", "4h", "4H", "240"),
        ["1d"] = new TimeframeOption("1 день", "86400", "1d", "1D", "D"),
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h"))
        {
            PrintHelp();
            return 0;
        }

        var timeframeKey = "1h";
        var limit = DefaultLimit;

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            if (string.Equals(argument, "--timeframe", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("Для аргумента --timeframe необходимо указать значение.");
                    return 1;
                }

                timeframeKey = args[++i];
            }
            else if (string.Equals(argument, "--limit", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || !int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out limit))
                {
                    Console.Error.WriteLine("Для аргумента --limit необходимо указать целое число.");
                    return 1;
                }
            }
            else if (argument.StartsWith("-"))
            {
                Console.Error.WriteLine($"Неизвестный аргумент: {argument}");
                PrintHelp();
                return 1;
            }
        }

        if (!Timeframes.TryGetValue(timeframeKey, out var timeframeConfig))
        {
            Console.Error.WriteLine($"Неподдерживаемый таймфрейм: {timeframeKey}");
            PrintHelp();
            return 1;
        }

        limit = Math.Max(50, limit);

        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var fetchers = new (string Name, Func<HttpClient, int, string, Task<List<Candle>>> Fetch)[]
        {
            ("Coinbase", ExchangeClient.FetchCoinbaseAsync),
            ("Binance", ExchangeClient.FetchBinanceAsync),
            ("OKX", ExchangeClient.FetchOkxAsync),
            ("Bybit", ExchangeClient.FetchBybitAsync),
        };

        var results = new List<ExchangeResult>();
        var errors = new List<string>();

        foreach (var (name, fetch) in fetchers)
        {
            try
            {
                var timeframeValue = timeframeConfig.ForExchange(name);
                var candles = await fetch(httpClient, limit, timeframeValue).ConfigureAwait(false);
                results.Add(ExchangeAnalyzer.AnalyseExchange(name, candles));
            }
            catch (ExchangeFetchException ex)
            {
                errors.Add($"{name}: {ex.Message}");
            }
            catch (Exception ex)
            {
                errors.Add($"{name}: непредвиденная ошибка {ex.Message}");
            }
        }

        Console.WriteLine($"Анализ BTC/USD для таймфрейма {timeframeConfig.Label} (последние {limit} свечей):\n");

        if (results.Count > 0)
        {
            foreach (var result in results)
            {
                Console.WriteLine(ExchangeReporter.DescribeResult(result));
                Console.WriteLine(new string('-', 80));
            }

            var aggregateSuggestion = ExchangeAnalyzer.AggregateSuggestion(results);
            Console.WriteLine($"Средний балл по биржам: {aggregateSuggestion.AverageScore:F2}");
            Console.WriteLine($"Рекомендация: {aggregateSuggestion.Suggestion}");

            var latestTimestamp = results.Max(result => result.Candles[^1].Timestamp);
            var averageClosePrice = results.Average(result => result.Candles[^1].Close);

            var tracker = new PerformanceTracker(
                Path.Combine(AppContext.BaseDirectory, "trading_performance.json"),
                initialBalance: 10_000.0,
                leverage: 10.0);

            var portfolioState = tracker.Load();
            var closedTrade = tracker.CloseOpenPosition(portfolioState, averageClosePrice, latestTimestamp);
            if (closedTrade != null)
            {
                var direction = TradeBiasExtensions.ToRussian(closedTrade.Bias);
                var sign = closedTrade.ProfitLoss >= 0 ? "+" : string.Empty;
                Console.WriteLine(
                    $"Закрыта позиция {direction} от {closedTrade.OpenedAt:O}: {sign}{closedTrade.ProfitLoss:F2} USD (ROI {closedTrade.ReturnOnMargin * 100:F2}%).");
            }

            var openedPosition = tracker.OpenPosition(
                portfolioState,
                aggregateSuggestion.Bias,
                averageClosePrice,
                latestTimestamp,
                aggregateSuggestion.Suggestion);

            if (openedPosition != null)
            {
                Console.WriteLine(
                    $"Открыта новая позиция {TradeBiasExtensions.ToRussian(openedPosition.Bias)} по {openedPosition.EntryPrice:F2} USD. Маржа {openedPosition.Margin:F2} USD, объём {openedPosition.Quantity:F6} BTC при плече {openedPosition.Leverage:F1}x.");
            }
            else if (aggregateSuggestion.Bias != TradeBias.Neutral)
            {
                Console.WriteLine("Недостаточно средств для открытия новой позиции в тестовом портфеле.");
            }

            tracker.Save(portfolioState);

            Console.WriteLine();
            Console.WriteLine("Сводка тестового счёта:");
            Console.WriteLine(tracker.DescribeState(portfolioState, averageClosePrice));

            var deepSeekKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
            if (string.IsNullOrWhiteSpace(deepSeekKey))
            {
                Console.WriteLine();
                Console.WriteLine("AI советник DeepSeek: переменная окружения DEEPSEEK_API_KEY не задана.");
            }
            else
            {
                try
                {
                    using var deepSeekClient = DeepSeekAdvisor.CreateHttpClient(deepSeekKey);
                    var advisor = new DeepSeekAdvisor(deepSeekClient);
                    var prompt = AiPromptBuilder.BuildPrompt(
                        timeframeKey,
                        timeframeConfig,
                        results,
                        aggregateSuggestion,
                        portfolioState,
                        closedTrade,
                        openedPosition,
                        averageClosePrice);
                    var recommendation = await advisor.TryGetRecommendationAsync(prompt).ConfigureAwait(false);
                    if (recommendation != null)
                    {
                        Console.WriteLine();
                        Console.WriteLine($"AI советник ({recommendation.Provider}):");
                        Console.WriteLine(recommendation.Message);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine();
                    Console.WriteLine($"AI советник DeepSeek не ответил: {ex.Message}");
                }
            }
        }
        else
        {
            Console.WriteLine("Не удалось получить данные ни от одной из бирж.");
        }

        if (errors.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Возникшие ошибки:");
            foreach (var message in errors)
            {
                Console.WriteLine($"  - {message}");
            }
        }

        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Использование: dotnet run -- [--timeframe <1h|4h|1d>] [--limit <число>]");
        Console.WriteLine();
        Console.WriteLine("Опции:");
        Console.WriteLine("  --timeframe    Таймфрейм свечей (1h, 4h, 1d). По умолчанию 1h.");
        Console.WriteLine("  --limit        Количество свечей для загрузки. Минимум 50. По умолчанию 200.");
        Console.WriteLine();
        Console.WriteLine("Переменные окружения:");
        Console.WriteLine("  DEEPSEEK_API_KEY  Токен для обращения к AI DeepSeek (опционально).");
        Console.WriteLine();
        Console.WriteLine("Примечание: программа ведёт учёт тестового баланса с плечом 10x в файле trading_performance.json и обновляет статистику сделок при каждом запуске.");
    }
}

internal sealed class TimeframeOption
{
    public TimeframeOption(string label, string coinbase, string binance, string okx, string bybit)
    {
        Label = label;
        Coinbase = coinbase;
        Binance = binance;
        Okx = okx;
        Bybit = bybit;
    }

    public string Label { get; }

    private string Coinbase { get; }

    private string Binance { get; }

    private string Okx { get; }

    private string Bybit { get; }

    public string ForExchange(string exchange)
    {
        return exchange.ToLowerInvariant() switch
        {
            "coinbase" => Coinbase,
            "binance" => Binance,
            "okx" => Okx,
            "bybit" => Bybit,
            _ => throw new ArgumentOutOfRangeException(nameof(exchange), exchange, "Неизвестная биржа"),
        };
    }
}

internal sealed class Candle
{
    public Candle(DateTime timestamp, double open, double high, double low, double close, double volume)
    {
        Timestamp = timestamp;
        Open = open;
        High = high;
        Low = low;
        Close = close;
        Volume = volume;
    }

    public DateTime Timestamp { get; }

    public double Open { get; }

    public double High { get; }

    public double Low { get; }

    public double Close { get; }

    public double Volume { get; }
}

internal sealed class ExchangeResult
{
    public ExchangeResult(
        string name,
        List<Candle> candles,
        double? sma20,
        double? sma50,
        double? ema12,
        double? ema26,
        double? macd,
        double? signal,
        double? histogram,
        double? rsi,
        double? volumeAverage20,
        double? volumeRatio,
        string candleSummary,
        int candleBias,
        int score,
        List<string> scoreBreakdown)
    {
        Name = name;
        Candles = candles;
        Sma20 = sma20;
        Sma50 = sma50;
        Ema12 = ema12;
        Ema26 = ema26;
        Macd = macd;
        Signal = signal;
        Histogram = histogram;
        Rsi = rsi;
        VolumeAverage20 = volumeAverage20;
        VolumeRatio = volumeRatio;
        CandleSummary = candleSummary;
        CandleBias = candleBias;
        Score = score;
        ScoreBreakdown = scoreBreakdown;
    }

    public string Name { get; }

    public List<Candle> Candles { get; }

    public double? Sma20 { get; }

    public double? Sma50 { get; }

    public double? Ema12 { get; }

    public double? Ema26 { get; }

    public double? Macd { get; }

    public double? Signal { get; }

    public double? Histogram { get; }

    public double? Rsi { get; }

    public double? VolumeAverage20 { get; }

    public double? VolumeRatio { get; }

    public string CandleSummary { get; }

    public int CandleBias { get; }

    public int Score { get; }

    public List<string> ScoreBreakdown { get; }
}

internal enum TradeBias
{
    Short = -1,
    Neutral = 0,
    Long = 1,
}

internal sealed class AggregateSuggestionResult
{
    public AggregateSuggestionResult(string suggestion, double averageScore, TradeBias bias)
    {
        Suggestion = suggestion;
        AverageScore = averageScore;
        Bias = bias;
    }

    public string Suggestion { get; }

    public double AverageScore { get; }

    public TradeBias Bias { get; }
}

internal sealed class CandlePattern
{
    public CandlePattern(string description, int bias)
    {
        Description = description;
        Bias = bias;
    }

    public string Description { get; }

    public int Bias { get; }
}

internal sealed class ExchangeFetchException : Exception
{
    public ExchangeFetchException(string message)
        : base(message)
    {
    }

    public ExchangeFetchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal static class ExchangeClient
{
    public static async Task<List<Candle>> FetchCoinbaseAsync(HttpClient httpClient, int limit, string timeframeValue)
    {
        var query = new Dictionary<string, string>
        {
            ["granularity"] = timeframeValue,
        };
        if (limit > 0)
        {
            query["limit"] = limit.ToString(CultureInfo.InvariantCulture);
        }

        var uri = BuildUri("https://api.exchange.coinbase.com/products/BTC-USD/candles", query);

        using var document = await LoadJsonAsync(httpClient, uri).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new ExchangeFetchException("Неожиданный формат ответа Coinbase");
        }

        var candles = new List<Candle>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 6)
            {
                continue;
            }

            var timestamp = JsonHelper.GetDouble(entry[0]);
            var low = JsonHelper.GetDouble(entry[1]);
            var high = JsonHelper.GetDouble(entry[2]);
            var open = JsonHelper.GetDouble(entry[3]);
            var close = JsonHelper.GetDouble(entry[4]);
            var volume = JsonHelper.GetDouble(entry[5]);

            candles.Add(new Candle(
                DateTimeOffset.FromUnixTimeSeconds((long)Math.Round(timestamp, MidpointRounding.AwayFromZero)).UtcDateTime,
                open,
                high,
                low,
                close,
                volume));
        }

        if (candles.Count == 0)
        {
            throw new ExchangeFetchException("Coinbase вернул пустой набор свечей");
        }

        candles.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));
        return candles;
    }

    public static async Task<List<Candle>> FetchBinanceAsync(HttpClient httpClient, int limit, string timeframeValue)
    {
        var query = new Dictionary<string, string>
        {
            ["symbol"] = "BTCUSDT",
            ["interval"] = timeframeValue,
            ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
        };

        var uri = BuildUri("https://api.binance.com/api/v3/klines", query);

        using var document = await LoadJsonAsync(httpClient, uri).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new ExchangeFetchException("Неожиданный формат ответа Binance");
        }

        var candles = new List<Candle>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 6)
            {
                continue;
            }

            var openTime = JsonHelper.GetDouble(entry[0]);
            var open = JsonHelper.GetDouble(entry[1]);
            var high = JsonHelper.GetDouble(entry[2]);
            var low = JsonHelper.GetDouble(entry[3]);
            var close = JsonHelper.GetDouble(entry[4]);
            var volume = JsonHelper.GetDouble(entry[5]);

            candles.Add(new Candle(
                DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(openTime, MidpointRounding.AwayFromZero)).UtcDateTime,
                open,
                high,
                low,
                close,
                volume));
        }

        if (candles.Count == 0)
        {
            throw new ExchangeFetchException("Binance вернул пустой набор свечей");
        }

        candles.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));
        return candles;
    }

    public static async Task<List<Candle>> FetchOkxAsync(HttpClient httpClient, int limit, string timeframeValue)
    {
        var query = new Dictionary<string, string>
        {
            ["instId"] = "BTC-USDT",
            ["bar"] = timeframeValue,
            ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
        };

        var uri = BuildUri("https://www.okx.com/api/v5/market/candles", query);

        using var document = await LoadJsonAsync(httpClient, uri).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("data", out var data))
        {
            throw new ExchangeFetchException("Неожиданный формат ответа OKX");
        }

        var candles = new List<Candle>();
        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 6)
            {
                continue;
            }

            var timestamp = JsonHelper.GetDouble(entry[0]);
            var open = JsonHelper.GetDouble(entry[1]);
            var high = JsonHelper.GetDouble(entry[2]);
            var low = JsonHelper.GetDouble(entry[3]);
            var close = JsonHelper.GetDouble(entry[4]);
            var volume = JsonHelper.GetDouble(entry[5]);

            candles.Add(new Candle(
                DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(timestamp, MidpointRounding.AwayFromZero)).UtcDateTime,
                open,
                high,
                low,
                close,
                volume));
        }

        if (candles.Count == 0)
        {
            throw new ExchangeFetchException("OKX вернул пустой набор свечей");
        }

        candles.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));
        return candles;
    }

    public static async Task<List<Candle>> FetchBybitAsync(HttpClient httpClient, int limit, string timeframeValue)
    {
        var query = new Dictionary<string, string>
        {
            ["category"] = "linear",
            ["symbol"] = "BTCUSDT",
            ["interval"] = timeframeValue,
            ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
        };

        var uri = BuildUri("https://api.bybit.com/v5/market/kline", query);

        using var document = await LoadJsonAsync(httpClient, uri).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("retCode", out var retCode))
        {
            throw new ExchangeFetchException("Неожиданный ответ Bybit");
        }

        var codeValue = retCode.ValueKind switch
        {
            JsonValueKind.Number => retCode.GetInt32().ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String => retCode.GetString() ?? string.Empty,
            _ => string.Empty,
        };

        if (codeValue != "0")
        {
            throw new ExchangeFetchException("Bybit вернул ошибочный код ответа");
        }

        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("list", out var list))
        {
            throw new ExchangeFetchException("Некорректный ответ Bybit");
        }

        var candles = new List<Candle>();
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 6)
            {
                continue;
            }

            var timestamp = JsonHelper.GetDouble(entry[0]);
            var open = JsonHelper.GetDouble(entry[1]);
            var high = JsonHelper.GetDouble(entry[2]);
            var low = JsonHelper.GetDouble(entry[3]);
            var close = JsonHelper.GetDouble(entry[4]);
            var volume = JsonHelper.GetDouble(entry[5]);

            candles.Add(new Candle(
                DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(timestamp, MidpointRounding.AwayFromZero)).UtcDateTime,
                open,
                high,
                low,
                close,
                volume));
        }

        if (candles.Count == 0)
        {
            throw new ExchangeFetchException("Bybit вернул пустой набор свечей");
        }

        candles.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));
        return candles;
    }

    private static async Task<JsonDocument> LoadJsonAsync(HttpClient httpClient, Uri uri)
    {
        try
        {
            using var response = await httpClient.GetAsync(uri).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ExchangeFetchException($"Ошибка при запросе {uri}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new ExchangeFetchException($"Истекло время ожидания ответа {uri}", ex);
        }
        catch (JsonException ex)
        {
            throw new ExchangeFetchException($"Не удалось декодировать ответ {uri}: {ex.Message}", ex);
        }
    }

    private static Uri BuildUri(string baseUrl, IReadOnlyDictionary<string, string> queryParameters)
    {
        var builder = new UriBuilder(baseUrl)
        {
            Query = string.Join("&", queryParameters.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}")),
        };
        return builder.Uri;
    }
}

internal static class JsonHelper
{
    public static double GetDouble(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.String when double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => throw new ExchangeFetchException("Не удалось прочитать числовое значение"),
        };
    }
}

internal static class Indicators
{
    public static double? SimpleMovingAverage(IReadOnlyList<double> values, int period)
    {
        if (values.Count < period)
        {
            return null;
        }

        var sum = 0.0;
        for (var i = values.Count - period; i < values.Count; i++)
        {
            sum += values[i];
        }

        return sum / period;
    }

    public static List<double?> EmaSeries(IReadOnlyList<double> values, int period)
    {
        if (values.Count < period)
        {
            return Enumerable.Repeat<double?>(null, values.Count).ToList();
        }

        var multiplier = 2.0 / (period + 1);
        var emaPrev = values.Take(period).Average();

        var emaValues = new List<double?>(values.Count);
        for (var i = 0; i < period - 1; i++)
        {
            emaValues.Add(null);
        }

        emaValues.Add(emaPrev);

        for (var i = period; i < values.Count; i++)
        {
            var price = values[i];
            emaPrev = (price - emaPrev) * multiplier + emaPrev;
            emaValues.Add(emaPrev);
        }

        return emaValues;
    }

    public static double? LastValid(IReadOnlyList<double?> values)
    {
        for (var i = values.Count - 1; i >= 0; i--)
        {
            var candidate = values[i];
            if (candidate.HasValue)
            {
                return candidate.Value;
            }
        }

        return null;
    }

    public static (double? Macd, double? Signal, double? Histogram) ComputeMacd(
        IReadOnlyList<double> values,
        int shortPeriod = 12,
        int longPeriod = 26,
        int signalPeriod = 9)
    {
        if (values.Count < longPeriod)
        {
            return (null, null, null);
        }

        var emaShort = EmaSeries(values, shortPeriod);
        var emaLong = EmaSeries(values, longPeriod);

        var macdLine = new List<double?>();
        for (var i = 0; i < values.Count; i++)
        {
            var shortValue = emaShort[i];
            var longValue = emaLong[i];
            if (shortValue.HasValue && longValue.HasValue)
            {
                macdLine.Add(shortValue.Value - longValue.Value);
            }
            else
            {
                macdLine.Add(null);
            }
        }

        var macdValues = macdLine.Where(value => value.HasValue).Select(value => value!.Value).ToList();
        if (macdValues.Count < signalPeriod)
        {
            return (null, null, null);
        }

        var signalSeries = EmaSeries(macdValues, signalPeriod);
        var macdValue = macdValues[^1];
        var signalValue = LastValid(signalSeries);
        var histogram = signalValue.HasValue ? macdValue - signalValue.Value : (double?)null;
        return (macdValue, signalValue, histogram);
    }

    public static double? ComputeRsi(IReadOnlyList<double> values, int period = 14)
    {
        if (values.Count <= period)
        {
            return null;
        }

        var gains = new List<double>();
        var losses = new List<double>();
        for (var i = 1; i <= period; i++)
        {
            var delta = values[i] - values[i - 1];
            if (delta >= 0)
            {
                gains.Add(delta);
                losses.Add(0.0);
            }
            else
            {
                gains.Add(0.0);
                losses.Add(-delta);
            }
        }

        var averageGain = gains.Sum() / period;
        var averageLoss = losses.Sum() / period;

        var rs = averageLoss == 0 ? double.PositiveInfinity : averageGain / averageLoss;
        var rsi = 100 - (100 / (1 + rs));

        for (var i = period + 1; i < values.Count; i++)
        {
            var delta = values[i] - values[i - 1];
            var gain = Math.Max(delta, 0.0);
            var loss = Math.Max(-delta, 0.0);

            averageGain = (averageGain * (period - 1) + gain) / period;
            averageLoss = (averageLoss * (period - 1) + loss) / period;

            rs = averageLoss == 0 ? double.PositiveInfinity : averageGain / averageLoss;
            rsi = 100 - (100 / (1 + rs));
        }

        return rsi;
    }
}

internal static class CandleInterpreter
{
    public static CandlePattern AnalyseCandlePattern(Candle latest, Candle? previous)
    {
        var direction = latest.Close > latest.Open ? "бычья" : latest.Close < latest.Open ? "медвежья" : "нейтральная";

        var totalRange = Math.Max(latest.High - latest.Low, 1e-9);
        var body = Math.Abs(latest.Close - latest.Open);
        var upperWick = latest.High - Math.Max(latest.Open, latest.Close);
        var lowerWick = Math.Min(latest.Open, latest.Close) - latest.Low;

        var descriptionParts = new List<string>
        {
            $"Последняя свеча {direction} (тело {body:F2}, диапазон {(latest.High - latest.Low):F2}).",
        };

        var bias = 0;

        if (previous != null)
        {
            var bullishEngulfing = latest.Close > latest.Open && previous.Close < previous.Open && latest.Close >= previous.Open && latest.Open <= previous.Close;
            var bearishEngulfing = latest.Close < latest.Open && previous.Close > previous.Open && latest.Close <= previous.Open && latest.Open >= previous.Close;

            if (bullishEngulfing)
            {
                descriptionParts.Add("Наблюдается бычье поглощение.");
                bias += 1;
            }
            else if (bearishEngulfing)
            {
                descriptionParts.Add("Наблюдается медвежье поглощение.");
                bias -= 1;
            }
        }

        var wickRatioHigh = upperWick / totalRange;
        var wickRatioLow = lowerWick / totalRange;

        if (wickRatioLow > 0.6 && latest.Close > latest.Open)
        {
            descriptionParts.Add("Длинная нижняя тень – потенциал отскока вверх.");
            bias += 1;
        }

        if (wickRatioHigh > 0.6 && latest.Close < latest.Open)
        {
            descriptionParts.Add("Длинная верхняя тень – давление продавцов.");
            bias -= 1;
        }

        if (body / totalRange < 0.2)
        {
            descriptionParts.Add("Небольшое тело – неопределённость рынка.");
        }

        bias = Math.Max(Math.Min(bias, 2), -2);
        return new CandlePattern(string.Join(' ', descriptionParts), bias);
    }
}

internal static class ExchangeAnalyzer
{
    public static ExchangeResult AnalyseExchange(string name, List<Candle> candles)
    {
        var closes = candles.Select(candle => candle.Close).ToList();
        var volumes = candles.Select(candle => candle.Volume).ToList();
        var latest = candles[^1];
        var previous = candles.Count > 1 ? candles[^2] : null;

        var sma20 = Indicators.SimpleMovingAverage(closes, 20);
        var sma50 = Indicators.SimpleMovingAverage(closes, 50);
        var ema12 = Indicators.LastValid(Indicators.EmaSeries(closes, 12));
        var ema26 = Indicators.LastValid(Indicators.EmaSeries(closes, 26));
        var (macd, signal, histogram) = Indicators.ComputeMacd(closes);
        var rsi = Indicators.ComputeRsi(closes);
        var volumeAverage20 = Indicators.SimpleMovingAverage(volumes, 20);
        var volumeRatio = volumeAverage20.HasValue ? volumes[^1] / volumeAverage20.Value : (double?)null;
        var candlePattern = CandleInterpreter.AnalyseCandlePattern(latest, previous);

        var score = 0;
        var scoreBreakdown = new List<string>();

        if (sma20.HasValue)
        {
            if (latest.Close > sma20.Value)
            {
                score += 1;
                scoreBreakdown.Add("Цена выше SMA20 (+1)");
            }
            else
            {
                score -= 1;
                scoreBreakdown.Add("Цена ниже SMA20 (-1)");
            }
        }

        if (sma50.HasValue)
        {
            if (latest.Close > sma50.Value)
            {
                score += 1;
                scoreBreakdown.Add("Цена выше SMA50 (+1)");
            }
            else
            {
                score -= 1;
                scoreBreakdown.Add("Цена ниже SMA50 (-1)");
            }
        }

        if (macd.HasValue && signal.HasValue)
        {
            if (macd.Value > signal.Value)
            {
                score += 1;
                scoreBreakdown.Add("MACD выше сигнальной линии (+1)");
            }
            else
            {
                score -= 1;
                scoreBreakdown.Add("MACD ниже сигнальной линии (-1)");
            }
        }

        if (rsi.HasValue)
        {
            if (rsi.Value >= 60)
            {
                score += 1;
                scoreBreakdown.Add($"RSI {rsi.Value:F1} – сила покупателей (+1)");
            }
            else if (rsi.Value <= 40)
            {
                score -= 1;
                scoreBreakdown.Add($"RSI {rsi.Value:F1} – сила продавцов (-1)");
            }
            else
            {
                scoreBreakdown.Add($"RSI {rsi.Value:F1} – нейтральная зона (0)");
            }
        }

        if (volumeRatio.HasValue)
        {
            if (volumeRatio.Value >= 1.2)
            {
                score += 1;
                scoreBreakdown.Add("Объём выше среднего (+1)");
            }
            else if (volumeRatio.Value <= 0.8)
            {
                score -= 1;
                scoreBreakdown.Add("Объём ниже среднего (-1)");
            }
            else
            {
                scoreBreakdown.Add("Объём рядом со средним (0)");
            }
        }

        score += candlePattern.Bias;
        if (candlePattern.Bias > 0)
        {
            scoreBreakdown.Add($"Свечной анализ добавляет {candlePattern.Bias} к оценке");
        }
        else if (candlePattern.Bias < 0)
        {
            scoreBreakdown.Add($"Свечной анализ вычитает {Math.Abs(candlePattern.Bias)} из оценки");
        }
        else
        {
            scoreBreakdown.Add("Свечной анализ нейтрален (0)");
        }

        return new ExchangeResult(
            name,
            candles,
            sma20,
            sma50,
            ema12,
            ema26,
            macd,
            signal,
            histogram,
            rsi,
            volumeAverage20,
            volumeRatio,
            candlePattern.Description,
            candlePattern.Bias,
            score,
            scoreBreakdown);
    }

    public static AggregateSuggestionResult AggregateSuggestion(IEnumerable<ExchangeResult> results)
    {
        var scores = results.Select(result => result.Score).ToList();
        if (scores.Count == 0)
        {
            return new AggregateSuggestionResult("Нет данных для рекомендации", 0.0, TradeBias.Neutral);
        }

        var averageScore = scores.Average();
        string suggestion;
        TradeBias bias;
        if (averageScore >= 2)
        {
            suggestion = "Преимущество быков – стоит рассмотреть длинную позицию (лонг).";
            bias = TradeBias.Long;
        }
        else if (averageScore <= -2)
        {
            suggestion = "Преимущество медведей – стоит рассмотреть короткую позицию (шорт).";
            bias = TradeBias.Short;
        }
        else
        {
            suggestion = "Сигналы смешанные – уместно подождать подтверждения перед входом.";
            bias = TradeBias.Neutral;
        }

        return new AggregateSuggestionResult(suggestion, averageScore, bias);
    }
}

internal static class ExchangeReporter
{
    public static string DescribeResult(ExchangeResult result)
    {
        var latest = result.Candles[^1];
        var previous = result.Candles.Count > 1 ? result.Candles[^2] : null;

        var lines = new List<string>
        {
            $"Биржа: {result.Name}",
            $"Последняя цена закрытия: {latest.Close.ToString("F2", CultureInfo.InvariantCulture)} USD ({latest.Timestamp:O})",
        };

        if (previous != null && Math.Abs(previous.Close) > double.Epsilon)
        {
            var change = (latest.Close - previous.Close) / previous.Close * 100;
            lines.Add($"Изменение к предыдущей свече: {change:+0.00;-0.00;0.00}%");
        }

        lines.Add($"SMA20: {FormatDouble(result.Sma20)}, SMA50: {FormatDouble(result.Sma50)}");
        lines.Add(
            $"EMA12: {FormatDouble(result.Ema12)} | EMA26: {FormatDouble(result.Ema26)} | MACD: {FormatDouble(result.Macd)} | Сигнал: {FormatDouble(result.Signal)} | Гистограмма: {FormatDouble(result.Histogram)}");
        lines.Add($"RSI(14): {FormatDouble(result.Rsi)}");

        if (result.VolumeAverage20.HasValue)
        {
            lines.Add(
                $"Объём: {latest.Volume.ToString("F2", CultureInfo.InvariantCulture)} против среднего {result.VolumeAverage20.Value.ToString("F2", CultureInfo.InvariantCulture)} (коэффициент {FormatDouble(result.VolumeRatio)})");
        }
        else
        {
            lines.Add($"Объём последней свечи: {latest.Volume.ToString("F2", CultureInfo.InvariantCulture)}");
        }

        lines.Add($"Свечной анализ: {result.CandleSummary}");
        lines.Add($"Итоговый балл: {result.Score} ({string.Join("; ", result.ScoreBreakdown)})");
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatDouble(double? value)
    {
        return value.HasValue ? value.Value.ToString("F2", CultureInfo.InvariantCulture) : "—";
    }
}

internal static class TradeBiasExtensions
{
    public static string ToRussian(TradeBias bias)
    {
        return bias switch
        {
            TradeBias.Long => "лонг",
            TradeBias.Short => "шорт",
            _ => "ожидание",
        };
    }
}

internal sealed class PortfolioState
{
    public double InitialBalance { get; set; }

    public double Balance { get; set; }

    public double Leverage { get; set; }

    public SimulatedPosition? OpenPosition { get; set; }

    public List<TradeRecord> History { get; set; } = new();

    public PerformanceStatistics Statistics { get; set; } = new();
}

internal sealed class SimulatedPosition
{
    public TradeBias Bias { get; set; }

    public double EntryPrice { get; set; }

    public double Quantity { get; set; }

    public double Margin { get; set; }

    public double Leverage { get; set; }

    public DateTime OpenedAt { get; set; }

    public string? Rationale { get; set; }

    public double CalculatePnl(double currentPrice)
    {
        var priceDelta = Bias switch
        {
            TradeBias.Long => currentPrice - EntryPrice,
            TradeBias.Short => EntryPrice - currentPrice,
            _ => 0.0,
        };

        return priceDelta * Quantity;
    }
}

internal sealed class TradeRecord
{
    public DateTime OpenedAt { get; set; }

    public DateTime ClosedAt { get; set; }

    public TradeBias Bias { get; set; }

    public double EntryPrice { get; set; }

    public double ExitPrice { get; set; }

    public double Quantity { get; set; }

    public double Margin { get; set; }

    public double ProfitLoss { get; set; }

    public double ReturnOnMargin { get; set; }

    public bool Liquidated { get; set; }

    public string? Rationale { get; set; }
}

internal sealed class PerformanceStatistics
{
    public double RealizedPnl { get; set; }

    public int TradesWon { get; set; }

    public int TradesLost { get; set; }

    public int TradesTotal { get; set; }

    public double BiggestWin { get; set; }

    public double BiggestLoss { get; set; }
}

internal sealed class PerformanceTracker
{
    private readonly string _filePath;
    private readonly double _initialBalance;
    private readonly double _leverage;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public PerformanceTracker(string filePath, double initialBalance, double leverage)
    {
        _filePath = filePath;
        _initialBalance = initialBalance;
        _leverage = leverage;
    }

    public PortfolioState Load()
    {
        if (!File.Exists(_filePath))
        {
            return CreateFreshState();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var state = JsonSerializer.Deserialize<PortfolioState>(json, _serializerOptions);
            if (state == null)
            {
                return CreateFreshState();
            }

            NormalizeState(state);
            return state;
        }
        catch (Exception)
        {
            return CreateFreshState();
        }
    }

    public void Save(PortfolioState state)
    {
        NormalizeState(state);

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(state, _serializerOptions);
        File.WriteAllText(_filePath, json);
    }

    public TradeRecord? CloseOpenPosition(PortfolioState state, double exitPrice, DateTime closedAt)
    {
        if (state.OpenPosition == null)
        {
            return null;
        }

        var position = state.OpenPosition;
        var rawProfitLoss = position.CalculatePnl(exitPrice);
        var profitLoss = Math.Max(-position.Margin, rawProfitLoss);
        var payout = position.Margin + profitLoss;
        state.Balance += payout;

        var record = new TradeRecord
        {
            OpenedAt = position.OpenedAt,
            ClosedAt = closedAt,
            Bias = position.Bias,
            EntryPrice = position.EntryPrice,
            ExitPrice = exitPrice,
            Quantity = position.Quantity,
            Margin = position.Margin,
            ProfitLoss = profitLoss,
            ReturnOnMargin = Math.Abs(position.Margin) < double.Epsilon ? 0.0 : profitLoss / position.Margin,
            Liquidated = rawProfitLoss < -position.Margin,
            Rationale = position.Rationale,
        };

        state.Statistics.RealizedPnl += profitLoss;
        state.Statistics.TradesTotal += 1;
        if (profitLoss > 0)
        {
            state.Statistics.TradesWon += 1;
        }
        else if (profitLoss < 0)
        {
            state.Statistics.TradesLost += 1;
        }

        state.Statistics.BiggestWin = Math.Max(state.Statistics.BiggestWin, profitLoss);
        state.Statistics.BiggestLoss = Math.Min(state.Statistics.BiggestLoss, profitLoss);

        state.History.Add(record);
        if (state.History.Count > 200)
        {
            state.History.RemoveRange(0, state.History.Count - 200);
        }

        state.OpenPosition = null;
        return record;
    }

    public SimulatedPosition? OpenPosition(
        PortfolioState state,
        TradeBias bias,
        double entryPrice,
        DateTime openedAt,
        string rationale)
    {
        if (bias == TradeBias.Neutral)
        {
            return null;
        }

        if (state.OpenPosition != null)
        {
            return state.OpenPosition;
        }

        var available = state.Balance;
        if (available <= 0)
        {
            return null;
        }

        var margin = available;
        var leverage = state.Leverage <= 0 ? _leverage : state.Leverage;
        var notional = margin * leverage;
        if (entryPrice <= 0)
        {
            return null;
        }

        var quantity = notional / entryPrice;
        if (quantity <= 0)
        {
            return null;
        }

        state.Balance -= margin;
        var position = new SimulatedPosition
        {
            Bias = bias,
            EntryPrice = entryPrice,
            Quantity = quantity,
            Margin = margin,
            Leverage = leverage,
            OpenedAt = openedAt,
            Rationale = rationale,
        };

        state.OpenPosition = position;
        return position;
    }

    public string DescribeState(PortfolioState state, double? markPrice)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Свободный баланс: {state.Balance.ToString("F2", CultureInfo.InvariantCulture)} USD");
        builder.AppendLine($"Реализованная прибыль: {state.Statistics.RealizedPnl.ToString("F2", CultureInfo.InvariantCulture)} USD");

        var winRate = state.Statistics.TradesTotal > 0
            ? state.Statistics.TradesWon / (double)state.Statistics.TradesTotal
            : 0.0;
        builder.AppendLine(
            $"Всего сделок: {state.Statistics.TradesTotal}, побед: {state.Statistics.TradesWon}, поражений: {state.Statistics.TradesLost}, win rate {(winRate * 100).ToString("F2", CultureInfo.InvariantCulture)}%.");

        var equity = state.Balance;
        if (state.OpenPosition != null)
        {
            var unrealized = markPrice.HasValue ? state.OpenPosition.CalculatePnl(markPrice.Value) : 0.0;
            equity += state.OpenPosition.Margin + unrealized;
            builder.AppendLine(
                $"Открытая позиция: {TradeBiasExtensions.ToRussian(state.OpenPosition.Bias)} с входом {state.OpenPosition.EntryPrice.ToString("F2", CultureInfo.InvariantCulture)} USD и плечом {state.OpenPosition.Leverage.ToString("F1", CultureInfo.InvariantCulture)}x.");
            if (markPrice.HasValue)
            {
                builder.AppendLine(
                    $"Текущая оценка прибыли: {unrealized.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture)} USD при марже {state.OpenPosition.Margin.ToString("F2", CultureInfo.InvariantCulture)} USD.");
            }
            else
            {
                builder.AppendLine(
                    $"Маржа позиции: {state.OpenPosition.Margin.ToString("F2", CultureInfo.InvariantCulture)} USD (нет цены для расчёта PnL).");
            }
        }

        builder.AppendLine(
            $"Текущая оценка капитала: {equity.ToString("F2", CultureInfo.InvariantCulture)} USD (старт {state.InitialBalance.ToString("F2", CultureInfo.InvariantCulture)} USD).");

        if (state.Statistics.BiggestWin > 0)
        {
            builder.AppendLine($"Максимальная прибыль за сделку: {state.Statistics.BiggestWin.ToString("F2", CultureInfo.InvariantCulture)} USD.");
        }

        if (state.Statistics.BiggestLoss < 0)
        {
            builder.AppendLine($"Максимальный убыток за сделку: {state.Statistics.BiggestLoss.ToString("F2", CultureInfo.InvariantCulture)} USD.");
        }

        if (state.History.Count > 0)
        {
            var last = state.History[^1];
            builder.AppendLine(
                $"Последняя закрытая сделка: {TradeBiasExtensions.ToRussian(last.Bias)} завершена {last.ClosedAt:O} с результатом {last.ProfitLoss.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture)} USD.");
        }

        return builder.ToString().TrimEnd();
    }

    private PortfolioState CreateFreshState()
    {
        return new PortfolioState
        {
            InitialBalance = _initialBalance,
            Balance = _initialBalance,
            Leverage = _leverage,
            History = new List<TradeRecord>(),
            Statistics = new PerformanceStatistics
            {
                BiggestLoss = 0.0,
                BiggestWin = 0.0,
            },
        };
    }

    private void NormalizeState(PortfolioState state)
    {
        state.InitialBalance = state.InitialBalance <= 0 ? _initialBalance : state.InitialBalance;
        state.Leverage = state.Leverage <= 0 ? _leverage : state.Leverage;
        state.History ??= new List<TradeRecord>();
        state.Statistics ??= new PerformanceStatistics();
    }
}

internal static class AiPromptBuilder
{
    public static string BuildPrompt(
        string timeframeKey,
        TimeframeOption timeframe,
        IReadOnlyList<ExchangeResult> results,
        AggregateSuggestionResult aggregate,
        PortfolioState state,
        TradeRecord? closedTrade,
        SimulatedPosition? openedPosition,
        double referencePrice)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Контекст анализа BTC/USDT:");
        builder.AppendLine($"- Таймфрейм: {timeframe.Label} ({timeframeKey})");
        builder.AppendLine($"- Средний балл: {aggregate.AverageScore.ToString("F2", CultureInfo.InvariantCulture)}");
        builder.AppendLine($"- Итоговая рекомендация: {aggregate.Suggestion}");
        builder.AppendLine($"- Средняя цена закрытия по биржам: {referencePrice.ToString("F2", CultureInfo.InvariantCulture)} USD");
        builder.AppendLine("Биржевые оценки:");
        foreach (var result in results)
        {
            var latest = result.Candles[^1];
            builder.AppendLine(
                $"  * {result.Name}: close {latest.Close.ToString("F2", CultureInfo.InvariantCulture)} USD, score {result.Score}, RSI {FormatNullable(result.Rsi)}, объём x{FormatNullable(result.VolumeRatio)}.");
        }

        builder.AppendLine();
        builder.AppendLine("Статистика тестового портфеля:");
        builder.AppendLine($"- Свободный баланс: {state.Balance.ToString("F2", CultureInfo.InvariantCulture)} USD");
        builder.AppendLine($"- Реализованная прибыль: {state.Statistics.RealizedPnl.ToString("F2", CultureInfo.InvariantCulture)} USD");

        var winRate = state.Statistics.TradesTotal > 0
            ? state.Statistics.TradesWon / (double)state.Statistics.TradesTotal
            : 0.0;
        builder.AppendLine(
            $"- Сделки: {state.Statistics.TradesTotal} всего, побед {state.Statistics.TradesWon}, поражений {state.Statistics.TradesLost}, win rate {(winRate * 100).ToString("F2", CultureInfo.InvariantCulture)}%.");

        if (closedTrade != null)
        {
            builder.AppendLine(
                $"- Последняя закрытая сделка: {TradeBiasExtensions.ToRussian(closedTrade.Bias)} {closedTrade.EntryPrice.ToString("F2", CultureInfo.InvariantCulture)} → {closedTrade.ExitPrice.ToString("F2", CultureInfo.InvariantCulture)}, результат {closedTrade.ProfitLoss.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture)} USD.");
        }

        if (openedPosition != null)
        {
            builder.AppendLine(
                $"- Текущая позиция: {TradeBiasExtensions.ToRussian(openedPosition.Bias)} по {openedPosition.EntryPrice.ToString("F2", CultureInfo.InvariantCulture)} USD, маржа {openedPosition.Margin.ToString("F2", CultureInfo.InvariantCulture)} USD, плечо {openedPosition.Leverage.ToString("F1", CultureInfo.InvariantCulture)}x.");
        }

        builder.AppendLine();
        builder.AppendLine("Задача: оцени данные, предложи проверку гипотез, дополнительные сигналы, управление риском и способы улучшить стратегию. Сделай вывод кратко, но по существу.");

        return builder.ToString();
    }

    private static string FormatNullable(double? value)
    {
        return value.HasValue ? value.Value.ToString("F2", CultureInfo.InvariantCulture) : "—";
    }
}

internal sealed class DeepSeekAdvisor
{
    private const string ModelName = "deepseek-chat";
    private readonly HttpClient _httpClient;

    public DeepSeekAdvisor(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public static HttpClient CreateHttpClient(string apiKey)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri("https://api.deepseek.com/"),
            Timeout = TimeSpan.FromSeconds(30),
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; BitcoinAnalysisBot/1.0)");
        return client;
    }

    public async Task<AiRecommendation?> TryGetRecommendationAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var request = new DeepSeekChatRequest
        {
            Model = ModelName,
            Messages = new List<DeepSeekMessage>
            {
                new("system", "Ты опытный криптоаналитик. Проанализируй информацию и предложи конструктивные шаги."),
                new("user", prompt),
            },
            Temperature = 0.3,
            MaxTokens = 600,
        };

        var payload = JsonSerializer.Serialize(request);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync("v1/chat/completions", content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (document.RootElement.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.ValueKind == JsonValueKind.Object &&
                first.TryGetProperty("message", out var messageElement) &&
                messageElement.ValueKind == JsonValueKind.Object &&
                messageElement.TryGetProperty("content", out var contentElement))
            {
                var text = contentElement.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return new AiRecommendation("DeepSeek", text.Trim());
                }
            }
        }

        return null;
    }

    private sealed class DeepSeekChatRequest
    {
        public string Model { get; set; } = ModelName;

        public List<DeepSeekMessage> Messages { get; set; } = new();

        public double Temperature { get; set; }

        public int MaxTokens { get; set; }
    }

    private sealed class DeepSeekMessage
    {
        public DeepSeekMessage()
        {
        }

        public DeepSeekMessage(string role, string content)
        {
            Role = role;
            Content = content;
        }

        public string Role { get; set; } = string.Empty;

        public string Content { get; set; } = string.Empty;
    }
}

internal sealed class AiRecommendation
{
    public AiRecommendation(string provider, string message)
    {
        Provider = provider;
        Message = message;
    }

    public string Provider { get; }

    public string Message { get; }
}

