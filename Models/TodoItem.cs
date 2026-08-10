using System.ComponentModel.DataAnnotations;

namespace _8BB_TODO_.NET.Models;

/// 
/// Represents a single to-do task entity within the system.
/// 
public class TodoItem
{
    /// 
    /// Unique auto-incrementing identifier (Primary Key).
    /// 
    public int Id { get; set; }

    /// 
    /// The core description or title of the task.
    /// 
    [Required(ErrorMessage = "Title is required.")]
    [StringLength(200, ErrorMessage = "Title cannot exceed 200 characters.")]
    public string Title { get; set; } = string.Empty;

    /// 
    /// State flag indicating if the task is complete.
    /// 
    public bool IsCompleted { get; set; }

    /// 
    /// UTC timestamp recording when the task was created.
    /// 
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// 
    /// Optional UTC timestamp recording when the task was marked completed.
    /// 
    public DateTime? CompletedAt { get; set; }
}