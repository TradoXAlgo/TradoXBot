using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Quartz;
using System;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Bot;
using TradoXBot.Models;
using TradoXBot.Services;

namespace TradoXBot.Jobs;

public class ScalpingMonitorJob : IJob
{
    private readonly ILogger<ScalpingMonitorJob> _logger;
    private readonly StoxKartClient _stoxKartClient;
    private readonly HistoricalDataFetcher _historicalFetcher;
    private readonly MongoDbService _mongoDbService;
    private readonly ChartinkScraper _chartinkScraper;
    private readonly TelegramBotClient _telegramBot;
    private readonly string? _chatId;

    public ScalpingMonitorJob(IConfiguration configuration, ILogger<ScalpingMonitorJob> logger, StoxKartClient stoxKartClient, ChartinkScraper chartinkScraper, HistoricalDataFetcher historicalFetcher, MongoDbService mongoDbService)
    {
        _logger = logger;
        _stoxKartClient = stoxKartClient;
        _chartinkScraper = chartinkScraper;
        _historicalFetcher = historicalFetcher;
        _mongoDbService = mongoDbService;
        _telegramBot = new TelegramBotClient(configuration["Telegram:ApiKey"]);
        _chatId = configuration["Telegram:ChatId"];
    }

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            // Check market hours (9:15 AM - 2:30 PM IST)
            var now = DateTime.Now;
            var marketOpen = new TimeSpan(9, 15, 0);
            var marketClose = new TimeSpan(14, 30, 0);
            if (now.TimeOfDay < marketOpen || now.TimeOfDay > marketClose || !IsTradingDay(now))
            {
                _logger.LogInformation("Scalping Monitor skipped: Outside market hours (9:15 AM - 2:30 PM IST).");
                return;
            }

            _logger.LogInformation("Executing Scalping Monitor Job at {Time}", now);
            await _stoxKartClient.AuthenticateAsync();

            // Check portfolio limit
            var openCount = await _mongoDbService.GetOpenPositionCountAsync();
            if (openCount >= 5)
            {
                _logger.LogWarning("Portfolio limit reached (5 positions).");
                await _telegramBot.SendMessage(_chatId, "Scalping Monitor: Portfolio limit reached (5 positions).");
                return;
            }

            // Monitor existing scalping positions
            var openTransactions = await _mongoDbService.GetOpenScalpingTransactionsAsync();
            var tokens = await _stoxKartClient.GetInstrumentTokensAsync("NSE");
            var quoteRequests = openTransactions
                .Select(t => tokens.GetValueOrDefault(t.Symbol))
                .Where(t => t != null)
                .Distinct()
                .ToList();
            var quotes = await _stoxKartClient.GetQuotesAsync("NSE", quoteRequests);

            var symbolQuotes = new Dictionary<string, Quote>();
            foreach (var kv in quotes)
            {
                var symbol = openTransactions.FirstOrDefault(t => tokens.GetValueOrDefault(t.Symbol) == kv.Key)?.Symbol;
                if (symbol != null)
                    symbolQuotes[symbol] = kv.Value;
            }

            foreach (var transaction in openTransactions)
            {
                if (!symbolQuotes.TryGetValue(transaction.Symbol, out var quote))
                {
                    _logger.LogWarning($"No quote data for {transaction.Symbol}. Skipping sell check.");
                    continue;
                }

                var ohlc = await _historicalFetcher.GetTodaysOhlcAsync(transaction.Symbol, "5m");
                var ema7 = await _historicalFetcher.GetEma7Async(transaction.Symbol);
                decimal profitPercent = (quote.LastPrice - transaction.BuyPrice) / transaction.BuyPrice * 100;
                bool sell = false;
                string sellReason = "";

                // Scalping sell conditions: 1% profit, 0.5% stop-loss, or close < EMA7
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
                else if (ema7.HasValue && ohlc != null && ohlc.Close < ema7.Value)
                {
                    sell = true;
                    sellReason = "close below EMA7";
                }

                if (sell)
                {
                    var token = tokens.GetValueOrDefault(transaction.Symbol);
                    if (token == null)
                    {
                        _logger.LogWarning($"No token found for {transaction.Symbol}. Skipping sell.");
                        continue;
                    }

                    var orderId = await _stoxKartClient.PlaceOrderAsync("SELL", "NSE", token, "MARKET", "INTRADAY", transaction.Quantity, 0);
                    _logger.LogInformation($"Scalping sold {transaction.Symbol}. Order ID: {orderId}");

                    decimal profitLossAmount = (quote.LastPrice - transaction.BuyPrice) * transaction.Quantity;
                    decimal profitLossPct = profitPercent;
                    await _mongoDbService.UpdateTransactionOnSellAsync("ScalpingTransactions", transaction.Symbol, DateTime.Now, quote.LastPrice, profitLossAmount, profitLossPct);

                    string sellMessage = $"Scalping Stock Sold:\n" +
                                         $"Name: {transaction.StockName}\n" +
                                         $"Symbol: {transaction.Symbol}\n" +
                                         $"Buy Date: {transaction.BuyDate:yyyy-MM-dd HH:mm}\n" +
                                         $"Buy Price: ₹{transaction.BuyPrice:F2}\n" +
                                         $"Sell Price: ₹{quote.LastPrice:F2}\n" +
                                         $"Profit/Loss: ₹{profitLossAmount:F2} ({profitLossPct:F2}%)\n" +
                                         $"Reason: {sellReason}";
                    await _telegramBot.SendMessage(_chatId, sellMessage);
                }
            }

