namespace TradoXBot.Models;

public class PortfolioHolding
{
    public string? Symbol { get; set; }
    public string? Token { get; set; }
    public int Quantity { get; set; }
    public decimal AvgPrice { get; set; }
}