# Playwright MCP Server for .NET

A lightweight Model Context Protocol (MCP) server for browser automation using Microsoft Playwright, built with .NET 8 C#. This server allows AI agents to interact with web browsers locally without requiring npm, Docker, or other external dependencies.

## Features

- **Pure .NET 8 Implementation**: No npm or Docker required
- **Full Playwright Browser Support**: Chromium, Firefox, and WebKit
- **Dual Mode Operation**: Run as MCP server (stdin/stdout) or HTTP REST API server
- **Comprehensive Browser Tools**: 21 browser automation tools for complete web interaction
- **Command Line Configuration**: Easy startup with command-line arguments
- **Agent-Friendly HTTP API**: Simple REST endpoints for easy integration with AI agents

## Requirements

- .NET 8.0 SDK or Runtime
- One of the supported browsers (can be installed via the `browser_install` tool)

## Installation

### Build from Source

```bash
# Clone the repository
git clone https://github.com/kolatts/playwright-dockerless-mcp.git
cd playwright-dockerless-mcp

# Build the project
dotnet build

# Run the server
dotnet run
```

### Install Browsers

Before using the browser tools, you need to install the browser binaries. You can do this by:

1. Using the `browser_install` tool via MCP or HTTP API
2. Or running the Playwright CLI:
   ```bash
   pwsh bin/Debug/net8.0/playwright.ps1 install
   ```

## Usage

### Command Line Options

```bash
PlaywrightMcpServer [options]

Options:
  --browser, -b <type>   Browser type: chromium, firefox, webkit (default: chromium)
  --headed               Run browser in headed mode (default: headless)
  --http                 Run as HTTP server instead of MCP stdin/stdout mode
  --port, -p <port>      HTTP server port (default: 5000, only used with --http)
  --help, -h             Show this help message
  --version, -v          Show version information
```

### Running Modes

#### MCP Mode (Default)

The default mode communicates via stdin/stdout using the MCP protocol, suitable for integration with MCP-compatible clients.

```bash
# Run in MCP mode
dotnet run

# With specific browser
dotnet run -- --browser firefox
```

#### HTTP Server Mode

The HTTP server mode provides a REST API that can be easily called by AI agents or any HTTP client.

```bash
# Start HTTP server on default port 5000
dotnet run -- --http

# Start on custom port with headed browser
dotnet run -- --http --port 8080 --headed
```

### MCP Client Configuration

Add the server to your MCP client configuration. For example, in Claude Desktop's `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "playwright": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/playwright-dockerless-mcp"]
    }
  }
}
```

Or if you've published the executable:

```json
{
  "mcpServers": {
    "playwright": {
      "command": "/path/to/PlaywrightMcpServer",
      "args": ["--browser", "chromium"]
    }
  }
}
```

## HTTP API Reference

When running in HTTP mode (`--http`), the following endpoints are available:

### Health Check

```http
GET /health
```

Returns server health status.

**Response:**
```json
{
  "status": "healthy",
  "version": "1.0.0"
}
```

### List Tools

```http
GET /tools
```

Returns all available browser automation tools with their schemas.

**Response:**
```json
{
  "tools": [
    {
      "name": "browser_navigate",
      "description": "Navigate to a URL",
      "inputSchema": {
        "type": "object",
        "properties": {
          "url": { "type": "string", "description": "The URL to navigate to" }
        },
        "required": ["url"]
      }
    },
    ...
  ]
}
```

### Execute Tool

```http
POST /tools/{toolName}
Content-Type: application/json
```

Execute a specific tool with the provided arguments.

**Example - Navigate to URL:**
```bash
curl -X POST http://localhost:5000/tools/browser_navigate \
     -H "Content-Type: application/json" \
     -d '{"url": "https://example.com"}'
```

**Response:**
```json
{
  "success": true,
  "url": "https://example.com/",
  "title": "Example Domain",
  "status": 200
}
```

**Example - Take Screenshot:**
```bash
curl -X POST http://localhost:5000/tools/browser_take_screenshot \
     -H "Content-Type: application/json" \
     -d '{"fullPage": true}'
