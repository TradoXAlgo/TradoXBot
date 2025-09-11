using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Bot;
using TradoXBot.Jobs;
using TradoXBot.Models;
using Microsoft.Extensions.DependencyInjection;
namespace TradoXBot.Services;

public class TradingOperations
{
    private readonly ILogger<TradingOperations> _logger;
    private readonly StoxKartClient _stoxKartClient;
    private readonly ChartinkScraper _chartinkScraper;
    private readonly HistoricalDataFetcher _historicalFetcher;
    private readonly MongoDbService _mongoDbService;
    private readonly BacktestService _backtestService;
    private readonly TelegramBotClient _telegramBot;
    private readonly IServiceProvider _serviceProvider;
    private readonly string? _chatId;

    public TradingOperations(IServiceProvider serviceProvider, IConfiguration configuration, ILogger<TradingOperations> logger,
        StoxKartClient stoxKartClient, ChartinkScraper chartinkScraper,
        HistoricalDataFetcher historicalFetcher, MongoDbService mongoDbService,
        BacktestService backtestService)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _stoxKartClient = stoxKartClient;
        _chartinkScraper = chartinkScraper;
        _historicalFetcher = historicalFetcher;
        _mongoDbService = mongoDbService;
        _backtestService = backtestService;
        _telegramBot = new TelegramBotClient(configuration["Telegram:ApiKey"]);
        _chatId = configuration["Telegram:ChatId"];
    }

    public async Task RunMethodAtIntervalAsync(Func<Task> method, TimeSpan interval, string methodName, CancellationToken token, TimeZoneInfo timeZone)
    {
        while (!token.IsCancellationRequested)
        {
            // Check if current time is between 9:15 AM and 10:15 AM
            TimeSpan startTime = new(9, 10, 0); // 9:15 AM
            TimeSpan endTime = new(15, 30, 0); // 15:30 pm 
            var istTimeZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, istTimeZone);
            TimeSpan currentTime = now.TimeOfDay;
            bool isWithinTime = currentTime >= startTime && currentTime <= endTime && IsTradingDay(now);
            if (!isWithinTime)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {methodName} skipped (outside 9:15 AM - 3:30 PM window).");
                return;
            }
            else
            {
                Console.WriteLine($"{methodName} executed at {currentTime} IST");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Running {methodName}...");
                await method();
                // Execute the method
            }
            // Wait for the next interval
            await Task.Delay(interval, token);
        }
    }

    public async Task ExecuteTokenAuthAsync()
    {
        try
        {
            _logger.LogInformation("Executing Auth operation");
            var job = ActivatorUtilities.CreateInstance<AuthJob>(_serviceProvider);
            await job.Execute(null);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error executing Swing Buy: {ex.Message}");
            throw;
        }
    }

    public async Task ExecuteSwingBuyAsync()
    {
        try
        {
            var istZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            TimeSpan currentTime = TimeZoneInfo.ConvertTime(DateTime.Now, istZone).TimeOfDay;
            TimeSpan startTime = new(15, 23, 0); // 15:30 pm
            TimeSpan endTime = new(15, 30, 0); // 15:30 pm

            if (currentTime <= startTime && currentTime >= endTime) return;

            _logger.LogInformation("Executing Buy Job at {Time} IST", currentTime);
            var job = ActivatorUtilities.CreateInstance<BuyJob>(_serviceProvider);
            await job.Execute(null);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error executing Swing Buy: {ex.Message}");
            throw;
        }
    }

    public async Task ExecuteScalpingBuyAsync()
    {
        try
        {
            var istZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            TimeSpan currentTime = TimeZoneInfo.ConvertTime(DateTime.Now, istZone).TimeOfDay;
            TimeSpan startTime = new(15, 23, 0); // 15:30 pm
            TimeSpan endTime = new(15, 30, 0); // 15:30 pm

            if (currentTime <= startTime && currentTime >= endTime) return;

            _logger.LogInformation("Executing Scalping Buy operation");
            var job = ActivatorUtilities.CreateInstance<ScalpingBuyJob>(_serviceProvider);
            await job.Execute(null);

        }
        catch (Exception ex)
        {
            _logger.LogError($"Error executing Swing Buy: {ex.Message}");
            throw;
        }
    }

    public async Task ExecuteSwingSellAsync()
    {
        try
        {
            var istZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            TimeSpan currentTime = TimeZoneInfo.ConvertTime(DateTime.Now, istZone).TimeOfDay;
            TimeSpan startTime = new(9, 15, 0); // 15:30 pm
            TimeSpan endTime = new(15, 30, 0); // 15:30 pm

            if (currentTime <= startTime && currentTime >= endTime) return;
            _logger.LogInformation("Executing Swing Buy operation");
            var job = ActivatorUtilities.CreateInstance<SellJob>(_serviceProvider);
            await job.Execute(null);

        }
        catch (Exception ex)
        {
            _logger.LogError($"Error executing Swing Buy: {ex.Message}");
            throw;
        }
    }

    public async Task ExecuteScalpingSellAsync()
    {

        try
        {
            var istZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            TimeSpan currentTime = TimeZoneInfo.ConvertTime(DateTime.Now, istZone).TimeOfDay;
            TimeSpan startTime = new(9, 15, 0); // 15:30 pm
            TimeSpan endTime = new(15, 30, 0); // 15:30 pm

            if (currentTime <= startTime && currentTime >= endTime) return;
            _logger.LogInformation("Executing Swing Buy operation");
            var job = ActivatorUtilities.CreateInstance<ScalpingSelJob>(_serviceProvider);
            await job.Execute(null);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error executing Swing Buy: {ex.Message}");
            throw;
        }
    }

    public async Task ExecuteHouralyStatusAsync()
    {

        try
        {
            var istZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            TimeSpan currentTime = TimeZoneInfo.ConvertTime(DateTime.Now, istZone).TimeOfDay;
            TimeSpan startTime = new(9, 15, 0); // 15:30 pm
            TimeSpan endTime = new(15, 30, 0); // 15:30 pm

            if (currentTime <= startTime && currentTime >= endTime) return;
            _logger.LogInformation("Executing Swing Buy operation");
            var job = ActivatorUtilities.CreateInstance<HourlyStatusJob>(_serviceProvider);
            await job.Execute(null);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error executing Swing Buy: {ex.Message}");
            throw;
        }
    }

    private async Task ViewOpenPositionsAsync()
    {
        try
        {
            var openSwingTransactions = await _mongoDbService.GetOpenSwingTransactionsAsync();
            var openScalpingTransactions = await _mongoDbService.GetOpenScalpingTransactionsAsync();
            var openTransactions = openSwingTransactions.Concat(openScalpingTransactions).ToList();
            var tokens = await _stoxKartClient.GetInstrumentTokensAsync("NSE");

            var quoteRequests = openTransactions
                .Select(t => tokens.GetValueOrDefault(t.Symbol.ToUpper()))
                .Where(t => t != null)
                .Distinct()
                .ToList();
            var quotes = await _stoxKartClient.GetQuotesAsync("NSE", quoteRequests);

            var symbolQuotes = new Dictionary<string, Quote>();
            foreach (var kv in quotes)
            {
                var symbol = openTransactions.FirstOrDefault(t => tokens.GetValueOrDefault(t.Symbol.ToUpper()) == kv.Key)?.Symbol;
                if (symbol != null)
                    symbolQuotes[symbol] = kv.Value;
            }

            Console.WriteLine("\n=== Open Positions ===");
            if (openTransactions.Count == 0)
            {
                Console.WriteLine("No open positions.");
                return;
            }

            foreach (var transaction in openTransactions)
            {
                var quote = symbolQuotes.GetValueOrDefault(transaction.Symbol);
                decimal currentPrice = quote?.LastPrice ?? transaction.ClosePrice;
                decimal profitPercent = (currentPrice - transaction.BuyPrice) / transaction.BuyPrice * 100;
                decimal? atr = await _historicalFetcher.GetAtrAsync(transaction.Symbol, 14,
                    transaction.ExpiryDate.TimeOfDay == new TimeSpan(14, 30, 0) ? "5m" : "1d");
                decimal riskPerShare = atr.HasValue ? 2 * atr.Value : transaction.BuyPrice * 0.025m;
                decimal stopLoss = transaction.BuyPrice - riskPerShare;
                decimal targetPrice = transaction.BuyPrice + (3 * riskPerShare);

                Console.WriteLine($"Name: {transaction.StockName}");
                Console.WriteLine($"Symbol: {transaction.Symbol}");
                Console.WriteLine($"Type: {(transaction.ExpiryDate.TimeOfDay == new TimeSpan(14, 30, 0) ? "Scalping" : "Swing")}");
                Console.WriteLine($"Buy Time: {transaction.BuyDate:yyyy-MM-dd HH:mm} IST");
                Console.WriteLine($"Buy Price: ₹{transaction.BuyPrice:F2}");
                Console.WriteLine($"Current Price: ₹{currentPrice:F2}");
                Console.WriteLine($"Target: ₹{targetPrice:F2}");
                Console.WriteLine($"Stop Loss: ₹{stopLoss:F2}");
                Console.WriteLine($"Profit/Loss: {profitPercent:F2}%");
                Console.WriteLine($"R:R: 3:1");
                Console.WriteLine("-------------------");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("View Open Positions Error: {Message}", ex.Message);
            Console.WriteLine($"View Open Positions Error: {ex.Message}");
            await _telegramBot.SendMessage(_chatId, $"View Open Positions Error: {ex.Message}");
        }
    }

    private async Task RunBacktestAsync()
    {
        try
        {
            Console.Write("Enter stock symbol: ");
            var symbol = Console.ReadLine()?.ToUpper();
            if (string.IsNullOrEmpty(symbol))
            {
                Console.WriteLine("Invalid symbol.");
                return;
            }

            Console.Write("Enter start date (yyyy-MM-dd): ");
            if (!DateTime.TryParse(Console.ReadLine(), out var startDate))
            {
                Console.WriteLine("Invalid start date.");
                return;
            }

            Console.Write("Enter end date (yyyy-MM-dd): ");
            if (!DateTime.TryParse(Console.ReadLine(), out var endDate))
            {
                Console.WriteLine("Invalid end date.");
                return;
            }

            Console.Write("Enter strategy (1 for Swing, 2 for Scalping): ");
            var strategyInput = Console.ReadLine();
            bool isSwing = strategyInput == "1";

            var result = await _backtestService.BacktestStrategyAsync(symbol, startDate, endDate, isSwing);
            Console.WriteLine($"\n=== Backtest Results for {symbol} ({(isSwing ? "Swing" : "Scalping")}) ===");
            Console.WriteLine($"Total Profit: ₹{result.TotalProfit:F2}");
            Console.WriteLine($"Win Rate: {result.WinRate:F2}%");
            Console.WriteLine($"Number of Trades: {result.Trades.Count}");
            Console.WriteLine("\nTrades:");
            foreach (var trade in result.Trades)
            {
                Console.WriteLine($"Buy: {trade.BuyDate:yyyy-MM-dd} at ₹{trade.BuyPrice:F2}, Qty: {trade.Quantity}");
                if (trade.SellDate.HasValue)
                {
                    Console.WriteLine($"Sell: {trade.SellDate:yyyy-MM-dd} at ₹{trade.SellPrice:F2}, " +
                                      $"Profit/Loss: ₹{trade.ProfitLoss:F2} ({trade.ProfitLossPct:F2}%), Reason: {trade.SellReason}");
                }
                Console.WriteLine("-------------------");
            }
            _logger.LogInformation("Backtest completed for {Symbol}: Total Profit ₹{TotalProfit:F2}, Win Rate {WinRate:F2}%",
                symbol, result.TotalProfit, result.WinRate);
        }
        catch (Exception ex)
        {
            _logger.LogError("Backtest Error: {Message}", ex.Message);
            Console.WriteLine($"Backtest Error: {ex.Message}");
            await _telegramBot.SendMessage(_chatId, $"Backtest Error: {ex.Message}");
        }
    }

    public async Task ShowMenuAsync()
    {
        await ExecuteTokenAuthAsync();

        _logger.LogInformation("Trading Bot Console App Started (Test Mode)");
        while (true)
        {
            Console.WriteLine("\nTrading Bot Test Menu:");
            Console.WriteLine("1. Execute Buy Job");
            Console.WriteLine("2. Execute Sell Job");
            Console.WriteLine("3. Execute Scalping Buy Job");
            Console.WriteLine("4. Execute Scalping Sell Job");
            Console.WriteLine("5. Execute Hourly Status Job");
            Console.WriteLine("6. Exit");
            Console.Write("Select an option (1-6): ");

            var input = Console.ReadLine();
            try
            {
                switch (input)
                {
                    case "1":
                        var buyJob = ActivatorUtilities.CreateInstance<BuyJob>(_serviceProvider);
                        await buyJob.Execute(null);
                        break;
                    case "2":
                        var sellJob = ActivatorUtilities.CreateInstance<SellJob>(_serviceProvider);
                        await sellJob.Execute(null);
                        break;
                    case "3":
                        var scalpingJob = ActivatorUtilities.CreateInstance<ScalpingBuyJob>(_serviceProvider);
                        await scalpingJob.Execute(null);
                        break;
                    case "4":
                        var scalpingSellJob = ActivatorUtilities.CreateInstance<ScalpingSelJob>(_serviceProvider);
                        await scalpingSellJob.Execute(null);
                        break;
                    case "5":
                        var statusJob = ActivatorUtilities.CreateInstance<HourlyStatusJob>(_serviceProvider);
                        await statusJob.Execute(null);
                        break;
                    case "6":
                        _logger.LogInformation("Exiting Trading Bot Console App");
                        return;
                    default:
                        Console.WriteLine("Invalid option. Please select 1-6.");
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error executing option {input}: {ex.Message}");
                Console.WriteLine($"Error: {ex.Message}");
            }
        }
    }
    private bool IsTradingDay(DateTime date)
    {
        return date.DayOfWeek != DayOfWeek.Saturday && date.DayOfWeek != DayOfWeek.Sunday && !Holidays.Contains(date.Date);
    }


    private readonly List<DateTime> Holidays =
    [
        new DateTime(2025, 2, 26), // Mahashivratri
        new DateTime(2025, 3, 14), // Holi
        new DateTime(2025, 3, 31), // Eid-Ul-Fitr
        new DateTime(2025, 4, 10), // Shri Mahavir Jayanti
        new DateTime(2025, 4, 14), // Dr. Baba Saheb Ambedkar Jayanti
        new DateTime(2025, 4, 18), // Good Friday
        new DateTime(2025, 5, 1), // Maharashtra Day
        new DateTime(2025, 8, 15), // Independence Day
        new DateTime(2025, 8, 27), // Ganesh Chaturthi
        new DateTime(2025, 10, 2), // Mahatma Gandhi Jayanti/Dussehra
        new DateTime(2025, 10, 21), // Diwali Laxmi Pujan
        new DateTime(2025, 10, 22), // Diwali-Balipratipada
        new DateTime(2025, 11, 5), // Prakash Gurpurb Sri Guru Nanak Dev
        new DateTime(2025, 12, 25) // Christmas
    ];
}