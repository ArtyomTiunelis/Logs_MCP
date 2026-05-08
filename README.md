# Logs MCP

A [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) server that lets an AI assistant (e.g. GitHub Copilot, Claude) search application log files across load-balanced Windows servers over UNC file shares — directly from a chat conversation.

---

## Table of Contents

- [What It Does](#what-it-does)
- [How It Works](#how-it-works)
- [Prerequisites](#prerequisites)
- [Setup](#setup)
  - [1. Clone and build](#1-clone-and-build)
  - [2. Configure server mappings](#2-configure-server-mappings)
  - [3. Register with your MCP client](#3-register-with-your-mcp-client)
- [Configuration Reference](#configuration-reference)
- [Available Tool](#available-tool)
- [Testing Your Configuration](#testing-your-configuration)

---

## What It Does

`Logs_MCP` exposes a single MCP tool — `search_federated_logs` — that an AI assistant can call to:

1. Look up which Windows servers host a given application's logs.
2. Fan out concurrent searches across all load-balanced nodes for that app.
3. Filter log files by date range.
4. Return matching lines, prefixed with the server name and source file, so the AI can reason about errors, correlation IDs, or exception IDs without any manual log diving.

---

## How It Works

```
AI assistant
    ?
    ?  calls search_federated_logs(server_name, target_app, search_keyword, start_date, end_date)
    ?
Logs_MCP (MCP stdio server)
    ?
    ?  looks up AppServerMap["server_name"] ? ["SERVER-A", "SERVER-B", ...]
    ?
    ???? Task: \\SERVER-A\logfiles$\LogFiles\<target_app*>\**\*.log
    ???? Task: \\SERVER-B\logfiles$\LogFiles\<target_app*>\**\*.log
    ???? ...
    ?
    ?  collects matching lines (up to max_results)
    ?  formats: "[SERVER-A] filename.log | matched line text"
    ?
AI assistant receives results and summarises / triages
```

The server ? app mapping and UNC share path are all driven by `appsettings.json` — no recompilation needed when infrastructure changes.

---

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8)
- Network access to the UNC log shares from the machine running the server (i.e. `\\SERVER\logfiles$\LogFiles` must be reachable and your Windows credentials must have read access)
- An MCP-compatible client (e.g. VS Code with GitHub Copilot, Claude Desktop)

---

## Setup

### 1. Clone and build

```powershell
git clone <repo-url>
cd Logs_MCP
dotnet build
```

### 2. Configure server mappings

Open `appsettings.json` and edit the `LogSearch` section:

```json
{
  "LogSearch": {
    "AppServerMap": {
      "my-app-uat1": [ "SERVER-LB01", "SERVER-LB02" ],
      "my-app-prod": [ "SERVER-PROD-LB01", "SERVER-PROD-LB02" ]
    },
    "LogShare": "logfiles$\\LogFiles",
    "MaxLineLength": 500,
    "TruncationSuffix": "...[TRUNCATED FOR CONTEXT]"
  }
}
```

| Field | Description |
|---|---|
| `AppServerMap` | Maps a server CNAME/environment key to one or more Windows server hostnames. The key is the value you pass as `server_name` when calling the tool. Keys are case-insensitive. |
| `LogShare` | The UNC share segment appended to every server name to form `\\SERVER\<LogShare>`. |
| `MaxLineLength` | Lines longer than this are truncated before being returned to the AI to protect the context window. |
| `TruncationSuffix` | Text appended to truncated lines so the AI knows the line was cut. |

### 3. Register with your MCP client

The server communicates over **stdio**. Point your MCP client at the built executable.

**VS Code (`settings.json` / `mcp.json`):**

```json
{
  "mcp": {
    "servers": {
      "logs-mcp": {
        "type": "stdio",
        "command": "dotnet",
        "args": [
          "run",
          "--project",
          "C:\\path\\to\\Logs_MCP\\Logs_MCP.csproj",
          "--no-build"
        ]
      }
    }
  }
}
```

Or point directly at the compiled binary for faster startup:

```json
{
  "mcp": {
    "servers": {
      "logs-mcp": {
        "type": "stdio",
        "command": "C:\\path\\to\\Logs_MCP\\bin\\Release\\net8.0\\Logs_MCP.exe",
        "args": []
      }
    }
  }
}
```

---

## Configuration Reference

Full `appsettings.json` schema:

```jsonc
{
  "LogSearch": {
    // Required. Maps app/CNAME names to their server hostnames.
    "AppServerMap": {
      "<app-name>": [ "<server1>", "<server2>" ]
    },

    // UNC share path segment appended to each server.
    // Full path becomes: \\<server>\<LogShare>\...
    // Default: "logfiles$\\LogFiles"
    "LogShare": "logfiles$\\LogFiles",

    // Maximum characters per log line returned to the AI.
    // Default: 500
    "MaxLineLength": 500,

    // Appended to lines that exceed MaxLineLength.
    // Default: "...[TRUNCATED FOR CONTEXT]"
    "TruncationSuffix": "...[TRUNCATED FOR CONTEXT]"
  }
}
```

---

## Available Tool

### `search_federated_logs`

Searches log files for a given application folder across the servers mapped to a specific server CNAME/environment.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `server_name` | `string` | ? | Server CNAME/environment key, exactly as defined in `AppServerMap` (e.g. `finweb-uat1` or `finintsvc-dev3`). Case-insensitive. |
| `target_app` | `string` | ? | Application or folder name used to narrow the physical log folder on the mapped server(s) (e.g. `potsplitter` or `Next.Fin.PotSplitter.Web`). Case-insensitive substring match. |
| `search_keyword` | `string` | ? | Text, Exception ID, or Correlation ID to search for inside log files. |
| `start_date` | `string` | ? | ISO 8601 date (`yyyy-MM-dd`). Defaults to today. |
| `end_date` | `string` | ? | ISO 8601 date (`yyyy-MM-dd`). Defaults to today. |
| `max_results` | `int` | ? | Maximum matching lines to return. Defaults to `50`. |

**Example prompt:**
> "Search the logs on `finweb-uat1` for app `potsplitter` and correlation ID `abc-123` between 2025-06-01 and 2025-06-03"

---

## Testing Your Configuration

The `Logs_MCP.IntegrationTests` project contains integration tests that validate:

- `appsettings.json` is loaded correctly
- expected mappings are present
- UNC paths are constructed correctly from configuration
- tool input validation behaves as expected
- folder discovery narrows results using `target_app`

### Run the tests

```powershell
cd Logs_MCP
dotnet test Logs_MCP.IntegrationTests\Logs_MCP.IntegrationTests.csproj
```

### How the tests work

Each entry in `AppServerMap` produces one test case per server. For example, if `finapihub-uat1` maps to `["END-FUWS59-LB01", "END-FUWS59-LB02"]`, two test cases are generated:

| App | Server | UNC Path checked |
|---|---|---|
| `finapihub-uat1` | `END-FUWS59-LB01` | `\\END-FUWS59-LB01\logfiles$\LogFiles` |
| `finapihub-uat1` | `END-FUWS59-LB02` | `\\END-FUWS59-LB02\logfiles$\LogFiles` |

A test **passes** if `Directory.Exists(uncPath)` returns `true`. A **failure** means the share is unreachable from the machine running the tests — either the server name is wrong, the share doesn't exist, or there's a network/permissions issue.

### When to run the tests

- After **adding a new app** to `AppServerMap`
- After **renaming or decommissioning** a server
- After changing the MCP tool contract or search behavior
- As part of a **CI/CD pipeline** to validate configuration and tool behavior
