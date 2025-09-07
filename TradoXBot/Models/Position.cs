namespace TradoXBot.Models;

public class Position
{
    public string? Symbol { get; set; }
    public string? Token { get; set; }
    public int NetQuantity { get; set; }
    public decimal AvgPrice { get; set; }
}