using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradoXBot.Models;

namespace TradoXBot.Services;

public class Backtester
{
    private readonly ILogger<Backtester> _logger;
    private readonly HistoricalDataFetcher _historicalFetcher;

    public Backtester(ILogger<Backtester> logger, HistoricalDataFetcher historicalFetcher)
    {
        _logger = logger;
        _historicalFetcher = historicalFetcher;
    }

    public async Task<(int Trades, decimal TotalProfitLoss, double WinRate)> BacktestSwingStrategyAsync(string symbol, string range = "1y")
    {
        try
        {
            _logger.LogInformation($"Starting swing backtest for {symbol} over {range}");
            var historical = await _historicalFetcher.FetchHistoricalDataAsync(symbol, range, "1d");
            if (historical.Count < 50)
            {
                _logger.LogWarning($"Insufficient data for swing backtesting {symbol}. Found {historical.Count} periods.");
                return (0, 0, 0);
            }

            int trades = 0;
            decimal totalProfitLoss = 0;
            int wins = 0;
            decimal capital = 100000;
            bool inPosition = false;
            decimal buyPrice = 0;
            int quantity = 0;
            DateTime buyDate = DateTime.MinValue;
            DateTime expiryDate = DateTime.MinValue;

            for (int i = 50; i < historical.Count - 1; i++)
            {
                var closePrices = historical.Take(i + 1).Select(r => r.Close).ToList();
                var volumes = historical.Take(i + 1).Select(r => r.Volume).ToList();

                decimal sma20 = closePrices.TakeLast(20).Average();
                decimal sumSquaredDiff = closePrices.TakeLast(20).Sum(p => (p - sma20) * (p - sma20));
                decimal stdDev = (decimal)Math.Sqrt((double)(sumSquaredDiff / 20));
                decimal upperBand = sma20 + (2 * stdDev);
                decimal? rsi = await CalculateRsiAsync(closePrices, 14);
                decimal? volumeSma = (decimal)volumes.TakeLast(20).Average();
                decimal? ema50 = await CalculateEmaAsync(closePrices, 50);
                decimal? atr = await CalculateAtrAsync(historical.Take(i + 1).ToList(), 14);
                decimal? ema21 = await CalculateEmaAsync(closePrices, 21);
                decimal? ema9 = await CalculateEmaAsync(closePrices, 9);
                decimal? ema5 = await CalculateEmaAsync(closePrices, 5);

                var current = historical[i];
                var nextDay = historical[i + 1];

                if (!inPosition && !IsMonthEnd(current.Timestamp) && rsi.HasValue && volumeSma.HasValue && ema50.HasValue)
                {
                    bool isBreakout = current.Close > upperBand && rsi > 60 && current.Volume > volumeSma;
                    bool isUpTrend = current.Close > ema50.Value;
                    if (isBreakout && isUpTrend)
                    {
                        decimal riskPerShare = atr.HasValue ? atr.Value * 2 : current.Close * 0.025m;
                        decimal riskPerTrade = capital * 0.01m;
                        quantity = (int)(riskPerTrade / riskPerShare);
                        if (quantity > 0)
                        {
                            buyPrice = current.Close;
                            buyDate = current.Timestamp;
                            expiryDate = AddTradingDays(buyDate, 10);
                            inPosition = true;
                            _logger.LogInformation($"Backtest Swing Buy: {symbol} at ₹{buyPrice:F2} on {buyDate:yyyy-MM-dd}");
                        }
                    }
                }

                if (inPosition)
                {
                    decimal profitPercent = (nextDay.Close - buyPrice) / buyPrice * 100;
                    decimal stopLossPrice = buyPrice - (atr.HasValue ? atr.Value * 2 : buyPrice * 0.025m);
                    bool sell = false;
                    string sellReason = "";

                    if (profitPercent >= 10)
                    {
                        sell = true;
                        sellReason = ">10% profit";
                    }
                    else if (nextDay.Timestamp.Date >= expiryDate.Date)
                    {
                        sell = true;
                        sellReason = "expired";
                    }
                    else if (nextDay.Close < stopLossPrice)
                    {
                        sell = true;
                        sellReason = "hit stop-loss";
                    }
                    else if (ema21.HasValue && nextDay.Close < ema21.Value)
                    {
                        sell = true;
                        sellReason = "below EMA21";
                    }
                    else if (ema9.HasValue && ema5.HasValue && volumeSma.HasValue &&
                             nextDay.Close < ema9.Value && nextDay.Close < nextDay.Open &&
                             nextDay.Close < ema5.Value && nextDay.Volume < volumeSma)
                    {
                        sell = true;
                        sellReason = "hybrid EMA9 stop-loss";
                    }

                    if (profitPercent > 5)
                    {
                        decimal trailingStop = buyPrice + (buyPrice * 0.02m);
                        if (nextDay.Close < trailingStop)
                        {
                            sell = true;
                            sellReason = "trailing stop-loss";
                        }
                    }

                    if (sell)
                    {
                        decimal profitLoss = (nextDay.Close - buyPrice) * quantity;
                        totalProfitLoss += profitLoss;
                        trades++;
                        if (profitLoss > 0) wins++;
                        _logger.LogInformation($"Backtest Swing Sell: {symbol} at ₹{nextDay.Close:F2} on {nextDay.Timestamp:yyyy-MM-dd}, Reason: {sellReason}, P/L: ₹{profitLoss:F2}");
                        inPosition = false;
                    }
                }
            }

            double winRate = trades > 0 ? (double)wins / trades : 0;
            _logger.LogInformation($"Swing Backtest Result for {symbol}: Trades={trades}, Total P/L=₹{totalProfitLoss:F2}, Win Rate={winRate:P2}");
            return (trades, totalProfitLoss, winRate);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error in swing backtest for {symbol}: {ex.Message}");
            return (0, 0, 0);
        }
    }

