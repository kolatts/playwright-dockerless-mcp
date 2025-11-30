# Playwright MCP Server for .NET

A lightweight Model Context Protocol (MCP) server for browser automation using Microsoft Playwright, built with .NET 8 C#. This server allows AI agents to interact with web browsers locally without requiring npm, Docker, or other external dependencies.

## Features

- **Pure .NET 8 Implementation**: No npm or Docker required
- **Full Playwright Browser Support**: Chromium, Firefox, and WebKit
- **MCP Protocol Compliant**: Works with any MCP-compatible client
- **Comprehensive Browser Tools**: 21 browser automation tools for complete web interaction
- **Command Line Configuration**: Easy startup with command-line arguments

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

1. Using the `browser_install` tool via MCP
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
  --help, -h             Show this help message
  --version, -v          Show version information
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

The server is implemented as a single console application that:
1. Reads JSON-RPC messages from stdin
2. Processes MCP protocol requests
3. Executes Playwright browser automation commands
4. Returns JSON-RPC responses to stdout

The implementation follows Microsoft C# coding conventions and is designed to be lightweight and easy to deploy.

## License

MIT License

