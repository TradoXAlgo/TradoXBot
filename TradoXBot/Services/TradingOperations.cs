using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;
using TradoXBot.Jobs;
namespace TradoXBot.Services
{
    public class TradingOperations
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IConfiguration _configuration;
        private readonly ILogger<TradingOperations> _logger;

        public TradingOperations(IServiceProvider serviceProvider, IConfiguration configuration, ILogger<TradingOperations> logger)
        {
            _serviceProvider = serviceProvider;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task ExecuteSwingBuyAsync()
        {
            try
            {
                _logger.LogInformation("Executing Swing Buy operation");
                var job = ActivatorUtilities.CreateInstance<BuyJob>(_serviceProvider);
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
                _logger.LogInformation("Executing Swing Sell operation");
                var job = ActivatorUtilities.CreateInstance<SellJob>(_serviceProvider);
                await job.Execute(null);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error executing Swing Sell: {ex.Message}");
                throw;
            }
        }

        public async Task ExecuteScalpingMonitorAsync()
        {
            try
            {
                _logger.LogInformation("Executing Scalping Monitor operation");
                var job = ActivatorUtilities.CreateInstance<ScalpingMonitorJob>(_serviceProvider);
                await job.Execute(null);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error executing Scalping Monitor: {ex.Message}");
                throw;
            }
        }

        public async Task ExecuteStatusReportAsync()
        {
            try
            {
                _logger.LogInformation("Executing Status Report operation");
                var job = ActivatorUtilities.CreateInstance<StatusJob>(_serviceProvider);
                await job.Execute(null);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error executing Status Report: {ex.Message}");
                throw;
            }
        }

        public async Task<(int Trades, decimal TotalProfitLoss, double WinRate)> ExecuteSwingBacktestAsync(string symbol)
        {
            try
            {
                _logger.LogInformation($"Executing Swing Backtest for {symbol}");
                var backtester = _serviceProvider.GetService<Backtester>();
                return await backtester.BacktestSwingStrategyAsync(symbol);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error executing Swing Backtest for {symbol}: {ex.Message}");
                throw;
            }
        }

        public async Task<(int Trades, decimal TotalProfitLoss, double WinRate)> ExecuteScalpingBacktestAsync(string symbol)
        {
            try
            {
                _logger.LogInformation($"Executing Scalping Backtest for {symbol}");
                var backtester = _serviceProvider.GetService<Backtester>();
                return await backtester.BacktestScalpingStrategyAsync(symbol);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error executing Scalping Backtest for {symbol}: {ex.Message}");
                throw;
            }
        }
    }
}