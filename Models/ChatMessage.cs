namespace _8BB_TODO_.NET.Models;

public class ChatMessage
{
    public int Id { get; set; }
    public string Sender { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    
    // Code Review Payload Fields
    public bool HasCode { get; set; }
    public string? Language { get; set; }
    public string? CodeSnippet { get; set; }
}