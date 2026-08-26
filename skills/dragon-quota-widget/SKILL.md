---
name: dragon-quota-widget
description: Launch, stop, or inspect the local Codex companion named 傻龙插件. Use when the user asks to start or close the widget, view the combined Codex quota, or inspect Work/Codex token usage for today, the last 24 hours, the last 7 days, the last 30 days, all local history, or the current conversation.
---

# 傻龙插件

This plugin controls a local Windows companion widget. It does not modify the Codex application or read authentication files. Its activity bubble maps observable local lifecycle events to short status labels and never exposes hidden reasoning, prompts, or tool inputs.

## Start the widget

Only launch it when the user asks to start, open, or show the widget:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "<plugin-root>\scripts\start-widget.ps1"
```

Report whether a new process was started or an existing instance was already running.

## Stop the widget

Only stop it when the user asks:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "<plugin-root>\scripts\stop-widget.ps1"
```

## Inspect usage without opening the UI

Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "<plugin-root>\scripts\get-usage.ps1"
```

When reporting values, preserve these definitions:

- Today is determined after converting each event timestamp to the computer's local timezone.
- Last 24 hours is a rolling window ending at the read time.
- Last 7 days is a rolling 7 x 24-hour window ending at the read time.
- Last 30 days is a rolling 30 x 24-hour window ending at the read time.
- All-time usage includes every local session log that still exists.
- Work and Codex token usage are calculated separately from session metadata, then combined only for the displayed total.
- Current conversation includes the most recently active top-level user session and child sessions with a traceable parent thread id.
- Total tokens are input plus output.
- Cached input is a subset of input and is not added to total tokens again.
- Cache hit rate is cached input divided by input.
- Quota is the newest locally recorded `rate_limits` snapshot across Work and Codex modes and can lag until Codex writes another token event.
- The 5-hour quota uses the rate-limit window whose `window_minutes` is `300`.
- The weekly quota uses the rate-limit window whose `window_minutes` is `10080`.

State the local-only limitation when it matters: deleted logs and activity from other devices are not included.
