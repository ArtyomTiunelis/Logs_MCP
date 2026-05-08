# Logs MCP
# Logs_MCP

A [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) server that lets an AI assistant search application logs across load-balanced Windows servers over UNC file shares.

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
- [Available Tools](#available-tools)
  - [`search_federated_logs`](#search_federated_logs)
  - [`list_app_folders`](#list_app_folders)
  - [`get_log_context`](#get_log_context)
  - [`get_correlation_context`](#get_correlation_context)
  - [`search_recent_errors`](#search_recent_errors)
- [Testing Your Configuration](#testing-your-configuration)

---

## What It Does

`Logs_MCP` exposes MCP tools that an AI assistant can call to:

1. Resolve which servers are mapped to a `server_name` key.
2. Discover candidate app folders before searching.
3. Search `.log` files with content filters and date bounds.
4. Return summary metadata plus paged hits.
5. Expand a hit by `hit_id` into surrounding context.
6. Expand correlation-linked entries into context blocks.
7. Search recent error-like entries without a free-text keyword.

---

## How It Works

```text
AI assistant
  -> calls one of:
     - list_app_folders(server_name, filter?, max_results?)
     - search_federated_logs(server_name, target_app, ...)
     - get_log_context(hit_id, before?, after?)
     - get_correlation_context(server_name, target_app, correlation_id, ...)
     - search_recent_errors(server_name, target_app, ...)

Logs_MCP (stdio MCP server)
  -> resolves AppServerMap[server_name] => [SERVER-A, SERVER-B, ...]
  -> searches \\SERVER\<LogShare>\<target_app*>\**\*.log
  -> filters + paginates
  -> returns summary + hits (with stable `hit_id` values)
```

The mapping and UNC path settings are driven by `appsettings.json`.

---

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8)
- Network access and read permissions for UNC shares (for example `\\SERVER\logfiles$\LogFiles`)
- An MCP-compatible client

---

## Setup

### 1. Clone and build

```powershell
git clone <repo-url>
cd Logs_MCP
dotnet build
```

### 2. Configure server mappings

Edit `appsettings.json`:

```json
{
  "LogSearch": {
    "AppServerMap": {
      "my-app-uat1": ["SERVER-LB01", "SERVER-LB02"],
      "my-app-prod": ["SERVER-PROD-LB01", "SERVER-PROD-LB02"]
    },
    "LogShare": "logfiles$\\LogFiles",
    "MaxLineLength": 500,
    "TruncationSuffix": "...[TRUNCATED FOR CONTEXT]"
  }
}
```

| Field | Description |
|---|---|
| `AppServerMap` | Maps a `server_name` key to one or more Windows hostnames. |
| `LogShare` | UNC segment appended to server name to form `\\SERVER\<LogShare>`. |
| `MaxLineLength` | Maximum characters returned per line before truncation. |
| `TruncationSuffix` | Suffix appended to truncated lines. |

### 3. Register with your MCP client

The server communicates over stdio.

Example (`mcp.json`):

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

---

## Configuration Reference

```jsonc
{
  "LogSearch": {
    "AppServerMap": {
      "<server-name>": ["<server1>", "<server2>"]
    },
    "LogShare": "logfiles$\\LogFiles",
    "MaxLineLength": 500,
    "TruncationSuffix": "...[TRUNCATED FOR CONTEXT]"
  }
}
```

---

## Available Tools

### `search_federated_logs`

Searches log files for a `target_app` across servers mapped to `server_name`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `server_name` | `string` | Yes | Mapping key defined in `AppServerMap`. |
| `target_app` | `string` | Yes | App/folder selector (case-insensitive substring match). |
| `search_keyword` | `string` | No | Free-text keyword. |
| `phrase` | `string` | No | Phrase filter. |
| `log_level` | `string` | No | Structured level filter (`Error`, `Warning`, etc.). |
| `correlation_id` | `string` | No | Correlation ID filter. |
| `exception_type` | `string` | No | Exception type filter. |
| `exact_match` | `bool` | No | Enables stricter normalized matching behavior for `search_keyword`. |
| `recent_errors` | `bool` | No | Restricts matches to error-like entries. |
| `start_date` | `string` | No | `yyyy-MM-dd` (defaults to today). |
| `end_date` | `string` | No | `yyyy-MM-dd` (defaults to today). |
| `max_results` | `int` | No | Max matches collected before paging (default `50`). |
| `page_size` | `int` | No | Matches returned in current page (default `max_results`). |
| `offset` | `int` | No | Zero-based page offset (default `0`). |

At least one content filter is required: `search_keyword`, `phrase`, `log_level`, `correlation_id`, `exception_type`, or `recent_errors=true`.

### `list_app_folders`

Lists discovered top-level app folders for a mapped `server_name`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `server_name` | `string` | Yes | Mapping key defined in `AppServerMap`. |
| `filter` | `string` | No | Optional case-insensitive folder-name substring filter. |
| `max_results` | `int` | No | Max folder names to return (default `50`). |

### `get_log_context`

Returns surrounding lines for a specific search hit.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `hit_id` | `string` | Yes | Stable identifier returned by `search_federated_logs`. |
| `before` | `int` | No | Lines before match (default `10`). |
| `after` | `int` | No | Lines after match (default `10`). |

### `get_correlation_context`

Finds entries for a `correlation_id` and returns surrounding context.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `server_name` | `string` | Yes | Mapping key defined in `AppServerMap`. |
| `target_app` | `string` | Yes | App/folder selector. |
| `correlation_id` | `string` | Yes | Correlation ID to expand. |
| `start_date` | `string` | No | `yyyy-MM-dd` (defaults to today). |
| `end_date` | `string` | No | `yyyy-MM-dd` (defaults to today). |
| `max_hits` | `int` | No | Max hits to expand (default `10`). |
| `before` | `int` | No | Lines before each hit (default `10`). |
| `after` | `int` | No | Lines after each hit (default `10`). |

### `search_recent_errors`

Searches recent error-like entries without requiring `search_keyword`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `server_name` | `string` | Yes | Mapping key defined in `AppServerMap`. |
| `target_app` | `string` | Yes | App/folder selector. |
| `log_level` | `string` | No | Optional log level (defaults to `Error`). |
| `start_date` | `string` | No | `yyyy-MM-dd` (defaults to today). |
| `end_date` | `string` | No | `yyyy-MM-dd` (defaults to today). |
| `max_results` | `int` | No | Max matches collected before paging (default `50`). |
| `page_size` | `int` | No | Matches returned in current page (default `max_results`). |
| `offset` | `int` | No | Zero-based page offset (default `0`). |

---

## Testing Your Configuration

`Logs_MCP.IntegrationTests` validates configuration loading, tool input validation, matching behavior, summaries, and pagination helpers.

Run tests:

```powershell
dotnet test Logs_MCP.IntegrationTests\Logs_MCP.IntegrationTests.csproj
```
