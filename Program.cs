using Microsoft.EntityFrameworkCore;
using _8BB_TODO_.NET.Data;
using _8BB_TODO_.NET.Models;
using _8BB_TODO_.NET.Hubs;

var builder = WebApplication.CreateBuilder(args);

// Register OpenAPI services
builder.Services.AddOpenApi();

// Register DbContext with SQLite
builder.Services.AddDbContext<_8BB_TODO_.NET.Data.TodoDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("SqliteConnection")));

// Register SignalR real-time web sockets
builder.Services.AddSignalR();

var app = builder.Build();

// Configure HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// --- TODO REST API ENDPOINTS ---

app.MapGet("/api/todoitems", async (_8BB_TODO_.NET.Data.TodoDbContext db) =>
    Results.Ok(await db.TodoItems.ToListAsync()));

app.MapGet("/api/todoitems/{id:int}", async (int id, _8BB_TODO_.NET.Data.TodoDbContext db) =>
    await db.TodoItems.FindAsync(id) is _8BB_TODO_.NET.Models.TodoItem todo
        ? Results.Ok(todo)
        : Results.NotFound());

app.MapPost("/api/todoitems", async (_8BB_TODO_.NET.Models.TodoItem todo, _8BB_TODO_.NET.Data.TodoDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(todo.Title))
    {
        return Results.BadRequest(new { error = "Title cannot be empty." });
    }

    todo.CreatedAt = DateTime.UtcNow;
    db.TodoItems.Add(todo);
    await db.SaveChangesAsync();

    return Results.Created($"/api/todoitems/{todo.Id}", todo);
});

app.MapPut("/api/todoitems/{id:int}", async (int id, _8BB_TODO_.NET.Models.TodoItem inputTodo, _8BB_TODO_.NET.Data.TodoDbContext db) =>
{
    var todo = await db.TodoItems.FindAsync(id);
    if (todo is null) return Results.NotFound();

    if (string.IsNullOrWhiteSpace(inputTodo.Title))
    {
        return Results.BadRequest(new { error = "Title cannot be empty." });
    }

    todo.Title = inputTodo.Title;
    todo.IsCompleted = inputTodo.IsCompleted;

    if (inputTodo.IsCompleted && todo.CompletedAt is null)
    {
        todo.CompletedAt = DateTime.UtcNow;
    }
    else if (!inputTodo.IsCompleted)
    {
        todo.CompletedAt = null;
    }

    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.MapDelete("/api/todoitems/{id:int}", async (int id, _8BB_TODO_.NET.Data.TodoDbContext db) =>
{
    if (await db.TodoItems.FindAsync(id) is _8BB_TODO_.NET.Models.TodoItem todo)
    {
        db.TodoItems.Remove(todo);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    return Results.NotFound();
});

// --- SIGNALR REAL-TIME CHAT HUB ---
app.MapHub<ChatHub>("/chathub");

app.Run();
