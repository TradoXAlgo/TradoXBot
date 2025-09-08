using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TradoXBot.Models;

namespace TradoXBot.Services
{
    public class HistoricalDataFetcher
    {
        private readonly ILogger<HistoricalDataFetcher> _logger;
        private readonly HttpClient _httpClient;

        public HistoricalDataFetcher(ILogger<HistoricalDataFetcher> logger)
        {
            _logger = logger;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        }

        public async Task<List<Ohlc>> FetchHistoricalDataAsync(string symbol, string range, string interval)
        {
            try
            {
                var url = $"https://query1.finance.yahoo.com/v7/finance/chart/{symbol}.NS?range={range}&interval={interval}&indicators=quote&includeTimestamps=true";
                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(json);
                var chart = doc.RootElement.GetProperty("chart");
                var result = chart.GetProperty("result")[0];
                var timestamps = result.GetProperty("timestamp").EnumerateArray().Select(t => DateTimeOffset.FromUnixTimeSeconds(t.GetInt64()).DateTime).ToList();
                var indicators = result.GetProperty("indicators").GetProperty("quote")[0];

                var opens = indicators.GetProperty("open").EnumerateArray().Select(o => o.ValueKind == JsonValueKind.Null ? 0m : o.GetDecimal()).ToList();
                var highs = indicators.GetProperty("high").EnumerateArray().Select(h => h.ValueKind == JsonValueKind.Null ? 0m : h.GetDecimal()).ToList();
                var lows = indicators.GetProperty("low").EnumerateArray().Select(l => l.ValueKind == JsonValueKind.Null ? 0m : l.GetDecimal()).ToList();
                var closes = indicators.GetProperty("close").EnumerateArray().Select(c => c.ValueKind == JsonValueKind.Null ? 0m : c.GetDecimal()).ToList();
                var volumes = indicators.GetProperty("volume").EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Null ? 0L : v.GetInt64()).ToList();

                var ohlcList = new List<Ohlc>();
                for (int i = 0; i < timestamps.Count; i++)
                {
                    if (closes[i] == 0) continue; // Skip invalid data
                    ohlcList.Add(new Ohlc
                    {
                        Timestamp = timestamps[i],
                        Open = opens[i],
                        High = highs[i],
                        Low = lows[i],
                        Close = closes[i],
                        Volume = volumes[i]
                    });
                }

                return ohlcList.OrderBy(o => o.Timestamp).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error fetching historical data for {symbol} (range: {range}, interval: {interval}): {ex.Message}");
                return new List<Ohlc>();
            }
        }

        public async Task<decimal?> GetEmaAsync(string symbol, int period, string interval = "1d")
        {
            try
            {
                var range = interval == "1d" ? "3mo" : "7d"; //3mo
                var historical = await FetchHistoricalDataAsync(symbol, range, interval);
                var closePrices = historical.Select(r => r.Close).ToList();
                if (closePrices.Count < period)
                {
                    _logger.LogWarning($"Not enough data for EMA {period} on {symbol}. Found {closePrices.Count} periods.");
                    return null;
                }

                decimal multiplier = 2m / (period + 1);
                decimal sma = closePrices.Take(period).Average();
                decimal ema = sma;
                for (int i = period; i < closePrices.Count; i++)
                {
                    ema = (closePrices[i] * multiplier) + (ema * (1 - multiplier));
                }

                return ema;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error calculating EMA for {symbol}: {ex.Message}");
                return null;
            }
        }

        public async Task<decimal?> GetEma5Async(string symbol) => await GetEmaAsync(symbol, 5);
        public async Task<decimal?> GetEma7Async(string symbol) => await GetEmaAsync(symbol, 7, "5m");
        public async Task<decimal?> GetEma9Async(string symbol) => await GetEmaAsync(symbol, 9);
        public async Task<decimal?> GetEma21Async(string symbol) => await GetEmaAsync(symbol, 21);
        public async Task<decimal?> GetEma50Async(string symbol) => await GetEmaAsync(symbol, 44);

        public async Task<decimal?> GetAtrAsync(string symbol, int period = 14, string interval = "1d")
        {
            try
            {
                var range = interval == "1d" ? "1mo" : "7d";
                var historical = await FetchHistoricalDataAsync(symbol, range, interval);
                var highPrices = historical.Select(r => r.High).ToList();
                var lowPrices = historical.Select(r => r.Low).ToList();
                var closePrices = historical.Select(r => r.Close).ToList();

                if (closePrices.Count < period + 1)
                {
                    _logger.LogWarning($"Not enough data for ATR {period} on {symbol}. Found {closePrices.Count} periods.");
                    return null;
                }

                decimal sumTr = 0;
                for (int i = 1; i <= period; i++)
                {
                    decimal tr = Math.Max(highPrices[i] - lowPrices[i],
                        Math.Max(Math.Abs(highPrices[i] - closePrices[i - 1]), Math.Abs(lowPrices[i] - closePrices[i - 1])));
                    sumTr += tr;
                }
                return sumTr / period;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error calculating ATR for {symbol}: {ex.Message}");
                return null;
            }
        }

