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
            transaction.Id = Guid.NewGuid().ToString();
            await _swingTransactions.InsertOneAsync(transaction);
        }

        public async Task InsertScalpingTransactionAsync(Transaction transaction)
        {
            transaction.Id = Guid.NewGuid().ToString();
            await _scalpingTransactions.InsertOneAsync(transaction);
        }

        public async Task UpdateTransactionOnSellAsync(string collectionName, string symbol, DateTime sellDate, decimal sellPrice, decimal profitLoss, decimal profitLossPercentage)
        {
            var collection = collectionName == "SwingTransactions" ? _swingTransactions : _scalpingTransactions;
            var filter = Builders<Transaction>.Filter.And(
                Builders<Transaction>.Filter.Eq(t => t.Symbol, symbol),
                Builders<Transaction>.Filter.Eq(t => t.SellDate, DateTime.Now),
                Builders<Transaction>.Filter.Eq(t => t.IsOpen, false)
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
            var filter = Builders<Transaction>.Filter.Eq(t => t.IsOpen, true) &
                                  Builders<Transaction>.Filter.Eq(t => t.TransactionType, "Swing");
            return await _swingTransactions.Find(filter).ToListAsync();
        }

        public async Task<List<Transaction>> GetOpenScalpingTransactionsAsync()
        {
            var filter = Builders<Transaction>.Filter.Eq(t => t.IsOpen, true) &
                                 Builders<Transaction>.Filter.Eq(t => t.TransactionType, "Scalping");
            return await _scalpingTransactions.Find(filter).ToListAsync();
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
                // Use IST for date comparison
                var now = DateTime.UtcNow.AddHours(5.5); // Convert to IST
                var today = now.Date;
                var tomorrow = today.AddDays(1);

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
            var scalpingCount = await _scalpingTransactions.CountDocumentsAsync(Builders<Transaction>.Filter.Eq(t => t.IsOpen, true));
            return (int)scalpingCount;
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

        private bool IsTradingDay(DateTime date)
        {
            return date.DayOfWeek != DayOfWeek.Saturday && date.DayOfWeek != DayOfWeek.Sunday;
        }
    }
}
