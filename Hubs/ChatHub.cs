using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using _8BB_TODO_.NET.Models;

namespace _8BB_TODO_.NET.Hubs;

public class ChatHub : Hub
{
    // Thread-safe dictionary to track ConnectionId -> Username
    private static readonly ConcurrentDictionary<string, string> _onlineUsers = new();

    public async Task JoinChat(string username)
    {
        // Map the unique WebSocket connection to the entered handle
        _onlineUsers[Context.ConnectionId] = username;
        
        // Announce the arrival
        await Clients.All.SendAsync("ReceiveMessage", new ChatMessage 
        { 
            Sender = "System", 
            Content = $"{username} has entered the 8BB Orbit.", 
            HasCode = false 
        });

        await BroadcastUserList();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // If they close the app, remove them and announce the departure
        if (_onlineUsers.TryRemove(Context.ConnectionId, out string? username))
        {
            await Clients.All.SendAsync("ReceiveMessage", new ChatMessage 
            { 
                Sender = "System", 
                Content = $"{username} has left the orbit.", 
                HasCode = false 
            });
            await BroadcastUserList();
        }
        await base.OnDisconnectedAsync(exception);
    }

    private async Task BroadcastUserList()
    {
        // Push the fresh list of names to every connected client
        var users = _onlineUsers.Values.Distinct().OrderBy(u => u).ToList();
        await Clients.All.SendAsync("UpdateUserList", users);
    }

    public async Task BroadcastMessage(string user, string message)
    {
        var msg = new ChatMessage
        {
            Sender = user,
            Content = message,
            HasCode = false
        };
        await Clients.All.SendAsync("ReceiveMessage", msg);
    }

    public async Task BroadcastCodeSnippet(string user, string language, string code, string description)
    {
        var msg = new ChatMessage
        {
            Sender = user,
            Content = description,
            HasCode = true,
            Language = language,
            CodeSnippet = code
        };
        await Clients.All.SendAsync("ReceiveMessage", msg);
    }
}