            // Check for new scalping buys
            var availableFunds = await _stoxKartClient.GetFundsAsync();
            var maxOrders = Math.Min((int)(availableFunds / 20000), 5 - openCount);
            if (maxOrders <= 0)
            {
                _logger.LogWarning("Insufficient funds for scalping buy.");
                await _telegramBot.SendMessage(_chatId, "Scalping Buy: Insufficient funds.");
                return;
            }

            var scannerStocks = await _chartinkScraper.GetStocksAsync();
            if (scannerStocks.Count == 0)
            {
                _logger.LogWarning("No stocks found in Chartink scanner for scalping.");
                return;
            }

            foreach (var stock in scannerStocks.Take(maxOrders))
            {
                if (await _mongoDbService.ShouldSkipStockForScalpingAsync(stock.Symbol))
                {
                    _logger.LogInformation($"Skipping {stock.Symbol} for scalping due to recent profitable trade today.");
                    continue;
                }

                bool isScalpingBreakout = await _historicalFetcher.IsScalpingBreakoutAsync(stock.Symbol);
                if (!isScalpingBreakout)
                {
                    _logger.LogInformation($"Skipping {stock.Symbol} for scalping: No breakout.");
                    continue;
                }

                var token = tokens.GetValueOrDefault(stock.Symbol);
                if (token == null)
                {
                    _logger.LogWarning($"No token found for {stock.Symbol}. Skipping scalping buy.");
                    continue;
                }

                var quoteList = new Dictionary<string, Quote>();
                quoteList = await _stoxKartClient.GetQuotesAsync("NSE", new[] { token }.ToList());
                if (!quoteList.TryGetValue(token, out var quote))
                {
                    _logger.LogWarning($"No quote data for {stock.Symbol}. Skipping scalping buy.");
                    continue;
                }

                decimal? atr = await _historicalFetcher.GetAtrAsync(stock.Symbol, 14, "5m");
                decimal riskPerShare = atr.HasValue ? atr.Value : quote.LastPrice * 0.005m; // 0.5% if ATR unavailable
                decimal riskPerTrade = availableFunds * 0.01m;
                int quantity = (int)(riskPerTrade / riskPerShare);
                if (quantity <= 0)
                {
                    _logger.LogWarning($"Calculated quantity for {stock.Symbol} is {quantity}. Skipping scalping buy.");
                    continue;
                }

                var orderId = await _stoxKartClient.PlaceOrderAsync("BUY", "NSE", token, "MARKET", "INTRADAY", quantity, 0);
                _logger.LogInformation($"Scalping bought {stock.Symbol} (Qty: {quantity}, Price: {quote.LastPrice:F2}). Order ID: {orderId}");

                var ohlc = await _historicalFetcher.GetTodaysOhlcAsync(stock.Symbol, "5m");
                var transaction = new Transaction
                {
                    StockName = stock.Name,
                    Symbol = stock.Symbol,
                    BuyDate = DateTime.Now,
                    BuyPrice = quote.LastPrice,
                    Quantity = quantity,
                    ExpiryDate = DateTime.Now.Date.AddDays(1),
                    OpenPrice = ohlc?.Open ?? 0,
                    HighPrice = ohlc?.High ?? 0,
                    LowPrice = ohlc?.Low ?? 0,
                    ClosePrice = ohlc?.Close ?? quote.LastPrice
                };
                await _mongoDbService.InsertScalpingTransactionAsync(transaction);
                await _mongoDbService.InsertScannerStockAsync(stock);

                decimal stopLoss = quote.LastPrice - riskPerShare;
                decimal targetPrice = quote.LastPrice + (2 * riskPerShare); // 2:1 R:R

                string message = $"Scalping Trade Alert:\n" +
                                $"Name: {stock.Name}\n" +
                                $"Symbol: {stock.Symbol}\n" +
                                $"Buy Date: {transaction.BuyDate:yyyy-MM-dd HH:mm}\n" +
                                $"Buy Price: ₹{quote.LastPrice:F2}\n" +
                                $"Target: ₹{targetPrice:F2}\n" +
                                $"Stop Loss: ₹{stopLoss:F2}\n" +
                                $"Quantity: {quantity}\n" +
                                $"Risk-Reward Ratio: 2:1";
                await _telegramBot.SendMessage(_chatId, message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error in Scalping Monitor Job: {ex.Message}");
            await _telegramBot.SendMessage(_chatId, $"Scalping Monitor Error: {ex.Message}");
        }
    }

    private bool IsTradingDay(DateTime date)
    {
        return date.DayOfWeek != DayOfWeek.Saturday && date.DayOfWeek != DayOfWeek.Sunday;
    }
}