    public async Task<(int Trades, decimal TotalProfitLoss, double WinRate)> BacktestScalpingStrategyAsync(string symbol, string range = "1mo")
    {
        try
        {
            _logger.LogInformation($"Starting scalping backtest for {symbol} over {range}");
            var historical = await _historicalFetcher.FetchHistoricalDataAsync(symbol, range, "5m");
            if (historical.Count < 20)
            {
                _logger.LogWarning($"Insufficient data for scalping backtesting {symbol}. Found {historical.Count} periods.");
                return (0, 0, 0);
            }

            int trades = 0;
            decimal totalProfitLoss = 0;
            int wins = 0;
            decimal capital = 100000;
            bool inPosition = false;
            decimal buyPrice = 0;
            int quantity = 0;

            for (int i = 20; i < historical.Count - 1; i++)
            {
                var closePrices = historical.Take(i + 1).Select(r => r.Close).ToList();
                var volumes = historical.Take(i + 1).Select(r => r.Volume).ToList();

                decimal sma20 = closePrices.TakeLast(20).Average();
                decimal sumSquaredDiff = closePrices.TakeLast(20).Sum(p => (p - sma20) * (p - sma20));
                decimal stdDev = (decimal)Math.Sqrt((double)(sumSquaredDiff / 20));
                decimal upperBand = sma20 + (2 * stdDev);
                decimal? rsi = await CalculateRsiAsync(closePrices, 14);
                decimal? volumeSma = (decimal)volumes.TakeLast(20).Average();
                decimal? ema7 = await CalculateEmaAsync(closePrices, 7);
                decimal? atr = await CalculateAtrAsync(historical.Take(i + 1).ToList(), 14);

                var current = historical[i];
                var next = historical[i + 1];

                if (current.Timestamp.TimeOfDay < new TimeSpan(9, 15, 0) || current.Timestamp.TimeOfDay > new TimeSpan(14, 30, 0))
                    continue;

                if (!inPosition && rsi.HasValue && volumeSma.HasValue)
                {
                    bool isBreakout = current.Close > upperBand && rsi > 60 && current.Volume > volumeSma;
                    if (isBreakout)
                    {
                        decimal riskPerShare = atr.HasValue ? atr.Value : current.Close * 0.005m;
                        decimal riskPerTrade = capital * 0.01m;
                        quantity = (int)(riskPerTrade / riskPerShare);
                        if (quantity > 0)
                        {
                            buyPrice = current.Close;
                            inPosition = true;
                            _logger.LogInformation($"Backtest Scalping Buy: {symbol} at ₹{buyPrice:F2} on {current.Timestamp:yyyy-MM-dd HH:mm}");
                        }
                    }
                }

                if (inPosition)
                {
                    decimal profitPercent = (next.Close - buyPrice) / buyPrice * 100;
                    bool sell = false;
                    string sellReason = "";

                    if (profitPercent >= 1)
                    {
                        sell = true;
                        sellReason = ">1% profit";
                    }
                    else if (profitPercent <= (decimal)-0.5)
                    {
                        sell = true;
                        sellReason = "<0.5% stop-loss";
                    }
                    else if (ema7.HasValue && next.Close < ema7.Value)
                    {
                        sell = true;
                        sellReason = "close below EMA7";
                    }

                    if (sell)
                    {
                        decimal profitLoss = (next.Close - buyPrice) * quantity;
                        totalProfitLoss += profitLoss;
                        trades++;
                        if (profitLoss > 0) wins++;
                        _logger.LogInformation($"Backtest Scalping Sell: {symbol} at ₹{next.Close:F2} on {next.Timestamp:yyyy-MM-dd HH:mm}, Reason: {sellReason}, P/L: ₹{profitLoss:F2}");
                        inPosition = false;
                    }
                }
            }

            double winRate = trades > 0 ? (double)wins / trades : 0;
            _logger.LogInformation($"Scalping Backtest Result for {symbol}: Trades={trades}, Total P/L=₹{totalProfitLoss:F2}, Win Rate={winRate:P2}");
            return (trades, totalProfitLoss, winRate);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error in scalping backtest for {symbol}: {ex.Message}");
            return (0, 0, 0);
        }
    }

