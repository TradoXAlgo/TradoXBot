namespace TradoXBot.Models;

public class Transaction
{
    public string? Id { get; set; }
    public string? OrderId { get; set; }
    public string? StockName { get; set; }
    public string? Symbol { get; set; }
    public DateTime BuyDate { get; set; }
    public decimal BuyPrice { get; set; }
    public int Quantity { get; set; }
    public DateTime ExpiryDate { get; set; }
    public DateTime? SellDate { get; set; }
    public decimal? SellPrice { get; set; }
    public decimal? ProfitLoss { get; set; }
    public decimal? ProfitLossPct { get; set; }
    public decimal OpenPrice { get; set; }
    public decimal HighPrice { get; set; }
    public decimal LowPrice { get; set; }
    public decimal ClosePrice { get; set; }
    public bool IsOpen { get; set; } = true;
}
