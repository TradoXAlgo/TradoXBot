using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradoXBot.Models;

namespace TradoXBot.Services;

public class BacktestService
{
    private readonly ILogger<BacktestService> _logger;
    private readonly HistoricalDataFetcher _historicalFetcher;
    private readonly ChartinkScraper _chartinkScraper;
    private readonly MongoDbService _mongoDbService;

    public BacktestService(ILogger<BacktestService> logger, HistoricalDataFetcher historicalFetcher, ChartinkScraper chartinkScraper, MongoDbService mongoDbService)
    {
        _logger = logger;
        _historicalFetcher = historicalFetcher;
        _chartinkScraper = chartinkScraper;
        _mongoDbService = mongoDbService;
    }

    public async Task<BacktestResult> BacktestStrategyAsync(string symbol, DateTime startDate, DateTime endDate, bool isSwing)
    {
        try
        {
            var result = new BacktestResult
            {
                Symbol = symbol,
                Trades = new List<BacktestTrade>(),
                TotalProfit = 0,
                WinRate = 0
            };
            var currentDate = startDate.Date;
            decimal capital = 100000; // Starting capital
            var openPositions = new List<BacktestTrade>();
            var dailyBoughtStocks = new HashSet<string>(); // Tracks unique stocks bought per day

            while (currentDate <= endDate.Date)
            {
                if (!IsTradingDay(currentDate) || IsMonthEnd(currentDate))
                {
                    currentDate = currentDate.AddDays(1);
                    continue;
                }

                // Reset daily stock count at the start of each trading day
                var istTimeZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
                var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, istTimeZone);
                if (currentDate.Date != now.Date)
                {
                    dailyBoughtStocks.Clear();
                }

                // Simulate Chartink scanner for the day
                var scannerStocks = await _chartinkScraper.GetStocksAsync();
                if (!scannerStocks.Any(s => s.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)))
                {
                    currentDate = currentDate.AddDays(1);
                    continue;
                }

                // Buy logic
                if (openPositions.Count < 5 && dailyBoughtStocks.Count < 5)
                {
                    bool isUptrend = await _historicalFetcher.IsPriceAboveEmaAsync(symbol, 50, currentDate);
                    //bool isBreakout = await _historicalFetcher.IsBreakoutAsync(symbol, currentDate);
                    bool isRsiValid = await _historicalFetcher.GetRsiAsync(symbol, currentDate) > 50;
                    bool isVolumeValid = await _historicalFetcher.IsVolumeAboveSmaAsync(symbol, 20, currentDate);

                    // Check if stock was sold at profit within 20 trading days
                    bool wasSoldRecently = await _mongoDbService.WasStockSoldAtProfitRecentlyAsync(symbol);

                    if (isUptrend && isRsiValid && isVolumeValid && !wasSoldRecently && capital > 20000)
                    {
                        var quote = await _historicalFetcher.GetQuoteAsync(symbol, currentDate);
                        decimal? atr = await _historicalFetcher.GetAtrAsync(symbol, 14, isSwing ? "1d" : "5m", currentDate);
                        decimal riskPerShare = atr.HasValue ? 2 * atr.Value : quote.LastPrice * 0.025m;
                        decimal riskPerTrade = capital * 0.01m;
                        int quantity = (int)(riskPerTrade / riskPerShare);

                        if (quantity > 0)
                        {
                            var trade = new BacktestTrade
                            {
                                BuyDate = currentDate.Add(new TimeSpan(15, 25, 0)), // Simulate 3:25 PM IST
                                BuyPrice = quote.LastPrice,
                                Quantity = quantity,
                                ExpiryDate = isSwing ? AddTradingDays(currentDate, 10) : currentDate.Date.Add(new TimeSpan(14, 30, 0))
                            };
                            openPositions.Add(trade);
                            result.Trades.Add(trade);
                            dailyBoughtStocks.Add(symbol);
                            capital -= quantity * quote.LastPrice; // Deduct investment
                            _logger.LogInformation("Backtest: Bought {Symbol} on {Date} at ₹{Price:F2}, Qty: {Quantity}",
                                symbol, trade.BuyDate, trade.BuyPrice, trade.Quantity);
                        }
                    }
                }

                // Sell logic
                foreach (var trade in openPositions.ToList())
                {
                    var quote = await _historicalFetcher.GetQuoteAsync(symbol, currentDate);
                    decimal profitPercent = (quote.LastPrice - trade.BuyPrice) / trade.BuyPrice * 100;
                    bool sell = false;
                    string sellReason = "";
                    decimal? atr = await _historicalFetcher.GetAtrAsync(symbol, 14, isSwing ? "1d" : "5m", currentDate);
                    decimal stopLossPrice = trade.BuyPrice - (atr.HasValue ? 2 * atr.Value : trade.BuyPrice * 0.025m);
                    decimal trailingStopLoss = profitPercent > 5 ? trade.BuyPrice + (trade.BuyPrice * 0.02m) : stopLossPrice;

                    bool isEma5Confirmed = quote.Open < await _historicalFetcher.GetEmaAsync(symbol, 5, isSwing ? "1d" : "5m", currentDate) &&
                                           quote.Close < await _historicalFetcher.GetEmaAsync(symbol, 5, isSwing ? "1d" : "5m", currentDate) &&
                                           quote.Close < quote.Open;
                    bool isEma9Confirmed = quote.Open < await _historicalFetcher.GetEmaAsync(symbol, 9, isSwing ? "1d" : "5m", currentDate) &&
                                           quote.Close < await _historicalFetcher.GetEmaAsync(symbol, 9, isSwing ? "1d" : "5m", currentDate) &&
                                           quote.Close < quote.Open;
                    bool isVolumeDrop = await _historicalFetcher.IsVolumeBelowSmaAsync(symbol, 20, currentDate) &&
                                        quote.LastPrice < trade.BuyPrice &&
                                        currentDate.Date > trade.BuyDate.Date;

                    if (scannerStocks.Any(s => s.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)))
                    {
                        trade.ExpiryDate = AddTradingDays(trade.ExpiryDate, 1);
                        _logger.LogInformation("Backtest: Extended expiry for {Symbol} by 1 trading day (in Chartink scanner).", symbol);
                        continue;
                    }

                    if (isSwing)
                    {
                        if (currentDate.Date == trade.BuyDate.Date.AddDays(1) && profitPercent >= 2)
                        {
                            sell = true;
                            sellReason = ">2% profit since yesterday";
                        }
                        else if (trade.ExpiryDate <= currentDate)
                        {
                            sell = true;
                            sellReason = "Position expired";
                        }
                        else if (profitPercent >= 10)
                        {
                            sell = true;
                            sellReason = ">10% profit";
                        }
                        else if (quote.LastPrice < await _historicalFetcher.GetEmaAsync(symbol, 21, "1d", currentDate))
                        {
                            sell = true;
                            sellReason = "Price below EMA21";
                        }
                        else if (quote.LastPrice <= trailingStopLoss && isEma5Confirmed)
                        {
                            sell = true;
                            sellReason = profitPercent > 5 ? "Trailing stop-loss (entry + 2%)" : "Stop-loss (2x ATR or 2.5%)";
                        }
                        else if (isVolumeDrop)
                        {
                            sell = true;
                            sellReason = "Volume drop below 20-day SMA and price below entry";
                        }
                        else if (isEma9Confirmed)
                        {
                            sell = true;
                            sellReason = "Close below EMA9";
                        }
                    }
                    else
                    {
                        if (profitPercent >= 1)
                        {
                            sell = true;
                            sellReason = ">1% profit";
                        }
                        else if (quote.LastPrice <= trailingStopLoss)
                        {
                            sell = true;
                            sellReason = profitPercent > 5 ? "Trailing stop-loss (entry + 2%)" : "<0.5% stop-loss";
                        }
                        else if (quote.Close < await _historicalFetcher.GetEmaAsync(symbol, 7, "5m", currentDate))
                        {
                            sell = true;
                            sellReason = "Close below EMA7 (5m)";
                        }
                    }

                    if (sell)
                    {
                        trade.SellDate = currentDate;
                        trade.SellPrice = quote.LastPrice;
                        trade.ProfitLoss = (quote.LastPrice - trade.BuyPrice) * trade.Quantity;
                        trade.ProfitLossPct = profitPercent;
                        trade.SellReason = sellReason;
                        openPositions.Remove(trade);
                        capital += trade.Quantity * quote.LastPrice; // Add sale proceeds
                        _logger.LogInformation("Backtest: Sold {Symbol} on {Date} at ₹{Price:F2}, Profit/Loss: ₹{ProfitLoss:F2}, Reason: {Reason}",
                            symbol, trade.SellDate, trade.SellPrice, trade.ProfitLoss, trade.SellReason);
                    }
                }

