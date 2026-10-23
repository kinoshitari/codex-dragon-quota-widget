# 傻龙插件：当前版本摘要

> 更新日期：2026-10-23
> 当前清单版本：`0.4.3+codex.20261023`
> 本次更新基线提交：`d354b092219f23d49b796045be7492e9a1c21884`
> 运行平台：Windows 10/11、.NET 8 Windows Desktop Runtime

## 1. 项目定位

本次更新：修复气泡偶尔与龙娘脱节的问题。长摘要切回短文字时，WPF 延迟更新测量值，旧代码会用旧高度撑大窗口；已复现约 71.49 DIP 的偏移。气泡改为与立绘共用底部锚点，并在计算窗口高度前更新内容布局，确保小角始终靠近龙角。原有额度、Token、TPS、进度与互动逻辑保持不变。

傻龙插件是一个透明、置顶、可自由拖动的 Codex/AGY/Claude 桌面伴生挂件。它以龙娘立绘为主体，展示账户额度、本地 Token 消耗和可观察的任务状态。

插件不会读取 `auth.json`、复制登录凭据或调用私有登录接口，也不会展示隐藏推理、用户提示词或工具参数。

## 2. 当前主要功能

### 额度与 Token

- 支持 Codex、AGY、Claude 三套数据源，可通过右键菜单切换左键行为。旧豆包设置迁移至 Claude。
- 左键模式包括“Codex 额度”“AGY 额度”“Claude 额度”和“互动提示”。
- 额度模式下左键刷新并临时显示额度信息；默认每 60 秒自动刷新一次。
- 信息页签顺序固定为：

  1. 每周额度
  2. 5h 额度
  3. 过去 7 天、过去 30 天或全部本地 Token
  4. 当日或过去 24 小时 Token
  5. 本轮对话 Token

- Token 明细包含输入、输出和缓存命中率；总量按 `输入 + 输出` 计算，缓存输入是输入 Token 的子集，不重复计入总量。
- Codex 数据中分别统计 Work 与普通 Codex 会话；AGY 使用独立统计，不与 Codex/Work 混合。
- 每周和 5h 额度显示剩余百分比，有重置时间时显示倒计时。Claude 客户端缓存不提供重置时间，显示 `--`。

### 信息框与工作状态

- 额度信息框平时隐藏，在额度模式左键点击或通过右键命令显示时弹出。
- 信息框按设置的持续时间自动淡出；角标或菜单可以将其固定，固定后不自动淡出。
- 信息框出现时，可观察工作状态气泡会暂时收回；信息框消失且任务仍在进行时，状态气泡恢复。
- 工作状态气泡选取中文 `summary_text` 或公开工作进度，下方独立显示工具工作提示；英文思考片段不会覆盖中文进度。任务完成显示最后公开答复的中文摘录（最多 160 字），按信息框持续时间收回。固定额度框临时让位后恢复；关闭工作气泡时沿用完成弹出额度框的行为。
- 气泡收近到头部斜上方，小角与立绘共用底部锚点，固定在内侧龙角附近；正文向上展开，长短内容切换同步调整窗口高度，保持眼睛、头发和龙角可见。左右对称，出现和收回时保留立绘位置。
- Codex / Claude 模型和推理强度位于各自额度框内；`近 50 t/s` 表示最近一次响应的客户端流式速度，每次响应完成后更新，工具运行时保留最近样本。计算使用该响应 `usage.output_tokens / (token_usage_record 时间 - 首个 Reasoning/AgentMessage.started_at_ms)`，含推理、文本与工具参数输出，排除响应前等待和工具执行；不重复相加推理 Token，不使用任务或会话累计值。响应 ID、生成片段 ID 去重，切换轮次、模型时重置。缺少生成起点的响应和当前 Claude 日志显示 `-- t/s`。该值含流式传输和响应结束记账延迟，不是服务端纯解码速度或实时逐字速度。
- 工作状态增量跟踪最多 32 个最近活动会话，并行任务完成不会再被单一最新日志遮蔽。

### 龙娘互动

