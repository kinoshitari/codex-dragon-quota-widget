# 傻龙插件

一个面向 Windows Codex 桌面端的透明置顶伴生挂件。它参考了 [DeepSeek Balance Whale Widget](https://github.com/MeteorNOX/DeepSeek-Balance-Whale-Widget) 的拖拽、玩偶回弹与按压音效交互，但数据源与宿主实现均针对 Codex 重新设计。

> Codex 当前插件接口不支持向主窗口注入任意 DOM。本项目采用“Codex 个人插件 + 透明 WPF 伴生窗口”：不修改 Codex 安装目录，可自由放置，也可选择贴靠 Codex 窗口。

## 功能

- 鼠标经过挂件不会触发互动或刷新；右键可在“互动模式”和“额度信息模式”之间切换左键行为，左键仍会刷新数据；另有 60 秒自动刷新。
- “5h 额度”与“每周额度”分别读取 `window_minutes=300` 和 `window_minutes=10080` 的账户级额度窗口，显示剩余百分比、重置倒计时和重置时间。
- “长期消耗”模式位于额度与今日 Token 之间，可在设置中切换过去 7 天、过去 30 天或全部本地记录。
- Token 模式可在设置中选择“当日”或“过去 24 小时”，显示输入、输出、缓存命中率，并单独列出 Work 与 Codex 的 Token 总量。
- “本轮”模式统计最近活动的顶层对话及其可追溯子任务的输入、输出和缓存命中率。
- 页签顺序为“每周额度 → 5h 额度 → 7/30 天 Token → 今日/24h Token → 本轮对话消耗”，右键模式菜单保持相同顺序。
- 额度信息框平时隐藏；额度信息模式下左键点击、右键“显示额度信息”/“刷新数据”或 Codex 任务完成时临时打开，并按设置的秒数淡出。
- 设置窗口带滚动条，可调整 50%–180% 大小、信息框持续时间、互动锁定、音量、位置固定、始终置顶和 Codex 窗口贴靠。
- 可注册为 Windows 登录后台监测器：Codex 窗口出现时自动显示挂件，Codex 最小化时挂件保持显示，Codex 真正退出后自动隐藏。
- 可选择点击关闭后转入系统托盘后台运行；双击托盘图标恢复，托盘菜单可彻底退出。
- 默认可自由拖动，松手后仅在屏幕工作区边缘限制位置，防止挂件显示不全，不再自动吸附到四边。
- 点击或拖动龙娘会播放可调音量的按下/松开音效；可选择“小黄鸭”或“音效 1”两套声音。
- 互动气泡通常随机显示“好模型”或“臭模型”；2% 概率显示扩大的“今日reset”艺术字，并按设置时长固定气泡、锁定互动。
- 两种左键模式都会读取本机 Codex 会话中的可观察事件，以持续气泡显示“正在思考”“正在运行命令”“正在修改文件”和可见 commentary 摘要；不会展示隐藏推理、用户提示词或工具参数。
- 额度信息框出现时工作状态气泡收回，面板淡出后若任务仍在进行则恢复；互动模式按压或拖动时同样收回，互动气泡结束后恢复。
- 保存左键展示模式、数据模式、缩放、位置及全部窗口行为设置。
- 不读取 `auth.json`，不复制访问令牌，不调用私有登录接口。

## 数据口径

- 额度：取 Work 与 Codex 本地事件中时间最新的一条 `rate_limits`，视为账户级合并额度。右键手动刷新不会主动向 OpenAI 发送请求。
- Work/Codex 分类：`originator=codex_work_desktop` 计入 Work；普通 `Codex Desktop` 会话计入 Codex；具备父会话 ID 的子任务继承父会话分类。
- 当日输入/输出：按事件时间转换到本地时区后，累计自然日内的 `last_token_usage.input_tokens` 和 `output_tokens`。
- 过去 24 小时：以当前时间为终点，累计滚动 24 小时窗口内的 Token 事件。
- 过去 7 天：以读取时刻为终点的滚动 7 × 24 小时窗口。
- 过去 30 天：以读取时刻为终点的滚动 30 × 24 小时窗口。
- 总消耗：所有仍存在的本机会话日志；已删除日志和其它设备的使用量不包含在内。
- 本轮对话：选择最近活动的顶层用户会话，并累计该会话及可通过 `parent_thread_id` 追溯到它的子任务。
- 缓存命中率：`cached_input_tokens / input_tokens × 100%`。缓存 Token 是输入 Token 的子集，不重复计入总量。
- Token 总量：`input_tokens + output_tokens`；Work 与 Codex 分开累计后再显示合计。

因此，今日统计只覆盖本机仍保留的 Codex 会话日志；其他设备、已删除日志或尚未写入事件的请求不会计入。

## 使用

安装插件后，可以在 Codex 中说“启动傻龙插件”，或运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/start-widget.ps1
```

也可以直接双击插件根目录中的 `Launch-Dragon-Quota-Widget.bat`。

右键挂件可切换互动/额度信息两种左键模式，也可显示额度信息、刷新、切换额度/Token/本轮模式、贴靠 Codex、调整大小或退出。按住龙娘可自由拖动，松手会回弹并在屏幕边缘限制完整显示。额度面板开关以龙娘右下角为固定锚点，不会带动立绘跳位。

点击信息框右上角的 `▯` 可立即隐藏信息框，点击 `⚙` 打开带滚动条的完整设置。纯立绘模式下可右键龙娘打开设置。

设置中的“随 Codex 启动并监测其运行状态”默认启用。它会在当前用户的 Windows 登录启动项中注册稳定插件路径；检测到 Codex 后显示挂件，最小化 Codex 不会隐藏挂件，Codex 真正退出后才隐藏。

停止挂件：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/stop-widget.ps1
```

## 环境

- Windows 10/11
- .NET 8 Windows Desktop Runtime
- Codex 本地会话目录（默认 `%USERPROFILE%\.codex\sessions`，也兼容 `CODEX_HOME`）

## 开发与构建

```powershell
dotnet restore src/DragonQuotaWidget/DragonQuotaWidget.csproj --configfile NuGet.Config
dotnet publish src/DragonQuotaWidget/DragonQuotaWidget.csproj -c Release --no-restore -o bin/win-x64
```

代码采用 MIT 许可证。角色图片不随 MIT 代码许可证再授权，详见 `ASSET-NOTICE.md`。四个互动 MP3 来自指定的 MIT 项目，来源与许可证见 `THIRD-PARTY-NOTICES.md`。
