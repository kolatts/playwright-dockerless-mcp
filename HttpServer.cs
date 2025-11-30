// Copyright (c) Playwright MCP Server. All rights reserved.
// Licensed under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PlaywrightMcpServer;

/// <summary>
/// HTTP Server for Playwright browser automation, providing a REST API for agents.
/// </summary>
public static class HttpServer
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static async Task RunAsync(string browserType, bool headless, int port)
    {
        var builder = WebApplication.CreateBuilder();

        // Configure Kestrel to use the specified port
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenLocalhost(port);
        });

        // Suppress default logging noise
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var app = builder.Build();

        // Create browser automation instance as a singleton and ensure proper disposal
        await using var browserAutomation = new BrowserAutomation(browserType, headless);

        // Health check endpoint
        app.MapGet("/health", () => Results.Ok(new { status = "healthy", version = "1.0.0" }));

        // List all available tools
        app.MapGet("/tools", () =>
        {
            var tools = BrowserAutomation.GetToolDefinitions();
            return Results.Ok(new { tools });
        });

        // Execute a specific tool
        app.MapPost("/tools/{toolName}", async (string toolName, HttpContext context) =>
        {
            try
            {
                // Read the request body
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();

                JsonNode? arguments = null;
                if (!string.IsNullOrWhiteSpace(body))
                {
                    try
                    {
                        arguments = JsonNode.Parse(body);
                    }
                    catch (JsonException)
                    {
                        return Results.BadRequest(new { error = "Invalid JSON in request body" });
                    }
                }

                var result = await browserAutomation.ExecuteToolAsync(toolName, arguments);

                // Parse the result JSON and return it directly
                var resultJson = JsonNode.Parse(result);
                return Results.Ok(resultJson);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Unknown tool"))
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message, success = false }, statusCode: 500);
            }
        });

        Console.WriteLine($"Playwright HTTP Server starting on http://localhost:{port}");
        Console.WriteLine("Endpoints:");
        Console.WriteLine($"  GET  http://localhost:{port}/health - Health check");
        Console.WriteLine($"  GET  http://localhost:{port}/tools - List available tools");
        Console.WriteLine($"  POST http://localhost:{port}/tools/{{name}} - Execute a tool");
        Console.WriteLine();
        Console.WriteLine("Press Ctrl+C to stop the server");

        await app.RunAsync();
    }
}
