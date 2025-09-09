using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TradoXBot.Models;

namespace TradoXBot.Services
{
    public class MongoDbService
    {
        private readonly IMongoCollection<Transaction> _swingTransactions;
        private readonly IMongoCollection<Transaction> _scalpingTransactions;
        private readonly IMongoCollection<ScannerStock> _scannerStocks;
        private readonly ILogger<MongoDbService> _logger;

        public MongoDbService(IConfiguration configuration, ILogger<MongoDbService> logger)
        {
            _logger = logger;
            var client = new MongoClient(configuration["MongoDb:ConnectionString"]);
            var database = client.GetDatabase(configuration["MongoDb:DatabaseName"]);
            _swingTransactions = database.GetCollection<Transaction>("SwingTransactions");
            _scalpingTransactions = database.GetCollection<Transaction>("ScalpingTransactions");
            _scannerStocks = database.GetCollection<ScannerStock>("ScannerStocks");

            // Create indexes for performance
            _swingTransactions.Indexes.CreateOne(new CreateIndexModel<Transaction>(
                Builders<Transaction>.IndexKeys.Ascending(t => t.Symbol).Ascending(t => t.IsOpen)));
            _scalpingTransactions.Indexes.CreateOne(new CreateIndexModel<Transaction>(
                Builders<Transaction>.IndexKeys.Ascending(t => t.Symbol).Ascending(t => t.IsOpen)));
            _swingTransactions.Indexes.CreateOne(new CreateIndexModel<Transaction>(
                Builders<Transaction>.IndexKeys.Ascending(t => t.BuyDate)));
            _scalpingTransactions.Indexes.CreateOne(new CreateIndexModel<Transaction>(
                Builders<Transaction>.IndexKeys.Ascending(t => t.BuyDate)));
        }
        public async Task<bool> HasOpenPositionAsync(string symbol)
        {
            try
            {
                var swingFilter = Builders<Transaction>.Filter.Eq(t => t.Symbol, symbol) &
                                 Builders<Transaction>.Filter.Eq(t => t.IsOpen, true);
                var scalpingFilter = Builders<Transaction>.Filter.Eq(t => t.Symbol, symbol) &
                                    Builders<Transaction>.Filter.Eq(t => t.IsOpen, true);

                var swingCount = await _swingTransactions.CountDocumentsAsync(swingFilter);
                var scalpingCount = await _scalpingTransactions.CountDocumentsAsync(scalpingFilter);

                bool hasOpenPosition = swingCount > 0 || scalpingCount > 0;
                _logger.LogInformation("Checked open position for {Symbol}: {HasOpenPosition}", symbol, hasOpenPosition);
                return hasOpenPosition;
            }
            catch (Exception ex)
            {
                _logger.LogError("Error checking open position for {Symbol}: {Message}", symbol, ex.Message);
                return false;
            }
        }
        public async Task InsertSwingTransactionAsync(Transaction transaction)
        {
            try
            {
                var istTimeZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
                transaction.BuyDate = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, istTimeZone);
                await _swingTransactions.InsertOneAsync(transaction);
                _logger.LogInformation("Inserted swing transaction for {Symbol} with BuyDate {BuyDate}",
                    transaction.Symbol, transaction.BuyDate);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error inserting swing transaction for {Symbol}: {Message}", transaction.Symbol, ex.Message);
            }
        }

