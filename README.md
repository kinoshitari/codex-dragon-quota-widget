# 傻龙插件

一个面向 Windows Codex、AGY 与豆包桌面端的透明置顶伴生挂件。它参考了 [DeepSeek Balance Whale Widget](https://github.com/MeteorNOX/DeepSeek-Balance-Whale-Widget) 的拖拽、玩偶回弹与按压音效交互，针对三种本地数据源全新实现。

> 本项目采用伴生挂件架构：不修改客户端安装目录，可在屏幕内自由放置。

## 功能

- 鼠标经过挂件不会触发互动或刷新；右键可在“Codex 额度”、“AGY 额度”、“豆包额度”和“互动提示”之间切换左键行为，另有 60 秒自动刷新。
- 选择任一额度模式时，左键点击龙娘临时显示信息框并刷新所选数据源；选择“互动提示”模式时，左键显示互动气泡并在后台刷新当前数据源。
- “每周额度”与“5h 额度”分别读取 `window_minutes=10080` 和 `window_minutes=300` 的额度窗口，显示剩余百分比、重置倒计时和重置时间。
- “长期消耗”模式位于额度与今日 Token 之间，可在设置中切换过去 7 天、过去 30 天或全部本地记录。
- Token 模式可在设置中选择“当日”或“过去 24 小时”，显示输入、输出、缓存命中率；Codex 模式单独列出 Work 与 Codex 的 Token 分流，AGY 模式采用专属隔离统计。
- “本轮”模式统计最近活动的顶层对话及其可追溯子任务（Codex）或最新轨迹（AGY）的输入、输出和缓存命中率。
- 页签顺序为“每周额度 → 5h 额度 → 7/30 天 Token → 今日/24h Token → 本轮对话消耗”，右键模式菜单保持相同顺序。
- 额度信息框平时隐藏；额度模式下左键点击、右键“显示额度信息”/“刷新数据”或 Codex 任务完成时临时打开，并按设置的秒数淡出；面板角标可固定显示并取消自动淡出。
- 设置窗口带滚动条，可调整左键点击行为、50%–180% 大小、信息框持续时间、互动锁定、音量、位置固定和始终置顶。
- 可注册为 Windows 登录后台监测器：Codex 窗口出现时自动显示挂件；启动后独立运行，不随 Codex 最小化或关闭而隐藏。
- 可选择点击关闭后转入系统托盘后台运行；双击托盘图标恢复，托盘菜单可彻底退出。
- 默认可自由拖动，松手后仅在屏幕工作区边缘限制位置，防止挂件显示不全，不再自动吸附到四边。
- 点击或拖动龙娘会播放可调音量的按下/松开音效；可选择“小黄鸭”或“音效 1”两套声音。
- 互动气泡通常随机显示“好模型”或“臭模型”；2% 概率显示扩大的“今日reset”艺术字，并按设置时长固定气泡、锁定互动。
- 会话事件读取：增量跟踪最多 32 个最近活动的本机会话，以持续气泡显示“正在思考”“正在运行命令”“正在修改文件”和可见工作摘要；并行任务完成也可被识别，不展示隐藏推理、用户提示词或工具参数。
- 额度信息框出现时工作状态气泡收回，面板淡出后若任务仍在进行则恢复；互动模式按压或拖动时同样收回，互动气泡结束后恢复。
- 保存左键展示模式、数据模式、缩放、位置及全部窗口行为设置。
- 不读取 `auth.json`，不复制访问令牌，不调用私有登录接口。

## 数据口径

- Codex 额度：从全部可读 Work 与 Codex 会话摘要中取时间最新的一条 `rate_limits`，视为账户级合并额度，不再限于最近 8 个文件。右键手动刷新不会主动向 OpenAI 发送请求。
- AGY 额度：调用本机已登录的 AGY 官方 `/usage` 命令读取 Gemini 模型每周及 5h 额度窗口；多个 Gemini 组采用最受限窗口。刷新失败时只允许短时间显示明确标记的旧缓存，超过 5 分钟后不再显示过期额度。
- 豆包额度：在豆包电脑版已运行且已登录时，通过 Windows UI Automation 临时打开“额度状态”，读取“当前时段”和“近 7 天”的已用比例与重置时间，再自动返回原界面；不读取 Cookie、令牌或账号数据文件。右上角数据源按钮按 `Codex → AGY → 豆包` 循环切换。
- Work/Codex 分类：`originator=codex_work_desktop` 计入 Work；普通 `Codex Desktop` 会话计入 Codex；具备父会话 ID 的子任务继承父会话分类。
- AGY Token 统计：从本地 AGY 会话 SQLite 数据库直接解析 Protobuf 轨迹元数据，按 AGY 专属 surface 独立聚合，不与 Work/Codex 混合。
- 当日输入/输出：按事件时间转换到本地时区后，累计自然日内的输入和输出 Token。
- 过去 24 小时：以当前时间为终点，累计滚动 24 小时窗口内的 Token 事件。
- 过去 7 天：以读取时刻为终点的滚动 7 × 24 小时窗口。
- 过去 30 天：以读取时刻为终点的滚动 30 × 24 小时窗口。
- 总消耗：所有仍存在的本机会话日志/数据库；已删除日志和其它设备的使用量不包含在内。
- 本轮对话：Codex 选择最近活动的顶层用户会话及子任务；AGY 选择最新事件时间对应的轨迹会话。
- 缓存命中率：`cached_input_tokens / input_tokens × 100%`。缓存 Token 是输入 Token 的子集，不重复计入总量。
- Token 总量：`input_tokens + output_tokens`。

本地限制说明：统计仅覆盖本机仍保留的本地会话日志与数据库；其他设备、已删除数据或尚未写入事件的请求不会计入。

历史 Codex 会话摘要按文件长度和修改时间缓存；未变化的历史文件不会在每次 60 秒刷新时重复扫描。AGY 同一轨迹存在多个数据库副本时，最新副本不可读会自动回退到较早的有效副本并显示警告。

## 使用

安装插件后，可以在 Codex 中说“启动傻龙插件”，或运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/start-widget.ps1
```

也可以直接双击插件根目录中的 `Launch-Dragon-Quota-Widget.bat`。

右键挂件可在“Codex 额度”、“AGY 额度”、“豆包额度”和“互动提示”之间切换左键模式，也可显示或固定额度信息、刷新所选数据源、切换额度/Token/本轮模式、调整大小或退出。按住龙娘可自由拖动，松手会回弹并在屏幕边缘限制完整显示；龙娘移到屏幕右半侧时自动镜像。额度面板开关以龙娘右下角为固定锚点，不会带动立绘跳位。

命令行获取用量 JSON：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/get-usage.ps1 -Source Codex
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/get-usage.ps1 -Source Agy
```

点击信息框右上角的 `▯` 可立即隐藏信息框，点击 `⚙` 打开带滚动条的完整设置。纯立绘模式下可右键龙娘打开设置。

设置中的“随 Codex 启动并监测其运行状态”默认启用。它会在当前用户的 Windows 登录启动项中注册稳定插件路径；检测到 Codex 后显示挂件，最小化 Codex 不会隐藏挂件，Codex 真正退出后才隐藏。

停止挂件：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/stop-widget.ps1
```

## 环境

- Windows 10/11
- .NET 8 Windows Desktop Runtime
- Codex 本地会话目录（默认 `%USERPROFILE%\.codex\sessions`，兼容 `CODEX_HOME`）或 AGY 本地会话目录（`%USERPROFILE%\.gemini\antigravity-cli\conversations` 等）

## 开发与构建

```powershell
dotnet restore src/DragonQuotaWidget/DragonQuotaWidget.csproj --configfile NuGet.Config
dotnet publish src/DragonQuotaWidget/DragonQuotaWidget.csproj -c Release --no-restore -o bin/win-x64
```

代码采用 MIT 许可证。角色图片不随 MIT 代码许可证再授权，详见 `ASSET-NOTICE.md`。四个互动 MP3 来自指定的 MIT 项目，来源与许可证见 `THIRD-PARTY-NOTICES.md`。
