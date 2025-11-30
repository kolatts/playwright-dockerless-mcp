// Copyright (c) Playwright MCP Server. All rights reserved.
// Licensed under the MIT license.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PlaywrightMcpServer;

/// <summary>
/// Main entry point for the Playwright MCP Server.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string browserType = "chromium";
        bool headless = true;
        bool httpMode = false;
        int port = 5000;

        // Parse command line arguments
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--browser":
                case "-b":
                    if (i + 1 < args.Length)
                    {
                        browserType = args[++i].ToLowerInvariant();
                    }
                    break;
                case "--headed":
                    headless = false;
                    break;
                case "--http":
                    httpMode = true;
                    break;
                case "--port":
                case "-p":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var parsedPort))
                    {
                        port = parsedPort;
                    }
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    return 0;
                case "--version":
                case "-v":
                    Console.WriteLine("PlaywrightMcpServer v1.0.0");
                    return 0;
            }
        }

        if (httpMode)
        {
            await HttpServer.RunAsync(browserType, headless, port);
        }
        else
        {
            var server = new McpServer(browserType, headless);
            await server.RunAsync();
        }
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Playwright MCP Server - A local server for Playwright browser automation

            Usage: PlaywrightMcpServer [options]

            Options:
              --browser, -b <type>   Browser type: chromium, firefox, webkit (default: chromium)
              --headed               Run browser in headed mode (default: headless)
              --http                 Run as HTTP server instead of MCP stdin/stdout mode
              --port, -p <port>      HTTP server port (default: 5000, only used with --http)
              --help, -h             Show this help message
              --version, -v          Show version information

            Modes:
              Default (MCP):         Communicates via stdin/stdout using the MCP protocol
              HTTP (--http):         Starts an HTTP server with REST API endpoints

            HTTP API Endpoints (when using --http):
              GET  /tools            List all available browser automation tools
              POST /tools/{name}     Execute a specific tool with JSON body arguments
              GET  /health           Health check endpoint

            Examples:
              # Run in MCP mode (default)
              PlaywrightMcpServer --browser chromium

              # Run as HTTP server on port 8080
              PlaywrightMcpServer --http --port 8080 --headed

              # Call HTTP API to navigate
              curl -X POST http://localhost:5000/tools/browser_navigate \
                   -H "Content-Type: application/json" \
                   -d '{"url": "https://example.com"}'
            """);
    }
}

/// <summary>
/// MCP Server implementation for Playwright browser automation.
/// </summary>
public sealed class McpServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly BrowserAutomation _browserAutomation;

    public McpServer(string browserType, bool headless)
    {
        _browserAutomation = new BrowserAutomation(browserType, headless);
    }

    public async Task RunAsync()
    {
        using var inputReader = Console.OpenStandardInput();
        using var outputWriter = Console.OpenStandardOutput();
        using var reader = new StreamReader(inputReader, Encoding.UTF8);
        using var writer = new StreamWriter(outputWriter, new UTF8Encoding(false)) { AutoFlush = true };

        while (true)
        {
            string? line = await reader.ReadLineAsync();
            if (line == null)
            {
                break;
            }

            try
            {
                var response = await ProcessMessageAsync(line);
                if (response != null)
                {
                    await writer.WriteLineAsync(response);
                }
            }
            catch (Exception ex)
            {
                var errorResponse = CreateErrorResponse(null, -32603, $"Internal error: {ex.Message}");
                await writer.WriteLineAsync(errorResponse);
            }
        }
    }

    private async Task<string?> ProcessMessageAsync(string message)
    {
        JsonNode? json;
        try
        {
            json = JsonNode.Parse(message);
        }
        catch (JsonException)
        {
            return CreateErrorResponse(null, -32700, "Parse error");
        }

        if (json == null)
        {
            return CreateErrorResponse(null, -32700, "Parse error");
        }

        var method = json["method"]?.GetValue<string>();
        var id = json["id"];
        var @params = json["params"];

        if (method == null)
        {
            return CreateErrorResponse(id, -32600, "Invalid request: missing method");
        }

        return method switch
        {
            "initialize" => HandleInitialize(id, @params),
            "tools/list" => HandleToolsList(id),
            "tools/call" => await HandleToolCallAsync(id, @params),
            "notifications/initialized" => null, // Notification, no response
            _ => CreateErrorResponse(id, -32601, $"Method not found: {method}")
        };
    }

    private static string HandleInitialize(JsonNode? id, JsonNode? @params)
    {
        var result = new
        {
            protocolVersion = "2024-11-05",
            capabilities = new
            {
                tools = new { }
            },
            serverInfo = new
            {
                name = "playwright-mcp-server",
                version = "1.0.0"
            }
        };

        return CreateSuccessResponse(id, result);
    }

    private static string HandleToolsList(JsonNode? id)
    {
        var tools = BrowserAutomation.GetToolDefinitions();
        return CreateSuccessResponse(id, new { tools });
    }

    private async Task<string> HandleToolCallAsync(JsonNode? id, JsonNode? @params)
    {
        var toolName = @params?["name"]?.GetValue<string>();
        var arguments = @params?["arguments"];

        if (string.IsNullOrEmpty(toolName))
        {
            return CreateErrorResponse(id, -32602, "Invalid params: missing tool name");
        }

        try
        {
            var result = await _browserAutomation.ExecuteToolAsync(toolName, arguments);
            return CreateSuccessResponse(id, new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = result
                    }
                }
            });
        }
        catch (Exception ex)
        {
            return CreateSuccessResponse(id, new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = $"Error: {ex.Message}"
                    }
                },
                isError = true
            });
        }
    }

    private static string CreateSuccessResponse(JsonNode? id, object result)
    {
        var response = new
        {
            jsonrpc = "2.0",
            id = id?.GetValue<object>(),
            result
        };

        return JsonSerializer.Serialize(response, s_jsonOptions);
    }

    private static string CreateErrorResponse(JsonNode? id, int code, string message)
    {
        var response = new
        {
            jsonrpc = "2.0",
            id = id?.GetValue<object>(),
            error = new
            {
                code,
                message
            }
        };

        return JsonSerializer.Serialize(response, s_jsonOptions);
    }

    public async ValueTask DisposeAsync()
    {
        await _browserAutomation.DisposeAsync();
    }
}
