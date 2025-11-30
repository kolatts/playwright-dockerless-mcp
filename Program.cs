// Copyright (c) Playwright MCP Server. All rights reserved.
// Licensed under the MIT license.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Playwright;

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

        var server = new McpServer(browserType, headless);
        await server.RunAsync();
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Playwright MCP Server - A local MCP server for Playwright browser automation

            Usage: PlaywrightMcpServer [options]

            Options:
              --browser, -b <type>   Browser type: chromium, firefox, webkit (default: chromium)
              --headed               Run browser in headed mode (default: headless)
              --help, -h             Show this help message
              --version, -v          Show version information

            The server communicates via stdin/stdout using the MCP protocol.
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

    // Common description strings for tool definitions
    private const string ElementDescription = "Human-readable element description used to obtain permission to interact with the element";
    private const string RefDescription = "Exact target element reference from the page snapshot";

    private readonly string _browserType;
    private readonly bool _headless;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _currentPage;
    private readonly List<IConsoleMessage> _consoleMessages = [];
    private readonly List<IRequest> _networkRequests = [];
    private readonly SemaphoreSlim _browserLock = new(1, 1);

    public McpServer(string browserType, bool headless)
    {
        _browserType = browserType;
        _headless = headless;
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
        var tools = GetToolDefinitions();
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
            var result = await ExecuteToolAsync(toolName, arguments);
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

    private async Task<string> ExecuteToolAsync(string toolName, JsonNode? arguments)
    {
        return toolName switch
        {
            "browser_navigate" => await NavigateAsync(arguments),
            "browser_snapshot" => await SnapshotAsync(),
            "browser_click" => await ClickAsync(arguments),
            "browser_type" => await TypeAsync(arguments),
            "browser_fill_form" => await FillFormAsync(arguments),
            "browser_select_option" => await SelectOptionAsync(arguments),
            "browser_hover" => await HoverAsync(arguments),
            "browser_drag" => await DragAsync(arguments),
            "browser_press_key" => await PressKeyAsync(arguments),
            "browser_take_screenshot" => await TakeScreenshotAsync(arguments),
            "browser_evaluate" => await EvaluateAsync(arguments),
            "browser_console_messages" => GetConsoleMessages(),
            "browser_network_requests" => GetNetworkRequests(),
            "browser_file_upload" => await FileUploadAsync(arguments),
            "browser_handle_dialog" => await HandleDialogAsync(arguments),
            "browser_tabs" => await TabsAsync(arguments),
            "browser_navigate_back" => await NavigateBackAsync(),
            "browser_wait_for" => await WaitForAsync(arguments),
            "browser_close" => await CloseAsync(),
            "browser_resize" => await ResizeAsync(arguments),
            "browser_install" => await InstallBrowserAsync(),
            _ => throw new InvalidOperationException($"Unknown tool: {toolName}")
        };
    }

    private async Task EnsureBrowserAsync()
    {
        await _browserLock.WaitAsync();
        try
        {
            if (_playwright == null)
            {
                _playwright = await Playwright.CreateAsync();
            }

            if (_browser == null || !_browser.IsConnected)
            {
                var browserType = _browserType switch
                {
                    "firefox" => _playwright.Firefox,
                    "webkit" => _playwright.Webkit,
                    _ => _playwright.Chromium
                };

                _browser = await browserType.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = _headless
                });
            }

            if (_context == null)
            {
                _context = await _browser.NewContextAsync();
            }

            if (_currentPage == null || _currentPage.IsClosed)
            {
                _currentPage = await _context.NewPageAsync();
                SetupPageEventHandlers(_currentPage);
            }
        }
        finally
        {
            _browserLock.Release();
        }
    }

    private void SetupPageEventHandlers(IPage page)
    {
        page.Console += (_, msg) => _consoleMessages.Add(msg);
        page.Request += (_, req) => _networkRequests.Add(req);
    }

    private async Task<string> NavigateAsync(JsonNode? arguments)
    {
        var url = arguments?["url"]?.GetValue<string>()
            ?? throw new ArgumentException("Missing required parameter: url");

        await EnsureBrowserAsync();
        var response = await _currentPage!.GotoAsync(url);

        return JsonSerializer.Serialize(new
        {
            success = true,
            url = _currentPage.Url,
            title = await _currentPage.TitleAsync(),
            status = response?.Status
        }, s_jsonOptions);
    }

    private async Task<string> SnapshotAsync()
    {
        await EnsureBrowserAsync();

        // Note: IPage.Accessibility.SnapshotAsync is marked obsolete (CS0612 - Obsolete member).
        // This API is still functional in Playwright 1.49 and is the standard way to get accessibility snapshots.
        // When Playwright removes this API, migrate to the replacement API (if available) or use page.Locator().AriaSnapshot().
#pragma warning disable CS0612 // Type or member is obsolete
        var snapshot = await _currentPage!.Accessibility.SnapshotAsync();
#pragma warning restore CS0612 // Type or member is obsolete
        var url = _currentPage.Url;
        var title = await _currentPage.TitleAsync();

        return JsonSerializer.Serialize(new
        {
            url,
            title,
            snapshot
        }, s_jsonOptions);
    }

    private async Task<string> ClickAsync(JsonNode? arguments)
    {
        var element = arguments?["element"]?.GetValue<string>();
        var selector = arguments?["ref"]?.GetValue<string>();
        var button = arguments?["button"]?.GetValue<string>() ?? "left";
        var doubleClick = arguments?["doubleClick"]?.GetValue<bool>() ?? false;

        if (string.IsNullOrEmpty(selector))
        {
            throw new ArgumentException("Missing required parameter: ref");
        }

        await EnsureBrowserAsync();

        var mouseButton = button.ToLowerInvariant() switch
        {
            "right" => MouseButton.Right,
            "middle" => MouseButton.Middle,
            _ => MouseButton.Left
        };

        var locator = _currentPage!.Locator(selector);

        if (doubleClick)
        {
            await locator.DblClickAsync(new LocatorDblClickOptions { Button = mouseButton });
        }
        else
        {
            await locator.ClickAsync(new LocatorClickOptions { Button = mouseButton });
        }

        return JsonSerializer.Serialize(new
        {
            success = true,
            element,
            action = doubleClick ? "double-click" : "click"
        }, s_jsonOptions);
    }

    private async Task<string> TypeAsync(JsonNode? arguments)
    {
        var text = arguments?["text"]?.GetValue<string>()
            ?? throw new ArgumentException("Missing required parameter: text");
        var selector = arguments?["ref"]?.GetValue<string>()
            ?? throw new ArgumentException("Missing required parameter: ref");
        var slowly = arguments?["slowly"]?.GetValue<bool>() ?? false;
        var submit = arguments?["submit"]?.GetValue<bool>() ?? false;

        await EnsureBrowserAsync();

        var locator = _currentPage!.Locator(selector);

        if (slowly)
        {
            await locator.PressSequentiallyAsync(text);
        }
        else
        {
            await locator.FillAsync(text);
        }

        if (submit)
        {
            await locator.PressAsync("Enter");
        }

        return JsonSerializer.Serialize(new
        {
            success = true,
            text,
            slowly,
            submitted = submit
        }, s_jsonOptions);
    }

    private async Task<string> FillFormAsync(JsonNode? arguments)
    {
        var fields = arguments?["fields"]?.AsArray()
            ?? throw new ArgumentException("Missing required parameter: fields");

        await EnsureBrowserAsync();

        var results = new List<object>();

        foreach (var field in fields)
        {
            var name = field?["name"]?.GetValue<string>();
            var type = field?["type"]?.GetValue<string>();
            var selector = field?["ref"]?.GetValue<string>();
            var value = field?["value"]?.GetValue<string>();

            if (string.IsNullOrEmpty(selector) || string.IsNullOrEmpty(value))
            {
                continue;
            }

            var locator = _currentPage!.Locator(selector);

            switch (type?.ToLowerInvariant())
            {
                case "checkbox":
                    if (bool.TryParse(value, out var isChecked))
                    {
                        await locator.SetCheckedAsync(isChecked);
                    }
                    break;
                case "radio":
                    await locator.CheckAsync();
                    break;
                case "combobox":
                    await locator.SelectOptionAsync(value);
                    break;
                case "slider":
                    await locator.FillAsync(value);
                    break;
                default:
                    await locator.FillAsync(value);
                    break;
            }

            results.Add(new { name, type, success = true });
        }

        return JsonSerializer.Serialize(new
        {
            success = true,
            fields = results
        }, s_jsonOptions);
    }

    private async Task<string> SelectOptionAsync(JsonNode? arguments)
    {
        var selector = arguments?["ref"]?.GetValue<string>()
            ?? throw new ArgumentException("Missing required parameter: ref");
        var values = arguments?["values"]?.AsArray()?.Select(v => v?.GetValue<string>() ?? "").ToArray()
            ?? throw new ArgumentException("Missing required parameter: values");

        await EnsureBrowserAsync();

        var locator = _currentPage!.Locator(selector);
        var selected = await locator.SelectOptionAsync(values);

        return JsonSerializer.Serialize(new
        {
            success = true,
            selectedValues = selected
        }, s_jsonOptions);
    }

    private async Task<string> HoverAsync(JsonNode? arguments)
    {
        var selector = arguments?["ref"]?.GetValue<string>()
            ?? throw new ArgumentException("Missing required parameter: ref");

        await EnsureBrowserAsync();

        await _currentPage!.Locator(selector).HoverAsync();

        return JsonSerializer.Serialize(new
        {
            success = true
        }, s_jsonOptions);
    }

    private async Task<string> DragAsync(JsonNode? arguments)
    {
        var startRef = arguments?["startRef"]?.GetValue<string>()
            ?? throw new ArgumentException("Missing required parameter: startRef");
        var endRef = arguments?["endRef"]?.GetValue<string>()
            ?? throw new ArgumentException("Missing required parameter: endRef");

        await EnsureBrowserAsync();

        var source = _currentPage!.Locator(startRef);
        var target = _currentPage.Locator(endRef);

        await source.DragToAsync(target);

        return JsonSerializer.Serialize(new
        {
            success = true
        }, s_jsonOptions);
    }

    private async Task<string> PressKeyAsync(JsonNode? arguments)
    {
        var key = arguments?["key"]?.GetValue<string>()
            ?? throw new ArgumentException("Missing required parameter: key");

        await EnsureBrowserAsync();

        await _currentPage!.Keyboard.PressAsync(key);

        return JsonSerializer.Serialize(new
        {
            success = true,
            key
        }, s_jsonOptions);
    }

    private async Task<string> TakeScreenshotAsync(JsonNode? arguments)
    {
        var filename = arguments?["filename"]?.GetValue<string>();
        var fullPage = arguments?["fullPage"]?.GetValue<bool>() ?? false;
        var type = arguments?["type"]?.GetValue<string>() ?? "png";
        var elementRef = arguments?["ref"]?.GetValue<string>();

        await EnsureBrowserAsync();

        var screenshotType = type.ToLowerInvariant() == "jpeg"
            ? ScreenshotType.Jpeg
            : ScreenshotType.Png;

        byte[] screenshot;

        if (!string.IsNullOrEmpty(elementRef))
        {
            screenshot = await _currentPage!.Locator(elementRef).ScreenshotAsync(new LocatorScreenshotOptions
            {
                Type = screenshotType
            });
        }
        else
        {
            screenshot = await _currentPage!.ScreenshotAsync(new PageScreenshotOptions
            {
                FullPage = fullPage,
                Type = screenshotType
            });
        }

        if (!string.IsNullOrEmpty(filename))
        {
            await File.WriteAllBytesAsync(filename, screenshot);
            return JsonSerializer.Serialize(new
            {
                success = true,
                filename,
                size = screenshot.Length
            }, s_jsonOptions);
        }

        var base64 = Convert.ToBase64String(screenshot);
        return JsonSerializer.Serialize(new
        {
            success = true,
            data = base64,
            mimeType = screenshotType == ScreenshotType.Jpeg ? "image/jpeg" : "image/png",
            size = screenshot.Length
        }, s_jsonOptions);
    }

    private async Task<string> EvaluateAsync(JsonNode? arguments)
    {
        var function = arguments?["function"]?.GetValue<string>()
            ?? throw new ArgumentException("Missing required parameter: function");
        var elementRef = arguments?["ref"]?.GetValue<string>();

        await EnsureBrowserAsync();

        object? result;

        if (!string.IsNullOrEmpty(elementRef))
        {
            var locator = _currentPage!.Locator(elementRef);
            result = await locator.EvaluateAsync(function);
        }
        else
        {
            result = await _currentPage!.EvaluateAsync(function);
        }

        return JsonSerializer.Serialize(new
        {
            success = true,
            result
        }, s_jsonOptions);
    }

    private string GetConsoleMessages()
    {
        var messages = _consoleMessages.Select(m => new
        {
            type = m.Type,
            text = m.Text,
            location = m.Location
        }).ToList();

        return JsonSerializer.Serialize(new
        {
            messages
        }, s_jsonOptions);
    }

    private string GetNetworkRequests()
    {
        var requests = _networkRequests.Select(r => new
        {
            url = r.Url,
            method = r.Method,
            resourceType = r.ResourceType
        }).ToList();

        return JsonSerializer.Serialize(new
        {
            requests
        }, s_jsonOptions);
    }

    private async Task<string> FileUploadAsync(JsonNode? arguments)
    {
        var paths = arguments?["paths"]?.AsArray()?.Select(p => p?.GetValue<string>() ?? "").ToArray();

        await EnsureBrowserAsync();

        if (paths == null || paths.Length == 0)
        {
            // Cancel file chooser
            return JsonSerializer.Serialize(new
            {
                success = true,
                cancelled = true
            }, s_jsonOptions);
        }

        // Wait for file chooser and set files
        var fileChooserTask = _currentPage!.WaitForFileChooserAsync();

        // Trigger file input if needed - usually this is triggered by a click
        var fileChooser = await fileChooserTask;
        await fileChooser.SetFilesAsync(paths);

        return JsonSerializer.Serialize(new
        {
            success = true,
            uploadedFiles = paths
        }, s_jsonOptions);
    }

    private async Task<string> HandleDialogAsync(JsonNode? arguments)
    {
        var accept = arguments?["accept"]?.GetValue<bool>() ?? true;
        var promptText = arguments?["promptText"]?.GetValue<string>();

        await EnsureBrowserAsync();

        // Set up handler for the next dialog
        _currentPage!.Dialog += async (_, dialog) =>
        {
            if (accept)
            {
                await dialog.AcceptAsync(promptText);
            }
            else
            {
                await dialog.DismissAsync();
            }
        };

        return JsonSerializer.Serialize(new
        {
            success = true,
            willAccept = accept,
            promptText
        }, s_jsonOptions);
    }

    private async Task<string> TabsAsync(JsonNode? arguments)
    {
        var action = arguments?["action"]?.GetValue<string>()
            ?? throw new ArgumentException("Missing required parameter: action");
        var index = arguments?["index"]?.GetValue<int>();

        await EnsureBrowserAsync();

        var pages = _context!.Pages;

        return action.ToLowerInvariant() switch
        {
            "list" => await ListTabsAsync(pages),

            "new" => await CreateNewTabAsync(),

            "close" => await CloseTabAsync(index),

            "select" => await SelectTabAsync(index ?? 0),

            _ => throw new ArgumentException($"Unknown tab action: {action}")
        };
    }

    private async Task<string> ListTabsAsync(IReadOnlyList<IPage> pages)
    {
        var tabs = new List<object>();
        for (int i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            tabs.Add(new
            {
                index = i,
                url = page.Url,
                title = await page.TitleAsync(),
                isCurrent = page == _currentPage
            });
        }

        return JsonSerializer.Serialize(new { tabs }, s_jsonOptions);
    }

    private async Task<string> CreateNewTabAsync()
    {
        _currentPage = await _context!.NewPageAsync();
        SetupPageEventHandlers(_currentPage);

        return JsonSerializer.Serialize(new
        {
            success = true,
            index = _context.Pages.Count - 1
        }, s_jsonOptions);
    }

    private async Task<string> CloseTabAsync(int? index)
    {
        var pages = _context!.Pages;

        if (index.HasValue && index.Value >= 0 && index.Value < pages.Count)
        {
            await pages[index.Value].CloseAsync();
        }
        else if (_currentPage != null)
        {
            await _currentPage.CloseAsync();
        }

        // Select another page if available
        if (_context.Pages.Count > 0)
        {
            _currentPage = _context.Pages[^1];
        }
        else
        {
            _currentPage = await _context.NewPageAsync();
            SetupPageEventHandlers(_currentPage);
        }

        return JsonSerializer.Serialize(new
        {
            success = true
        }, s_jsonOptions);
    }

    private Task<string> SelectTabAsync(int index)
    {
        var pages = _context!.Pages;

        if (index < 0 || index >= pages.Count)
        {
            throw new ArgumentException($"Invalid tab index: {index}");
        }

        _currentPage = pages[index];

        return Task.FromResult(JsonSerializer.Serialize(new
        {
            success = true,
            index,
            url = _currentPage.Url
        }, s_jsonOptions));
    }

    private async Task<string> NavigateBackAsync()
    {
        await EnsureBrowserAsync();

        var response = await _currentPage!.GoBackAsync();

        return JsonSerializer.Serialize(new
        {
            success = true,
            url = _currentPage.Url,
            status = response?.Status
        }, s_jsonOptions);
    }

    private async Task<string> WaitForAsync(JsonNode? arguments)
    {
        var text = arguments?["text"]?.GetValue<string>();
        var textGone = arguments?["textGone"]?.GetValue<string>();
        var time = arguments?["time"]?.GetValue<double>();

        await EnsureBrowserAsync();

        if (!string.IsNullOrEmpty(text))
        {
            await _currentPage!.GetByText(text).WaitForAsync();
            return JsonSerializer.Serialize(new
            {
                success = true,
                waitedFor = "text",
                text
            }, s_jsonOptions);
        }

        if (!string.IsNullOrEmpty(textGone))
        {
            await _currentPage!.GetByText(textGone).WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Hidden
            });
            return JsonSerializer.Serialize(new
            {
                success = true,
                waitedFor = "textGone",
                textGone
            }, s_jsonOptions);
        }

        if (time.HasValue)
        {
            await Task.Delay(TimeSpan.FromSeconds(time.Value));
            return JsonSerializer.Serialize(new
            {
                success = true,
                waitedFor = "time",
                seconds = time.Value
            }, s_jsonOptions);
        }

        throw new ArgumentException("Must specify text, textGone, or time parameter");
    }

    private async Task<string> CloseAsync()
    {
        if (_currentPage != null && !_currentPage.IsClosed)
        {
            await _currentPage.CloseAsync();
        }

        return JsonSerializer.Serialize(new
        {
            success = true
        }, s_jsonOptions);
    }

    private async Task<string> ResizeAsync(JsonNode? arguments)
    {
        var width = arguments?["width"]?.GetValue<int>()
            ?? throw new ArgumentException("Missing required parameter: width");
        var height = arguments?["height"]?.GetValue<int>()
            ?? throw new ArgumentException("Missing required parameter: height");

        await EnsureBrowserAsync();

        await _currentPage!.SetViewportSizeAsync(width, height);

        return JsonSerializer.Serialize(new
        {
            success = true,
            width,
            height
        }, s_jsonOptions);
    }

    private static async Task<string> InstallBrowserAsync()
    {
        var exitCode = Microsoft.Playwright.Program.Main(["install"]);

        return JsonSerializer.Serialize(new
        {
            success = exitCode == 0,
            exitCode
        }, s_jsonOptions);
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

    private static List<object> GetToolDefinitions()
    {
        return
        [
            new
            {
                name = "browser_navigate",
                description = "Navigate to a URL",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["url"] = new { type = "string", description = "The URL to navigate to" }
                    },
                    required = new[] { "url" }
                }
            },
            new
            {
                name = "browser_snapshot",
                description = "Capture accessibility snapshot of the current page, this is better than screenshot",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>(),
                    required = Array.Empty<string>()
                }
            },
            new
            {
                name = "browser_click",
                description = "Perform click on a web page",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["element"] = new { type = "string", description = ElementDescription },
                        ["ref"] = new { type = "string", description = RefDescription },
                        ["button"] = new { type = "string", description = "Button to click, defaults to left", @enum = new[] { "left", "right", "middle" } },
                        ["doubleClick"] = new { type = "boolean", description = "Whether to perform a double click instead of a single click" }
                    },
                    required = new[] { "element", "ref" }
                }
            },
            new
            {
                name = "browser_type",
                description = "Type text into editable element",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["element"] = new { type = "string", description = ElementDescription },
                        ["ref"] = new { type = "string", description = RefDescription },
                        ["text"] = new { type = "string", description = "Text to type into the element" },
                        ["slowly"] = new { type = "boolean", description = "Whether to type one character at a time. Useful for triggering key handlers in the page. By default entire text is filled in at once." },
                        ["submit"] = new { type = "boolean", description = "Whether to submit entered text (press Enter after)" }
                    },
                    required = new[] { "element", "ref", "text" }
                }
            },
            new
            {
                name = "browser_fill_form",
                description = "Fill multiple form fields",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["fields"] = new
                        {
                            type = "array",
                            description = "Fields to fill in",
                            items = new
                            {
                                type = "object",
                                properties = new Dictionary<string, object>
                                {
                                    ["name"] = new { type = "string", description = "Human-readable field name" },
                                    ["ref"] = new { type = "string", description = "Exact target field reference from the page snapshot" },
                                    ["type"] = new { type = "string", description = "Type of the field", @enum = new[] { "textbox", "checkbox", "radio", "combobox", "slider" } },
                                    ["value"] = new { type = "string", description = "Value to fill in the field. If the field is a checkbox, the value should be `true` or `false`. If the field is a combobox, the value should be the text of the option." }
                                },
                                required = new[] { "name", "type", "ref", "value" }
                            }
                        }
                    },
                    required = new[] { "fields" }
                }
            },
            new
            {
                name = "browser_select_option",
                description = "Select an option in a dropdown",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["element"] = new { type = "string", description = ElementDescription },
                        ["ref"] = new { type = "string", description = RefDescription },
                        ["values"] = new { type = "array", items = new { type = "string" }, description = "Array of values to select in the dropdown. This can be a single value or multiple values." }
                    },
                    required = new[] { "element", "ref", "values" }
                }
            },
            new
            {
                name = "browser_hover",
                description = "Hover over element on page",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["element"] = new { type = "string", description = ElementDescription },
                        ["ref"] = new { type = "string", description = RefDescription }
                    },
                    required = new[] { "element", "ref" }
                }
            },
            new
            {
                name = "browser_drag",
                description = "Perform drag and drop between two elements",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["startElement"] = new { type = "string", description = "Human-readable source element description used to obtain the permission to interact with the element" },
                        ["startRef"] = new { type = "string", description = "Exact source element reference from the page snapshot" },
                        ["endElement"] = new { type = "string", description = "Human-readable target element description used to obtain the permission to interact with the element" },
                        ["endRef"] = new { type = "string", description = "Exact target element reference from the page snapshot" }
                    },
                    required = new[] { "startElement", "startRef", "endElement", "endRef" }
                }
            },
            new
            {
                name = "browser_press_key",
                description = "Press a key on the keyboard",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["key"] = new { type = "string", description = "Name of the key to press or a character to generate, such as `ArrowLeft` or `a`" }
                    },
                    required = new[] { "key" }
                }
            },
            new
            {
                name = "browser_take_screenshot",
                description = "Take a screenshot of the current page. You can't perform actions based on the screenshot, use browser_snapshot for actions.",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["element"] = new { type = "string", description = $"{ElementDescription}. If not provided, the screenshot will be taken of viewport. If element is provided, ref must be provided too." },
                        ["ref"] = new { type = "string", description = $"{RefDescription}. If not provided, the screenshot will be taken of viewport. If ref is provided, element must be provided too." },
                        ["fullPage"] = new { type = "boolean", description = "When true, takes a screenshot of the full scrollable page, instead of the currently visible viewport. Cannot be used with element screenshots." },
                        ["type"] = new { type = "string", description = "Image format for the screenshot. Default is png.", @enum = new[] { "png", "jpeg" } },
                        ["filename"] = new { type = "string", description = "File name to save the screenshot to. Defaults to returning base64 encoded data if not specified." }
                    },
                    required = Array.Empty<string>()
                }
            },
            new
            {
                name = "browser_evaluate",
                description = "Evaluate JavaScript expression on page or element",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["element"] = new { type = "string", description = ElementDescription },
                        ["ref"] = new { type = "string", description = RefDescription },
                        ["function"] = new { type = "string", description = "() => { /* code */ } or (element) => { /* code */ } when element is provided" }
                    },
                    required = new[] { "function" }
                }
            },
            new
            {
                name = "browser_console_messages",
                description = "Returns all console messages",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>(),
                    required = Array.Empty<string>()
                }
            },
            new
            {
                name = "browser_network_requests",
                description = "Returns all network requests since loading the page",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>(),
                    required = Array.Empty<string>()
                }
            },
            new
            {
                name = "browser_file_upload",
                description = "Upload one or multiple files",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["paths"] = new { type = "array", items = new { type = "string" }, description = "The absolute paths to the files to upload. Can be single file or multiple files. If omitted, file chooser is cancelled." }
                    },
                    required = Array.Empty<string>()
                }
            },
            new
            {
                name = "browser_handle_dialog",
                description = "Handle a dialog",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["accept"] = new { type = "boolean", description = "Whether to accept the dialog." },
                        ["promptText"] = new { type = "string", description = "The text of the prompt in case of a prompt dialog." }
                    },
                    required = new[] { "accept" }
                }
            },
            new
            {
                name = "browser_tabs",
                description = "List, create, close, or select a browser tab.",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["action"] = new { type = "string", description = "Operation to perform", @enum = new[] { "list", "new", "close", "select" } },
                        ["index"] = new { type = "number", description = "Tab index, used for close/select. If omitted for close, current tab is closed." }
                    },
                    required = new[] { "action" }
                }
            },
            new
            {
                name = "browser_navigate_back",
                description = "Go back to the previous page",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>(),
                    required = Array.Empty<string>()
                }
            },
            new
            {
                name = "browser_wait_for",
                description = "Wait for text to appear or disappear or a specified time to pass",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["text"] = new { type = "string", description = "The text to wait for" },
                        ["textGone"] = new { type = "string", description = "The text to wait for to disappear" },
                        ["time"] = new { type = "number", description = "The time to wait in seconds" }
                    },
                    required = Array.Empty<string>()
                }
            },
            new
            {
                name = "browser_close",
                description = "Close the page",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>(),
                    required = Array.Empty<string>()
                }
            },
            new
            {
                name = "browser_resize",
                description = "Resize the browser window",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["width"] = new { type = "number", description = "Width of the browser window" },
                        ["height"] = new { type = "number", description = "Height of the browser window" }
                    },
                    required = new[] { "width", "height" }
                }
            },
            new
            {
                name = "browser_install",
                description = "Install the browser specified in the config. Call this if you get an error about the browser not being installed.",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>(),
                    required = Array.Empty<string>()
                }
            }
        ];
    }

    public async ValueTask DisposeAsync()
    {
        if (_currentPage != null)
        {
            await _currentPage.CloseAsync();
        }

        if (_context != null)
        {
            await _context.CloseAsync();
        }

        if (_browser != null)
        {
            await _browser.CloseAsync();
        }

        _playwright?.Dispose();
        _browserLock.Dispose();
    }
}
