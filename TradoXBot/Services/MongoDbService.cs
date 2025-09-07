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
                Builders<Transaction>.Filter.Eq(t => t.SellDate, null)
            );

            var update = Builders<Transaction>.Update
                .Set(t => t.SellDate, sellDate)
                .Set(t => t.SellPrice, sellPrice)
                .Set(t => t.ProfitLoss, profitLoss)
                .Set(t => t.ProfitLossPct, profitLossPercentage);

            await collection.UpdateOneAsync(filter, update);
        }

        public async Task InsertScannerStockAsync(ScannerStock scannerStock)
        {
            scannerStock.Id = Guid.NewGuid().ToString();
            await _scannerStocks.InsertOneAsync(scannerStock);
        }

        public async Task<List<Transaction>> GetOpenSwingTransactionsAsync()
        {
            var filter = Builders<Transaction>.Filter.Eq(t => t.SellDate, null) &
                                 Builders<Transaction>.Filter.Eq(t => t.IsOpen, true); ;
            return await _swingTransactions.Find(filter).ToListAsync();
        }

        public async Task<List<Transaction>> GetOpenScalpingTransactionsAsync()
        {
            var filter = Builders<Transaction>.Filter.Eq(t => t.SellDate, null);
            return await _scalpingTransactions.Find(filter).ToListAsync();
        }

        public async Task<List<Transaction>> GetClosedTransactionsAsync()
        {
            var filter = Builders<Transaction>.Filter.Ne(t => t.SellDate, null);
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

        public async Task<int> GetOpenPositionCountAsync()
        {
            var swingCount = await _swingTransactions.CountDocumentsAsync(Builders<Transaction>.Filter.Eq(t => t.SellDate, null));
            var scalpingCount = await _scalpingTransactions.CountDocumentsAsync(Builders<Transaction>.Filter.Eq(t => t.SellDate, null));
            return (int)(swingCount + scalpingCount);
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