        public async Task InsertScalpingTransactionAsync(Transaction transaction)
        {
            try
            {
                var istTimeZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
                transaction.BuyDate = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, istTimeZone);
                await _scalpingTransactions.InsertOneAsync(transaction);
                _logger.LogInformation("Inserted scalping transaction for {Symbol} with BuyDate {BuyDate}",
                    transaction.Symbol, transaction.BuyDate);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error inserting scalping transaction for {Symbol}: {Message}", transaction.Symbol, ex.Message);
            }
        }

        public async Task UpdateTransactionOnSellAsync(string collectionName, string symbol, DateTime sellDate, decimal? sellPrice, decimal? profitLoss, decimal? profitLossPercentage)
        {
            var collection = collectionName == "SwingTransactions" ? _swingTransactions : _scalpingTransactions;
            var filter = Builders<Transaction>.Filter.And(
                Builders<Transaction>.Filter.Eq(t => t.Symbol, symbol),
                Builders<Transaction>.Filter.Eq(t => t.IsOpen, true)
            );

            var update = Builders<Transaction>.Update
                .Set(t => t.SellDate, sellDate)
                .Set(t => t.SellPrice, sellPrice)
                .Set(t => t.ProfitLoss, profitLoss)
                .Set(t => t.ProfitLossPct, profitLossPercentage)
                .Set(t => t.IsOpen, false);

            await collection.UpdateOneAsync(filter, update);
        }

        public async Task InsertScannerStockAsync(ScannerStock scannerStock)
        {
            scannerStock.Id = Guid.NewGuid().ToString();
            await _scannerStocks.InsertOneAsync(scannerStock);
        }

        public async Task<List<Transaction>> GetOpenSwingTransactionsAsync()
        {
            try
            {
                return await _swingTransactions.Find(Builders<Transaction>.Filter.Eq(t => t.IsOpen, true))
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError("Error getting open swing transactions: {Message}", ex.Message);
                return new List<Transaction>();
            }

        }

        public async Task<List<Transaction>> GetOpenScalpingTransactionsAsync()
        {
            try
            {
                return await _scalpingTransactions.Find(Builders<Transaction>.Filter.Eq(t => t.IsOpen, true))
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError("Error getting open scalping transactions: {Message}", ex.Message);
                return new List<Transaction>();
            }
        }

        public async Task<List<Transaction>> GetClosedTransactionsAsync()
        {
            var filter = Builders<Transaction>.Filter.Ne(t => t.IsOpen, false);
            var swingClosed = await _swingTransactions.Find(filter).ToListAsync();
            var scalpingClosed = await _scalpingTransactions.Find(filter).ToListAsync();
            swingClosed.AddRange(scalpingClosed);
            return swingClosed;
        }

        public async Task UpdateTransactionExpiryAsync(string collectionName, string symbol, DateTime newExpiry)
        {
            var collection = collectionName == "SwingTransactions" ? _swingTransactions : _scalpingTransactions;
            var filter = Builders<Transaction>.Filter.And(
                Builders<Transaction>.Filter.Eq(t => t.Symbol, symbol),
                Builders<Transaction>.Filter.Eq(t => t.SellDate, null)
            );

            var update = Builders<Transaction>.Update
                .Set(t => t.ExpiryDate, newExpiry);

            await collection.UpdateOneAsync(filter, update);
        }

        public async Task<int> GetDailyUniqueStocksBoughtAsync()
        {
            try
            {
                var istTimeZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
                var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, istTimeZone);
                var today = now.Date;
                var tomorrow = today.AddDays(1);

                _logger.LogInformation("Checking unique stocks bought for date range: {Start} to {End} IST",
                    today, tomorrow);

                var swingFilter = Builders<Transaction>.Filter.Gte(t => t.BuyDate, today) &
                                 Builders<Transaction>.Filter.Lt(t => t.BuyDate, tomorrow);

                var swingSymbols = await _swingTransactions.Find(swingFilter)
                    .Project(t => t.Symbol)
                    .ToListAsync();

                var uniqueSymbols = swingSymbols.Count;
                _logger.LogInformation("Found {Count} unique stocks bought on {Date} (IST).", uniqueSymbols, today.ToString("yyyy-MM-dd"));
                return uniqueSymbols;
            }
            catch (Exception ex)
            {
                _logger.LogError("Error counting daily unique stocks: {Message}", ex.Message);
                return 0;
            }
        }

        public async Task<int> GetScalpingniqueStocksBoughtAsync()
        {
            try
            {
                var istTimeZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
                var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, istTimeZone);
                var today = now.Date;
                var tomorrow = today.AddDays(1);

                _logger.LogInformation("Checking unique stocks bought for date range: {Start} to {End} IST",
                    today, tomorrow);

                var scalpingFilter = Builders<Transaction>.Filter.Gte(t => t.BuyDate, today) &
                                    Builders<Transaction>.Filter.Lt(t => t.BuyDate, tomorrow);

                var scalpingSymbols = await _scalpingTransactions.Find(scalpingFilter)
                    .Project(t => t.Symbol)
                    .ToListAsync();

                var uniqueSymbols = scalpingSymbols.Count;
                _logger.LogInformation("Found {Count} unique stocks bought today: {Symbols}",
                    uniqueSymbols, string.Join(", ", uniqueSymbols));
                return uniqueSymbols;
            }
            catch (Exception ex)
            {
                _logger.LogError("Error counting daily unique stocks: {Message}", ex.Message);
                return 0;
            }
        }

        public async Task<List<Transaction>> GetSwingTransactionsBoughtTodayAsync()
        {
            try
            {
                var istTimeZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
                var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, istTimeZone);
                var today = now.Date;
                var tomorrow = today.AddDays(1);

                var filter = Builders<Transaction>.Filter.Eq(t => t.IsOpen, true) &
                             Builders<Transaction>.Filter.Gte(t => t.BuyDate, today) &
                             Builders<Transaction>.Filter.Lt(t => t.BuyDate, tomorrow);

                var transactions = await _swingTransactions.Find(filter).ToListAsync();
                _logger.LogInformation("Found {Count} swing transactions bought today: {Symbols}",
                    transactions.Count, string.Join(", ", transactions.Select(t => t.Symbol)));
                return transactions;
            }
            catch (Exception ex)
            {
                _logger.LogError("Error getting swing transactions bought today: {Message}", ex.Message);
                return new List<Transaction>();
            }
        }

        public async Task<bool> WasStockSoldAtProfitRecentlyAsync(string symbol)
        {
            try
            {
                var istTimeZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
                var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, istTimeZone);
                var today = now.Date;
                var twentyTradingDaysAgo = SubtractTradingDays(now, 20);

                var filter = Builders<Transaction>.Filter.Eq(t => t.Symbol, symbol) &
                             Builders<Transaction>.Filter.Gte(t => t.SellDate, twentyTradingDaysAgo) &
                             Builders<Transaction>.Filter.Lte(t => t.SellDate, today.AddDays(1)) &
                             Builders<Transaction>.Filter.Gt(t => t.ProfitLoss, 0);

                var swingCount = await _swingTransactions.CountDocumentsAsync(filter);
                var scalpingCount = await _scalpingTransactions.CountDocumentsAsync(filter);
                return swingCount > 0 || scalpingCount > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError("Error checking recent profitable sale for {Symbol}: {Message}", symbol, ex.Message);
                return false;
            }
        }

        public async Task<bool> WasStockBoughtAndSoldSameDayAsync(string symbol)
        {
            try
            {
                var istTimeZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
                var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, istTimeZone).Date;
                var tomorrow = today.AddDays(1);
                var filter = Builders<Transaction>.Filter.Eq(t => t.Symbol, symbol);
                Builders<Transaction>.Filter.Gte(t => t.BuyDate, today);
                Builders<Transaction>.Filter.Lt(t => t.BuyDate, tomorrow);
                Builders<Transaction>.Filter.Exists(t => t.SellDate);
                Builders<Transaction>.Filter.Gte(t => t.SellDate, today);
                Builders<Transaction>.Filter.Lt(t => t.SellDate, tomorrow);
                var count = await _scalpingTransactions.CountDocumentsAsync(filter);
                bool wasBoughtAndSold = count > 0;
                _logger.LogInformation("Checked if {Symbol} was bought and sold today: {Result}", symbol, wasBoughtAndSold);
                return wasBoughtAndSold;
            }
            catch (Exception ex)
            {
                _logger.LogError("Error checking if {Symbol} was bought and sold today: {Message}", symbol, ex.Message);
                return false;
            }
        }
        public async Task<int> GetOpenPositionCountAsync()
        {
            var swingCount = await _swingTransactions.CountDocumentsAsync(Builders<Transaction>.Filter.Eq(t => t.IsOpen, true));
            return (int)swingCount;
        }
        public async Task<int> GetScalpingpenPositionCountAsync()
        {
            try
            {
                var scalpingCount = await _scalpingTransactions.CountDocumentsAsync(Builders<Transaction>.Filter.Eq(t => t.IsOpen, true));
                return (int)scalpingCount;
            }
            catch (Exception ex)
            {
                _logger.LogError("Error counting open scalping positions: {Message}", ex.Message);
                return 0;
            }
        }
        public async Task<bool> ShouldSkipStockAsync(string symbol)
        {
            var twentyTradingDaysAgo = AddTradingDays(DateTime.Now, -20);
            var filter = Builders<Transaction>.Filter.And(
                Builders<Transaction>.Filter.Eq(t => t.Symbol, symbol),
                Builders<Transaction>.Filter.Ne(t => t.SellDate, null),
                Builders<Transaction>.Filter.Gte(t => t.SellDate, twentyTradingDaysAgo),
                Builders<Transaction>.Filter.Gt(t => t.ProfitLossPct, 0)
            );

            var swingCount = await _swingTransactions.CountDocumentsAsync(filter);
            var scalpingCount = await _scalpingTransactions.CountDocumentsAsync(filter);
            return swingCount > 0 || scalpingCount > 0;
        }

        public async Task<bool> ShouldSkipStockForScalpingAsync(string symbol)
        {
            var today = DateTime.Now.Date;
            var filter = Builders<Transaction>.Filter.And(
                Builders<Transaction>.Filter.Eq(t => t.Symbol, symbol),
                Builders<Transaction>.Filter.Ne(t => t.SellDate, null),
                Builders<Transaction>.Filter.Eq(t => t.SellDate, today),
                Builders<Transaction>.Filter.Gt(t => t.ProfitLossPct, 0)
            );

            var scalpingCount = await _scalpingTransactions.CountDocumentsAsync(filter);
            return scalpingCount > 0;
        }
        private DateTime SubtractTradingDays(DateTime date, int tradingDays)
        {
            var currentDate = date.Date;
            int daysSubtracted = 0;
            while (daysSubtracted < tradingDays)
            {
                currentDate = currentDate.AddDays(-1);
                if (currentDate.DayOfWeek != DayOfWeek.Saturday && currentDate.DayOfWeek != DayOfWeek.Sunday && !Holidays.Contains(date.Date))
                    daysSubtracted++;
            }
            return currentDate;
        }
        private DateTime AddTradingDays(DateTime startDate, int tradingDays)
        {
            int daysToAdd = Math.Abs(tradingDays);
            DateTime resultDate = startDate;
            int direction = tradingDays >= 0 ? 1 : -1;

            while (daysToAdd > 0)
            {
                resultDate = resultDate.AddDays(direction);
                if (IsTradingDay(resultDate))
                {
                    daysToAdd--;
                }
            }

            return resultDate;
        }


        private static bool IsTradingDay(DateTime date)
        {
            return date.DayOfWeek != DayOfWeek.Saturday && date.DayOfWeek != DayOfWeek.Sunday && !Holidays.Contains(date.Date);
        }

        private static readonly List<DateTime> Holidays =
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
}