                currentDate = currentDate.AddDays(1);
            }

            result.TotalProfit = result.Trades.Sum(t => t.ProfitLoss ?? 0);
            result.WinRate = result.Trades.Count > 0 ? result.Trades.Count(t => t.ProfitLoss > 0) / (double)result.Trades.Count * 100 : 0;
            _logger.LogInformation("Backtest completed for {Symbol}: Total Profit ₹{TotalProfit:F2}, Win Rate {WinRate:F2}%",
                symbol, result.TotalProfit, result.WinRate);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError("Error in backtest for {Symbol}: {Message}", symbol, ex.Message);
            return new BacktestResult { Symbol = symbol, Trades = new List<BacktestTrade>() };
        }
    }



    private bool IsTradingDay(DateTime date)
    {
        return date.DayOfWeek != DayOfWeek.Saturday && date.DayOfWeek != DayOfWeek.Sunday;
    }

    private bool IsMonthEnd(DateTime date)
    {
        return date.Day >= 28 && date.Day <= 31;
    }

    private DateTime AddTradingDays(DateTime date, int tradingDays)
    {
        var currentDate = date.Date;
        int daysAdded = 0;
        while (daysAdded < tradingDays)
        {
            currentDate = currentDate.AddDays(1);
            if (currentDate.DayOfWeek != DayOfWeek.Saturday && currentDate.DayOfWeek != DayOfWeek.Sunday)
                daysAdded++;
        }
        return currentDate.Add(new TimeSpan(15, 30, 0));
    }
}

public class BacktestResult
{
    public string? Symbol { get; set; }
    public List<BacktestTrade>? Trades { get; set; }
    public decimal TotalProfit { get; set; }
    public double WinRate { get; set; }
}

public class BacktestTrade
{
    public DateTime BuyDate { get; set; }
    public decimal BuyPrice { get; set; }
    public int Quantity { get; set; }
    public DateTime ExpiryDate { get; set; }
    public DateTime? SellDate { get; set; }
    public decimal? SellPrice { get; set; }
    public decimal? ProfitLoss { get; set; }
    public decimal? ProfitLossPct { get; set; }
    public string? SellReason { get; set; }
}