```

**Example - Click Element:**
```bash
curl -X POST http://localhost:5000/tools/browser_click \
     -H "Content-Type: application/json" \
     -d '{"element": "Submit button", "ref": "button[type=\"submit\"]"}'
```

**Example - Get Accessibility Snapshot:**
```bash
curl -X POST http://localhost:5000/tools/browser_snapshot \
     -H "Content-Type: application/json" \
     -d '{}'
```

## Available Tools

The server provides the following browser automation tools:

### Navigation
- **browser_navigate** - Navigate to a URL
- **browser_navigate_back** - Go back to the previous page

### Page Interaction
- **browser_click** - Perform click on a web page element
- **browser_type** - Type text into an editable element
- **browser_fill_form** - Fill multiple form fields at once
- **browser_select_option** - Select an option in a dropdown
- **browser_hover** - Hover over an element
- **browser_drag** - Perform drag and drop between elements
- **browser_press_key** - Press a keyboard key
- **browser_file_upload** - Upload files
- **browser_handle_dialog** - Handle browser dialogs (alert, confirm, prompt)

### Page Information
- **browser_snapshot** - Capture accessibility snapshot of the page (preferred for AI interactions)
- **browser_take_screenshot** - Take a screenshot of the page
- **browser_console_messages** - Get all console messages
- **browser_network_requests** - Get all network requests
- **browser_evaluate** - Evaluate JavaScript on the page

### Tab Management
- **browser_tabs** - List, create, close, or select browser tabs

### Utilities
- **browser_wait_for** - Wait for text, element, or time
- **browser_resize** - Resize the browser viewport
- **browser_close** - Close the current page
- **browser_install** - Install browser binaries

## Example Workflows

### Web Scraping with HTTP API

```bash
# 1. Navigate to the page
curl -X POST http://localhost:5000/tools/browser_navigate \
     -H "Content-Type: application/json" \
     -d '{"url": "https://example.com"}'

# 2. Get accessibility snapshot
curl -X POST http://localhost:5000/tools/browser_snapshot \
     -H "Content-Type: application/json" \
     -d '{}'

# 3. Click a link
curl -X POST http://localhost:5000/tools/browser_click \
     -H "Content-Type: application/json" \
     -d '{"element": "More information link", "ref": "a[href]"}'

# 4. Take a screenshot
curl -X POST http://localhost:5000/tools/browser_take_screenshot \
     -H "Content-Type: application/json" \
     -d '{"filename": "/tmp/screenshot.png"}'
```

### Form Filling

```bash
# Fill a login form
curl -X POST http://localhost:5000/tools/browser_fill_form \
     -H "Content-Type: application/json" \
     -d '{
       "fields": [
         {"name": "username", "type": "textbox", "ref": "#username", "value": "user@example.com"},
         {"name": "password", "type": "textbox", "ref": "#password", "value": "secretpass"},
         {"name": "remember", "type": "checkbox", "ref": "#remember-me", "value": "true"}
       ]
     }'
```

## Example MCP Interactions

### Initialize the Server

```json
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}
```

### List Available Tools

```json
{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}
```

### Navigate to a URL

```json
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"browser_navigate","arguments":{"url":"https://example.com"}}}
```

### Take a Snapshot

```json
{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"browser_snapshot","arguments":{}}}
```

### Click an Element

```json
{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"browser_click","arguments":{"element":"Submit button","ref":"button[type='submit']"}}}
```

## Development

### Building

```bash
dotnet build
```

### Running Tests

```bash
dotnet test
```

### Publishing

To create a self-contained executable:

```bash
dotnet publish -c Release -r linux-x64 --self-contained
dotnet publish -c Release -r win-x64 --self-contained
dotnet publish -c Release -r osx-x64 --self-contained
```

## Architecture

The server is implemented with a clean separation of concerns:

1. **BrowserAutomation** - Core browser automation logic using Playwright
2. **McpServer** - MCP protocol handler for stdin/stdout communication
3. **HttpServer** - REST API server using ASP.NET Core minimal APIs

Both servers share the same `BrowserAutomation` class, ensuring consistent behavior regardless of the communication method.

## License

MIT License