- 可在屏幕工作区内自由拖动，只有接近屏幕边缘时才限制位置，以防立绘显示不完整。
- 拖动使用手掌抓取光标，松手后有回弹效果。
- 龙娘位于屏幕左半侧时面向右方，位于右半侧时面向左方；支持多显示器工作区。
- 互动模式会随机显示“好模型”或“臭模型”，并有 2% 概率显示放大的“今日reset”艺术字。
- “今日reset”出现后会按设置时间固定气泡并锁定互动，默认 3 秒。
- 支持两套点击音效、音效开关和音量调节。

### 窗口与生命周期

- 主窗口透明置顶，当前版本不在 Windows 任务栏中显示。
- 可设置 50%–180% 窗口缩放、位置固定和始终置顶。
- 可选择关闭窗口时转入系统托盘；双击托盘图标恢复，托盘菜单可彻底退出。
- 可注册为 Windows 登录后台监测器，在检测到 Codex 窗口后显示挂件。
- 挂件启动后独立运行，不随 Codex 最小化而最小化；已删除贴靠或移动到 Codex 窗口的功能。

## 3. 数据来源与统计口径

| 数据 | 来源 | 当前口径 |
|---|---|---|
| Codex 每周/5h 额度 | 本机 Codex 会话中的最新 `rate_limits` 事件 | 分别采用 `window_minutes=10080` 和 `window_minutes=300`；视为账户级额度快照 |
| Codex Token | 本机 `.codex/sessions` 会话 JSONL | 按事件时间聚合，并依据会话元数据区分 Work、Codex 和子任务归属 |
| AGY 每周/5h 额度 | 本机已登录 AGY 的官方 `/usage` 命令 | 读取命令返回的实际额度窗口；没有快照时不臆测数值 |
| AGY Token | 本机 AGY 会话 SQLite 数据库中的轨迹元数据 | 使用 AGY 专属 surface 聚合，与 Codex/Work 隔离 |
| Claude 额度 | `%APPDATA%\Claude\plan-usage-history.json` v2 | 最新样本 `u.fh` / `u.sd`；缺失不推算，5 分钟后标为旧缓存、30 分钟后失效 |
| Claude Token / 活动 | `%USERPROFILE%\.claude\projects` JSONL | 按消息 ID 去重；输入包含普通输入、缓存创建与缓存读取；公开中文文本和工具事件形成状态气泡 |
| 本轮对话 | 最近活动的顶层会话及可追溯子任务，或 AGY 最新轨迹 | 仅覆盖本机仍可读取的会话事件 |

时间范围定义：

- “当日”按本地时区的自然日统计。
- “过去 24 小时”“过去 7 天”和“过去 30 天”均为以当前读取时刻为终点的滚动窗口。
- “全部”是本机当前仍存在的全部可读取会话记录。

## 4. 为什么额度消耗与 Token 数量可能不成比例

额度百分比与本地原始 Token 不是同一计量单位，不能按固定比例换算。额度可能综合模型等级、上下文长度、推理负载、缓存、工具调用、执行速度以及 Codex/Work/云端活动进行加权，而插件的 Token 页只统计本机仍保留且已经写入日志或数据库的输入与输出 Token。

因此以下情况会造成“额度已经消耗较多，但 Token 只有几百 K”：

- 在 Web/云端 Work、其他设备或其他本机数据目录中产生的消耗，没有进入当前本地会话日志。
- 会话日志已被删除、轮转，或请求尚未写入完整 Token 事件。
- 高成本模型、长上下文、推理或工具工作对额度的影响大于原始 Token 数字所表现的比例。
- 额度快照与 Token 统计使用不同的时间窗口或更新时间。
- AGY `/usage` 暂时失败时，界面可能保留上一次成功的额度快照，而本地 Token 仍继续按现存记录聚合。

结论：额度页面适合判断“账户剩余容量”，Token 页面适合查看“本机可追溯的原始使用量”，两者应并列展示，但不应强行互相推算。

## 5. 已知边界

