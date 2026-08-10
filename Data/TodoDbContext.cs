using Microsoft.EntityFrameworkCore;
using _8BB_TODO_.NET.Models;

namespace _8BB_TODO_.NET.Data;

/// 
/// Database context bridging Entity Framework Core with the SQLite database.
/// 
public class TodoDbContext : DbContext
{
    public TodoDbContext(DbContextOptions options) : base(options)
    {
    }

    /// 
    /// Database table representation for Todo items.
    /// 
    public DbSet<_8BB_TODO_.NET.Models.TodoItem> TodoItems { get; set; } = null!;
}