    private async Task<decimal?> CalculateEmaAsync(List<decimal> prices, int period)
    {
        if (prices.Count < period) return null;
        decimal multiplier = 2m / (period + 1);
        decimal sma = prices.Take(period).Average();
        decimal ema = sma;
        for (int i = period; i < prices.Count; i++)
        {
            ema = (prices[i] * multiplier) + (ema * (1 - multiplier));
        }
        return ema;
    }

    private async Task<decimal?> CalculateAtrAsync(List<Ohlc> historical, int period)
    {
        if (historical.Count < period + 1) return null;
        decimal sumTr = 0;
        for (int i = 1; i <= period; i++)
        {
            var current = historical[historical.Count - i];
            var previous = historical[historical.Count - i - 1];
            decimal tr = Math.Max(current.High - current.Low,
                Math.Max(Math.Abs(current.High - previous.Close), Math.Abs(current.Low - previous.Close)));
            sumTr += tr;
        }
        return sumTr / period;
    }

    private async Task<decimal?> CalculateRsiAsync(List<decimal> prices, int period)
    {
        if (prices.Count < period + 1) return null;
        decimal gains = 0, losses = 0;
        for (int i = prices.Count - period - 1; i < prices.Count - 1; i++)
        {
            decimal change = prices[i + 1] - prices[i];
            if (change > 0) gains += change;
            else losses -= change;
        }
        decimal avgGain = gains / period;
        decimal avgLoss = losses / period;
        decimal rs = avgGain / (avgLoss == 0 ? 1 : avgLoss);
        return 100 - (100 / (1 + rs));
    }

    private bool IsMonthEnd(DateTime date)
    {
        var lastDayOfMonth = new DateTime(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month));
        return date.Date >= lastDayOfMonth.AddDays(-3);
    }

    private DateTime AddTradingDays(DateTime startDate, int tradingDays)
    {
        int daysToAdd = tradingDays;
        DateTime resultDate = startDate;
        int direction = tradingDays >= 0 ? 1 : -1;

        while (daysToAdd != 0)
        {
            resultDate = resultDate.AddDays(direction);
            if (IsTradingDay(resultDate))
            {
                daysToAdd -= direction;
            }
        }

        return resultDate;
    }

    private bool IsTradingDay(DateTime date)
    {
        return date.DayOfWeek != DayOfWeek.Saturday && date.DayOfWeek != DayOfWeek.Sunday;
    }
}