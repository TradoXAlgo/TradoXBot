namespace TradoXBot.Models;

public class Quote
{
    public decimal LastPrice { get; set; }
    public decimal PrevClose { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public long Volume { get; set; }
}