- 本地 Token 统计不保证包含 Web/云端 Work、其他设备、已删除日志或尚未落盘的请求。
- 右键刷新 Codex 数据只重新读取本地事件，不会主动向 OpenAI 发起一次额度查询。
- AGY 额度查询可能联网，并依赖本机 AGY 登录状态和 `/usage` 命令可用性。
- AGY 刷新失败时，最多使用 5 分钟且明确标记为旧缓存的额度；超过期限后显示无可用额度，不再无限沿用旧值。
- 本机 Claude Free 账号未提供额度百分比，因此无法验证非空的真实额度显示；缓存字段已通过客户端代码核对并用固定样本验证。普通云端 Claude 对话未写入 Claude Code 本地目录时，不提供其 Token / TPS。
- 可观察状态气泡不是模型的隐藏思考链；它只呈现客户端已公开写入本地会话的状态。
- 角色图片不随 MIT 代码许可证再授权；音效来源和许可证另见项目中的声明文件。

## 6. 设置项概览

- 左键展示模式：Codex 额度、AGY 额度、Claude 额度、互动提示
- Token 时间范围：当日、过去 24 小时
- 长期统计范围：过去 7 天、过去 30 天、全部
- 信息框：显示时长、固定显示
- 互动：reset 锁定时间、工作状态气泡
- 外观：缩放、位置固定、始终置顶
- 音效：启用状态、声音套装、音量
- 生命周期：随 Codex 启动监测、关闭时最小化到托盘

设置保存在：`%LOCALAPPDATA%\CodexDragonQuotaWidget\settings.json`。

## 7. 启动与停止

启动：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/start-widget.ps1
```

也可以双击仓库根目录的 `Launch-Dragon-Quota-Widget.bat`。

停止：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/stop-widget.ps1
```

命令行读取用量 JSON：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/get-usage.ps1 -Source Codex
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/get-usage.ps1 -Source Agy
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/get-usage.ps1 -Source Claude
```

## 8. 主要代码位置

- 窗口布局与任务栏行为：[MainWindow.xaml](src/DragonQuotaWidget/MainWindow.xaml)
- 交互、刷新与状态气泡：[MainWindow.xaml.cs](src/DragonQuotaWidget/MainWindow.xaml.cs)
- Codex 数据读取：[CodexUsageReader.cs](src/DragonQuotaWidget/CodexUsageReader.cs)
- AGY 数据读取：[AntigravityUsageReader.cs](src/DragonQuotaWidget/AntigravityUsageReader.cs)
- Claude 数据与活动：[ClaudeUsageReader.cs](src/DragonQuotaWidget/ClaudeUsageReader.cs)
- 设置模型与迁移：[WidgetSettings.cs](src/DragonQuotaWidget/WidgetSettings.cs)
- 启动脚本：[start-widget.ps1](scripts/start-widget.ps1)
- 测试入口：[Program.cs](tests/DragonQuotaWidget.Tests/Program.cs)

## 9. 当前修订与验收状态

- `0.3.1` 修复 AGY 额度陈旧缓存、数据库回退、Codex 全会话额度发现、历史摘要缓存、并行任务监测、关闭行为、设置损坏备份和安装失败回滚。
- 自动化测试共 30 项通过，包含 60 秒工具等待不影响生成 TPS、重复响应/片段去重、模型及并行会话隔离、缺失/无效计时、初始尾读恢复和日志截断；原有统计与镜像检查保持通过。额度框的新速度标签已检查实际渲染。
- 本次界面专项测试通过：长摘要切短摘要、左右镜像、50%/100%/180% 缩放、普通互动与 reset 气泡的高度及锚点；确认更新内容不移动角色。已检查短摘要左右两侧、长摘要及完成气泡的实际渲染。
- 真实 Codex 日志独立复算：某次响应输出 672 Token，从生成起点到响应完成为 14.929 秒，计算为 45.013 t/s，与挂件的 45.013 t/s 一致。该样本的旧任务均速为 39.115 t/s；新值不再受整个任务已经运行 669.337 秒的影响。
- 安装包采用 Windows IExpress 封装，当前未进行代码签名；首次下载或运行时可能出现 Windows SmartScreen 提示。
- Git 源码更新与 GitHub Release 附件是独立交付；旧 Release 不会自动包含本次更新。
