using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Polly;
using TradoXBot.Models;
using Newtonsoft.Json.Linq;
using Telegram.Bot;
using System.Text.Json;

namespace TradoXBot.Services;

public class HistoricalDataFetcher
{
    private readonly ILogger<HistoricalDataFetcher> _logger;
    private readonly IConfiguration _configuration;
    private readonly IAsyncPolicy _retryPolicy;
    private readonly TimeZoneInfo _istTimeZone;
    private readonly HttpClient _httpClient;
    private const string BaseUrl = "https://query1.finance.yahoo.com/v7/finance/chart/";

    public HistoricalDataFetcher(IConfiguration configuration, ILogger<HistoricalDataFetcher> logger)
    {
        _configuration = configuration;
        _httpClient = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        _logger = logger;
        _istTimeZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        _retryPolicy = Policy.Handle<HttpRequestException>()
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                (exception, timeSpan, retryCount, context) =>
                {
                    _logger.LogWarning("Retry {RetryCount} after {TimeSpan} due to {ExceptionMessage}",
                        retryCount, timeSpan, exception.Message);
                });
    }

    public async Task<bool> IsPriceAboveEmaAsync(string symbol, int period, DateTime? date = null)
    {
        try
        {
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _istTimeZone);
            date ??= now;
            var quote = await GetQuoteAsync(symbol, date.Value);
            var ema = await GetEmaAsync(symbol, period, "1d", date);
            if (quote == null || quote.LastPrice <= 0 || !ema.HasValue || ema.Value <= 0)
            {
                _logger.LogWarning("Invalid quote or EMA for {Symbol} on {Date}", symbol, date.Value);
                return false;
            }
            bool isAbove = quote.LastPrice > ema.Value;
            _logger.LogInformation("{Symbol} price {Price:F2} {IsAbove} EMA{Period} {Ema:F2} on {Date}",
                symbol, quote.LastPrice, isAbove ? ">" : "<=", period, ema.Value, date.Value);
            return isAbove;
        }
        catch (Exception ex)
        {
            _logger.LogError("Error checking price above EMA{Period} for {Symbol}: {Message}", period, symbol, ex.Message);
            return false;
        }
    }

    public async Task<bool> IsBreakoutAsync(string symbol, DateTime? date = null)
    {
        try
        {
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _istTimeZone);
            date ??= now;
            var historicalData = await FetchHistoricalDataAsync(symbol, date.Value, "1d", 20);
            if (historicalData.Count < 20)
            {
                _logger.LogWarning("Insufficient data for breakout check for {Symbol} on {Date}", symbol, date.Value);
                return false;
            }
            var upperBand = CalculateBollingerBands(historicalData.Select(d => d.Close).ToList(), 20, 2).Upper;
            var latestPrice = historicalData.Last().Close;
            bool isBreakout = latestPrice > upperBand;
            _logger.LogInformation("{Symbol} price {Price:F2} {IsBreakout} Bollinger Upper Band {Upper:F2} on {Date}",
                symbol, latestPrice, isBreakout ? ">" : "<=", upperBand, date.Value);
            return isBreakout;
        }
        catch (Exception ex)
        {
            _logger.LogError("Error checking breakout for {Symbol}: {Message}", symbol, ex.Message);
            return false;
        }
    }

    public async Task<decimal?> GetRsiAsync(string symbol, DateTime? date = null)
    {
        try
        {
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _istTimeZone);
            date ??= now;
            var historicalData = await FetchHistoricalDataAsync(symbol, date.Value, "1d", 15);
            if (historicalData.Count < 14)
            {
                _logger.LogWarning("Insufficient data for RSI calculation for {Symbol} on {Date}", symbol, date.Value);
                return null;
            }
            var rsi = CalculateRsi(historicalData.Select(d => d.Close).ToList(), 14);
            _logger.LogInformation("RSI for {Symbol} on {Date}: {Rsi:F2}", symbol, date.Value, rsi);
            return rsi;
        }
        catch (Exception ex)
        {
            _logger.LogError("Error calculating RSI for {Symbol}: {Message}", symbol, ex.Message);
            return null;
        }
    }

    public async Task<bool> IsVolumeAboveSmaAsync(string symbol, int period, DateTime? date = null)
    {
        try
        {
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _istTimeZone);
            date ??= now;
            var historicalData = await FetchHistoricalDataAsync(symbol, date.Value, "1d", period + 1);
            if (historicalData.Count < period)
            {
                _logger.LogWarning("Insufficient data for volume SMA check for {Symbol} on {Date}", symbol, date.Value);
                return false;
            }
            var latestVolume = historicalData.Last().Volume;
            var sma = historicalData.TakeLast(period).Average(d => d.Volume);
            bool isAbove = latestVolume > sma;
            _logger.LogInformation("{Symbol} volume {Volume} {IsAbove} SMA{Period} {Sma:F2} on {Date}",
                symbol, latestVolume, isAbove ? ">" : "<=", period, sma, date.Value);
            return isAbove;
        }
        catch (Exception ex)
        {
            _logger.LogError("Error checking volume above SMA for {Symbol}: {Message}", symbol, ex.Message);
            return false;
        }
    }

    public async Task<bool> IsVolumeBelowSmaAsync(string symbol, int period, DateTime? date = null)
    {
        try
        {
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _istTimeZone);
            date ??= now;
            var historicalData = await FetchHistoricalDataAsync(symbol, date.Value, "1d", period);
            if (historicalData.Count < period)
            {
                _logger.LogWarning("Insufficient data for volume SMA check for {Symbol} on {Date}", symbol, date.Value);
                return false;
            }
            var latestVolume = historicalData.Last().Volume;
            var sma = historicalData.TakeLast(period).Average(d => d.Volume);
            bool isBelow = latestVolume < sma;
            _logger.LogInformation("{Symbol} volume {Volume} {IsBelow} SMA{Period} {Sma:F2} on {Date}",
                symbol, latestVolume, isBelow ? "<" : ">=", period, sma, date.Value);
            return isBelow;
        }
        catch (Exception ex)
        {
            _logger.LogError("Error checking volume below SMA for {Symbol}: {Message}", symbol, ex.Message);
            return false;
        }
    }

    public async Task<decimal?> GetAtrAsync(string symbol, int period, string interval = "1d", DateTime? date = null)
    {
        try
        {
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _istTimeZone);
            date ??= now;
            var historicalData = await FetchHistoricalDataAsync(symbol, date.Value, interval, period + 1);
            if (historicalData.Count < period + 1)
            {
                _logger.LogWarning("Insufficient data for ATR calculation for {Symbol} on {Date}", symbol, date.Value);
                return null;
            }
            var atr = CalculateAtr(historicalData, period);
            _logger.LogInformation("ATR{Period} for {Symbol} on {Date} (interval: {Interval}): {Atr:F2}",
                period, symbol, date.Value, interval, atr);
            return atr;
        }
        catch (Exception ex)
        {
            _logger.LogError("Error calculating ATR for {Symbol}: {Message}", symbol, ex.Message);
            return null;
        }
    }

    public async Task<decimal?> GetEmaAsync(string symbol, int period, string interval = "1d", DateTime? date = null)
    {
        try
        {
            var range = interval == "1d" ? "3mo" : "7d"; //3mo
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _istTimeZone);
            date ??= now;
            var historicalData = await FetchHistoricalDataAsync(symbol, date.Value, interval, period * 2);
            if (historicalData.Count < period)
            {
                _logger.LogWarning("Insufficient data for EMA{Period} calculation for {Symbol} on {Date}", period, symbol, date.Value);
                return null;
            }
            var ema = CalculateEma(historicalData.Select(d => d.Close).ToList(), period);
            _logger.LogInformation("EMA{Period} for {Symbol} on {Date} (interval: {Interval}): {Ema:F2}",
                period, symbol, date.Value, interval, ema);
            return ema;
        }
        catch (Exception ex)
        {
            _logger.LogError("Error calculating EMA{Period} for {Symbol}: {Message}", period, symbol, ex.Message);
            return null;
        }
    }

    public async Task<Ohlc> GetTodaysOhlcAsync(string symbol, string interval = "1d")
    {
        try
        {
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _istTimeZone);
            var historicalData = await FetchHistoricalDataAsync(symbol, now, interval, 1);
            if (historicalData.Count == 0)
            {
                _logger.LogWarning("No OHLC data for {Symbol} on {Date}", symbol, now);
                return new Ohlc();
            }
            var data = historicalData.Last();
            var ohlc = new Ohlc
            {
                Open = data.Open,
                High = data.High,
                Low = data.Low,
                Close = data.Close,
                Volume = data.Volume,
                Timestamp = data.Timestamp
            };
            _logger.LogInformation("OHLC for {Symbol} on {Date}: O={Open:F2}, H={High:F2}, L={Low:F2}, C={Close:F2}",
                symbol, now, ohlc.Open, ohlc.High, ohlc.Low, ohlc.Close);
            return ohlc;
        }
        catch (Exception ex)
        {
            _logger.LogError("Error fetching OHLC for {Symbol}: {Message}", symbol, ex.Message);
            return new Ohlc();
        }
    }

    public async Task<Quote> GetQuoteAsync(string symbol, DateTime date)
    {
        try
        {
            var historicalData = await FetchHistoricalDataAsync(symbol, date, "1d", 1);
            if (historicalData.Count == 0)
            {
                _logger.LogWarning("No quote data for {Symbol} on {Date}", symbol, date);
                return new Quote();
            }
            var data = historicalData.Last();
            var quote = new Quote
            {
                LastPrice = data.Close,
                Open = data.Open,
                Close = data.Close,
                Volume = data.Volume,
                High = data.High,
                Low = data.Low,
                PrevClose = historicalData.Count > 1 ? historicalData[historicalData.Count - 2].Close : data.Close
            };
            _logger.LogInformation("Quote for {Symbol} on {Date}: LastPrice={LastPrice:F2}", symbol, date, quote.LastPrice);
            return quote;
        }
        catch (Exception ex)
        {
            _logger.LogError("Error fetching quote for {Symbol}: {Message}", symbol, ex.Message);
            return new Quote();
        }
    }

    private async Task<List<Ohlc>> FetchHistoricalDataAsync(string symbol, DateTime date, string interval, int period)
    {
        try
        {
            var endTime = (long)(TimeZoneInfo.ConvertTimeToUtc(date, _istTimeZone) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            var startTime = (long)(TimeZoneInfo.ConvertTimeToUtc(date.AddDays(-period * (interval == "5m" ? 1 : 2)), _istTimeZone) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            var url = $"{symbol}.NS?range={period}d&interval={interval}&includePrePost=false&events=history";

            // var response = await _retryPolicy.ExecuteAsync(async () =>
            // {
            //     var request = new HttpRequestMessage(HttpMethod.Get, url);
            //     var result = await _httpClient.SendAsync(request);
            //     return result;
            // });
            //response.EnsureSuccessStatusCode();

            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();


            var content = await response.Content.ReadAsStringAsync();
            var jsonDoc = JsonDocument.Parse(content);
            var result = jsonDoc.RootElement.GetProperty("chart").GetProperty("result")[0];
            var timestamps = result.GetProperty("timestamp").EnumerateArray().Select(t => t.GetInt64()).ToList();
            var indicators = result.GetProperty("indicators").GetProperty("quote")[0];
            
            var opens = indicators.GetProperty("open").EnumerateArray().Select(o => o.ValueKind == JsonValueKind.Null ? 0m : o.GetDecimal()).ToList();
            var highs = indicators.GetProperty("high").EnumerateArray().Select(h => h.ValueKind == JsonValueKind.Null ? 0m : h.GetDecimal()).ToList();
            var lows = indicators.GetProperty("low").EnumerateArray().Select(l => l.ValueKind == JsonValueKind.Null ? 0m : l.GetDecimal()).ToList();
            var closes = indicators.GetProperty("close").EnumerateArray().Select(c => c.ValueKind == JsonValueKind.Null ? 0m : c.GetDecimal()).ToList();
            var volumes = indicators.GetProperty("volume").EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Null ? 0L : v.GetInt64()).ToList();


            var historicalData = new List<Ohlc>();
            for (int i = 0; i < timestamps.Count; i++)
            {
                historicalData.Add(new Ohlc
                {
                    Timestamp = DateTimeOffset.FromUnixTimeSeconds(timestamps[i]).DateTime,
                    Open = opens[i],
                    High = highs[i],
                    Low = lows[i],
                    Close = closes[i],
                    Volume = volumes[i]
                });
            }
            return historicalData.OrderBy(d => d.Timestamp).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError("Error fetching historical data for {Symbol}: {Message}", symbol, ex.Message);
            return new List<Ohlc>();
        }
    }

    private decimal CalculateEma(List<decimal> prices, int period)
    {
        if (prices.Count < period) return 0;
        decimal k = 2m / (period + 1);
        decimal ema = prices.Take(period).Average();
        for (int i = period; i < prices.Count; i++)
        {
            ema = (prices[i] * k) + (ema * (1 - k));
        }
        return ema;
    }

    private decimal CalculateRsi(List<decimal> prices, int period)
    {
        if (prices.Count < period) return 0;
        var gains = new List<decimal>();
        var losses = new List<decimal>();
        for (int i = 1; i < prices.Count; i++)
        {
            var change = prices[i] - prices[i - 1];
            if (change > 0)
                gains.Add(change);
            else
                losses.Add(-change);
        }
        var avgGain = gains.TakeLast(period).Average();
        var avgLoss = losses.TakeLast(period).Average();
        if (avgLoss == 0) return 100;
        var rs = avgGain / avgLoss;
        return 100 - (100 / (1 + rs));
    }

    private decimal CalculateAtr(List<Ohlc> data, int period)
    {
        if (data.Count < period + 1) return 0;
        var trList = new List<decimal>();
        for (int i = 1; i < data.Count; i++)
        {
            var high = data[i].High;
            var low = data[i].Low;
            var prevClose = data[i - 1].Close;
            var tr1 = high - low;
            var tr2 = Math.Abs(high - prevClose);
            var tr3 = Math.Abs(low - prevClose);
            var trueRange = Math.Max(tr1, Math.Max(tr2, tr3));
            trList.Add(trueRange);
        }
        return trList.TakeLast(period).Average();
    }

    private (decimal Upper, decimal Lower) CalculateBollingerBands(List<decimal> prices, int period, decimal multiplier)
    {
        if (prices.Count < period) return (0, 0);
        var sma = prices.TakeLast(period).Average();
        var variance = prices.TakeLast(period).Sum(p => (p - sma) * (p - sma)) / period;
        var stdDev = (decimal)Math.Sqrt((double)variance);
        return (sma + multiplier * stdDev, sma - multiplier * stdDev);
    }

}