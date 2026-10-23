---
name: dragon-quota-widget
description: Launch, stop, or inspect the local Codex, AGY and Claude companion named 傻龙插件. Use for quota, local token usage, Chinese progress, completion summaries and model speed.
---

# 傻龙插件

This plugin controls a local Windows companion widget for Codex, AGY and Claude. Its activity bubble selects Chinese public summaries/progress, shows a separate tool-status line, and displays the final public completion excerpt. Raw reasoning, encrypted content, prompts and tool arguments are not displayed. Codex and Claude quota panels show the selected source's active model and effort. Codex speed is the latest completed response's client-observed streaming throughput (`近 50 t/s`), not task-average or live per-token speed. It uses response `usage.output_tokens` divided by seconds from the first timed Reasoning/AgentMessage start to token_usage_record completion, excluding pre-output waiting and tool execution. Output includes reasoning and tool arguments once; response/item IDs deduplicate samples and model/turn changes reset timing. Network/response-finalization overhead remains. Missing generation timing (including current Claude transcripts) yields `-- t/s`; transcript block timestamps and session-wide API duration are not generation timings.

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
powershell -NoProfile -ExecutionPolicy Bypass -File "<plugin-root>\scripts\get-usage.ps1" -Source <Codex|Agy|Claude>
```

When reporting values, preserve these definitions:

- Today is determined after converting each event timestamp to the computer's local timezone.
- Last 24 hours is a rolling window ending at the read time.
- Last 7 days is a rolling 7 x 24-hour window ending at the read time.
- Last 30 days is a rolling 30 x 24-hour window ending at the read time.
- All-time usage includes every local session log / conversation database that still exists.
- Work and Codex token usage are calculated separately from session metadata, then combined only for the displayed Codex total.
- AGY token usage is aggregated under its own surface directly from local AGY conversation databases.
- Claude usage is isolated under `Claude`, from local `.claude/projects` JSONL. Message IDs deduplicate blocks. Input includes plain input plus cache creation and cache reads. Raw thinking blocks are ignored; public assistant text drives the Chinese progress and completion display. Ordinary cloud Claude chats without local transcripts are not included.
- Claude quota is read from the desktop app's credential-free `plan-usage-history.json` v2 cache (`u.fh` and `u.sd`). Missing data is unavailable; the app may not provide quota for Free/API accounts. No reset time is inferred. Values older than 5 minutes are stale and older than 30 minutes expire. `-Source Claude` returns an object with `Usage` and `Activity`.
- Current conversation includes the most recently active top-level user session and child sessions with a traceable parent thread id for Codex, or the latest conversation trajectory for AGY.
- Total tokens are input plus output.
- Cached input is a subset of input and is not added to total tokens again.
- Cache hit rate is cached input divided by input.
- Codex quota is the newest locally recorded `rate_limits` snapshot across Work and Codex modes. AGY quota is retrieved by the locally installed and signed-in AGY CLI through its official `/usage` command, which may use the network. Missing buckets are never inferred.
- The 5-hour quota uses the rate-limit window whose `window_minutes` is `300`.
- The weekly quota uses the rate-limit window whose `window_minutes` is `10080`.

State the local-only limitation when it matters: deleted logs, databases from other devices, and cloud-only activity are not included.