        public async Task<Ohlc?> GetTodaysOhlcAsync(string symbol, string interval = "1d")
        {
            try
            {
                var range = interval == "1d" ? "7d" : "1d";
                var historical = await FetchHistoricalDataAsync(symbol, range, interval);
                var lastRow = historical.OrderByDescending(r => r.Timestamp).FirstOrDefault();
                if (lastRow == null)
                {
                    _logger.LogWarning($"No OHLC data for {symbol} with interval {interval}.");
                    return null;
                }

                return lastRow;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error fetching OHLC for {symbol} with interval {interval}: {ex.Message}");
                return null;
            }
        }

        public async Task<decimal?> GetVolumeSmaAsync(string symbol, int period = 20, string interval = "1d")
        {
            try
            {
                var range = interval == "1d" ? "1mo" : "7d";
                var historical = await FetchHistoricalDataAsync(symbol, range, interval);
                var volumes = historical.Select(r => r.Volume).ToList();
                if (volumes.Count < period)
                {
                    _logger.LogWarning($"Not enough data for Volume SMA {period} on {symbol}. Found {volumes.Count} periods.");
                    return null;
                }
                return (decimal)volumes.TakeLast(period).Average();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error calculating Volume SMA for {symbol}: {ex.Message}");
                return null;
            }
        }

        public async Task<decimal?> GetRsiAsync(string symbol, int period = 14, string interval = "1d")
        {
            try
            {
                var range = interval == "1d" ? "1mo" : "7d";
                var historical = await FetchHistoricalDataAsync(symbol, range, interval);
                var closePrices = historical.Select(r => r.Close).ToList();
                if (closePrices.Count < period + 1)
                {
                    _logger.LogWarning($"Not enough data for RSI {period} on {symbol}. Found {closePrices.Count} periods.");
                    return null;
                }

                decimal gains = 0, losses = 0;
                for (int i = closePrices.Count - period - 1; i < closePrices.Count - 1; i++)
                {
                    decimal change = closePrices[i + 1] - closePrices[i];
                    if (change > 0) gains += change;
                    else losses -= change;
                }
                decimal avgGain = gains / period;
                decimal avgLoss = losses / period;
                decimal rs = avgGain / (avgLoss == 0 ? 1 : avgLoss);
                return 100 - (100 / (1 + rs));
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error calculating RSI for {symbol}: {ex.Message}");
                return null;
            }
        }

        public async Task<bool> IsUpTrendAsync(string symbol)
        {
            try
            {
                var ema50 = await GetEma50Async(symbol);
                var quote = await GetTodaysOhlcAsync(symbol);
                if (!ema50.HasValue || quote == null)
                {
                    _logger.LogWarning($"Cannot determine uptrend for {symbol}. EMA50: {ema50}, OHLC: {quote}");
                    return false;
                }
                return quote.Close > ema50.Value;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error checking uptrend for {symbol}: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> IsBreakoutAsync(string symbol)
        {
            try
            {
                var historical = await FetchHistoricalDataAsync(symbol, "1mo", "1d");
                var closePrices = historical.Select(r => r.Close).ToList();
                var volumes = historical.Select(r => r.Volume).ToList();

                if (closePrices.Count < 20)
                {
                    _logger.LogWarning($"Not enough data for breakout on {symbol}. Found {closePrices.Count} periods.");
                    return false;
                }

                decimal sma20 = closePrices.TakeLast(20).Average();
                decimal sumSquaredDiff = closePrices.TakeLast(20).Sum(p => (p - sma20) * (p - sma20));
                decimal stdDev = (decimal)Math.Sqrt((double)(sumSquaredDiff / 20));
                decimal upperBand = sma20 + (2 * stdDev);

                decimal? rsi = await GetRsiAsync(symbol);
                decimal? volumeSma = await GetVolumeSmaAsync(symbol, 20);

                if (!rsi.HasValue || !volumeSma.HasValue)
                {
                    _logger.LogWarning($"Cannot calculate breakout for {symbol}. RSI: {rsi}, VolumeSMA: {volumeSma}");
                    return false;
                }

                return closePrices.Last() > upperBand && rsi > 50 && volumes.Last() > volumeSma;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error checking breakout for {symbol}: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> IsScalpingBreakoutAsync(string symbol)
        {
            try
            {
                var historical = await FetchHistoricalDataAsync(symbol, "7d", "5m");
                var closePrices = historical.Select(r => r.Close).ToList();
                var volumes = historical.Select(r => r.Volume).ToList();

                if (closePrices.Count < 20)
                {
                    _logger.LogWarning($"Not enough data for scalping breakout on {symbol}. Found {closePrices.Count} periods.");
                    return false;
                }

                decimal sma20 = closePrices.TakeLast(20).Average();
                decimal sumSquaredDiff = closePrices.TakeLast(20).Sum(p => (p - sma20) * (p - sma20));
                decimal stdDev = (decimal)Math.Sqrt((double)(sumSquaredDiff / 20));
                decimal upperBand = sma20 + (2 * stdDev);

                decimal? rsi = await GetRsiAsync(symbol, 14, "5m");
                decimal? volumeSma = await GetVolumeSmaAsync(symbol, 20, "5m");

                if (!rsi.HasValue || !volumeSma.HasValue)
                {
                    _logger.LogWarning($"Cannot calculate scalping breakout for {symbol}. RSI: {rsi}, VolumeSMA: {volumeSma}");
                    return false;
                }

                return closePrices.Last() > upperBand && rsi > 60 && volumes.Last() > volumeSma;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error checking scalping breakout for {symbol}: {ex.Message}");
                return false;
            }
        }

    }
}