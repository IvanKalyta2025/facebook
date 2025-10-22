using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
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

            var (suggestion, averageScore) = ExchangeAnalyzer.AggregateSuggestion(results);
            Console.WriteLine($"Средний балл по биржам: {averageScore:F2}");
            Console.WriteLine($"Рекомендация: {suggestion}");
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

    public static (string Suggestion, double AverageScore) AggregateSuggestion(IEnumerable<ExchangeResult> results)
    {
        var scores = results.Select(result => result.Score).ToList();
        if (scores.Count == 0)
        {
            return ("Нет данных для рекомендации", 0.0);
        }

        var averageScore = scores.Average();
        string suggestion;
        if (averageScore >= 2)
        {
            suggestion = "Преимущество быков – стоит рассмотреть длинную позицию (лонг).";
        }
        else if (averageScore <= -2)
        {
            suggestion = "Преимущество медведей – стоит рассмотреть короткую позицию (шорт).";
        }
        else
        {
            suggestion = "Сигналы смешанные – уместно подождать подтверждения перед входом.";
        }

        return (suggestion, averageScore);
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

