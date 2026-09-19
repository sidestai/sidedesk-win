# SideDesk 产品与开发者文档

> 一份面向「普通用户 / 组件作者 / 二次开发者 / 运维」的完整说明。
> 覆盖产品形态、全部组件、变量系统、对外 API（本机 HTTP / MCP / WebSocket / 后端 REST / 插件桥）以及开发约定。
> 文档基于当前四个代码库实测整理：`sidedesk-win-exe`（Windows 客户端）、`sidedesk-admin-backend`（后端）、`sidedesk-admin-frontend`（管理台）、`chrome-extension-selector`（浏览器扩展）。

---

## 目录

1. [产品总览](#1-产品总览)
2. [核心概念](#2-核心概念)
3. [组件大全（24 种）](#3-组件大全24-种)
4. [横切功能](#4-横切功能)
5. [数据看板与变量系统](#5-数据看板与变量系统)
6. [开发者集成 API](#6-开发者集成-api)
7. [插件系统](#7-插件系统)
8. [后端架构](#8-后端架构)
9. [管理前端（Console）](#9-管理前端console)
10. [Chrome 扩展](#10-chrome-扩展)
11. [构建、版本与发布](#11-构建版本与发布)
12. [二次开发约定](#12-二次开发约定)
13. [端口与地址速查表](#13-端口与地址速查表)

---

## 1. 产品总览

**SideDesk（微屏）** 是一款常驻桌面侧边的效率面板工具：在主屏两侧停靠可滚动的「微屏窗口」，把高频信息、待办、数据看板、快捷入口等以「组件」形式集中呈现，并支持团队协作、云端同步、外部程序 / AI 写入数据。

产品由四个可独立部署的部分组成：

| 交付物 | 代号目录 | 技术栈 | 职责 |
|---|---|---|---|
| **Windows 客户端** | `sidedesk-win-exe` | WPF / .NET 10（`net10.0-windows10.0.19041`）| 桌面侧边栏主体，渲染组件、本地存储、云同步、内置 HTTP/WS/MCP 服务 |
| **后端服务** | `sidedesk-admin-backend` | NestJS + TypeORM + MySQL + Redis | 账号 / 团队 / 分享 / 同步 / 变量 / 数字员工 / 插件市场等服务端能力 |
| **管理前端** | `sidedesk-admin-frontend` | Vue 3 + Vite + TypeScript | 运营 / 企业管理台（Console），部署于 `/console` 子路径 |
| **浏览器扩展** | `chrome-extension-selector` | Chrome MV3 | 网页元素 CSS Selector 拾取器，配合数据看板抓取网页数据 |

### 1.1 关键地址（随构建参数切换）

- 中文区站点：`https://sidedesk.bigjing.com`，API 基址 `https://api.sidedesk.bigjing.com/console`
- 国际区站点：`https://sidedesk.desktxp.com`，API 基址 `https://api.sidedesk.desktxp.com/console`
- 域名由 `SideDesk.csproj` 的 `ApiRegion` 编译符号（`API_REGION_EN`）在编译期选择，运行时统一从 `Constants.URLs.ServerBaseUrl` 读取。

### 1.2 进程内网络服务（客户端）

客户端在主进程内启动三个本机监听服务（详见 [§6](#6-开发者集成-api)）：

| 端口 | 协议 | 用途 |
|---|---|---|
| `19191` | WebSocket | 与浏览器扩展通信（CDP 抓取协调、消息广播） |
| `19192` | HTTP / SSE | MCP（Model Context Protocol）服务，供 AI 助手调用 |
| `19195` | HTTP | 本机业务 API：变量读写、通知推送、AI Chat 代理、状态查询 |

---

## 2. 核心概念

理解以下术语有助于阅读后续章节：

- **微屏窗口（Window）**：一个停靠区域（左 / 右 / 双侧），内部是可垂直滚动的面板列表，可置顶部分面板。配置持久化在 `windows.json` 与本地 SQLite。
- **面板 / 组件（PanelItem / PanelType）**：窗口内的一个功能单元（待办、便签、数据看板…）。`PanelType` 枚举共 24 种，见 [§3](#3-组件大全24-种)。
- **分组（Group）**：一种容器型组件，可嵌套子面板（`ChildPanels`），用于折叠归类。
- **作用域（Scope）**：数据与配置归属的边界。`corpId` 有值 = **团队**作用域（团队内共享、跨成员）；为空 = **个人**作用域（按 `userId` 隔离）。变量、数据项、分享均遵循此模型。
- **服务端 ID（ServerId）**：本地面板首次同步后由后端回写的稳定 ID，用于云端幂等更新；`null` 表示尚未同步。
- **数据变量（Variable）**：数据看板与外部程序共享的键值单元，`varKey` 在作用域内唯一；可由网页轮询产生，也可由外部 API 手动写入（手动值优先于轮询值）。
- **变量集（VariableSet）**：把多个变量按 `SUM/AVG/MAX/MIN/COUNT` 聚合出一个本地派生变量，支持通配符匹配（如 `qoder.count*`）。

---

## 3. 组件大全（24 种）

`PanelType` 定义了所有组件类型。下表按功能归类，列出用途、可右键配置项与是否出现在「添加模块」菜单中。

> 图例：★ = 用户可通过「添加模块」菜单直接新增；— = 由系统 / 其他入口生成或暂未开放添加入口。

### 3.1 信息与效率类

| 组件 | 枚举值 | 说明 | 主要能力 |
|---|---|---|---|
| 快捷方式 ★ | `QuickGroup` | 文件 / 程序 / URL / 云盘 快捷入口分组 | 可关联 `GroupId`，支持拖放添加、图标抓取（Favicon） |
| 便签 ★ | `StickyNotes` | 轻量文本便签，per-panel 存储 | 新建 / 编辑 / 删除，支持全局便签与模块内便签；MCP 可增删查 |
| 今日待办 ★ | `TodoList` | 当日任务清单（`TodoItem`） | 勾选完成、排序；跨日归档 |
| 剪贴板收藏 ★ | `Clipboard` | 手动收藏的剪贴板条目（`ClipboardSavedItem`） | 永久保存常用文本 / 图片 / 文件 |
| 剪贴板历史 | `ClipboardHistory` | 自动记录的剪贴板历史（最多约 100 条） | 文本 / 图片 / 文件；MCP 可搜索 |
| 信号灯 ★ | `SignalLight` | 多色状态灯指示 | 可配置纵向 / 横向排列、间距、对齐（`SignalLightVertical/Spacing/Alignment`） |

### 3.2 时间与专注类

| 组件 | 枚举值 | 说明 | 主要能力 |
|---|---|---|---|
| 计时器 ★ | `Timer` | 正计时 / 多计时器（`TimerItem`） | 起停、重置 |
| 倒计时器 ★ | `Countdown` | 到期倒计时（`CountdownItem`） | 目标时间提醒 |
| 番茄闹钟 ★ | `Pomodoro` | 番茄工作法 | 专注 / 短休 / 长休；MCP 可查状态、启停、统计轮数 |
| 打卡 ★ | `CheckIn` | 多个打卡按钮，点一次 +1，并镜像写入「每日活跃」 | 预设 6 项 + 自定义（文字 / 图标 / 颜色）；右键「列数」1~4 列、「样式」4 种（见下） |

**打卡组件右键菜单**：
- **列数**：1 / 2 / 3 / 4 列（`CheckInColumns`，默认 2），实时重排 UniformGrid 网格。
- **样式**（`CheckInStyle`，默认 `IconTextVertical`）：
  | 选项 | 值 | 效果 |
  |---|---|---|
  | 仅文字 | `TextOnly` | 只显示文字 + 计数徽标 |
  | 仅图标 | `IconOnly` | 只显示 emoji 图标 + 计数徽标 |
  | 图标文字竖置 | `IconTextVertical` | 图标在上、文字在下（默认） |
  | 图标文字横置 | `IconTextHorizontal` | 图标在左、文字在右 |
  > 计数徽标（右上角数字）在所有样式下均保留，满足「每点一次 +1、按钮显示数字」核心诉求。

### 3.3 数据与可视化类

| 组件 | 枚举值 | 说明 | 主要能力 |
|---|---|---|---|
| 数据看板 ★ | `DataDashboard` | 网页数据抓取 + 变量展示核心组件（`DataDashboardItem`） | 选择器抓取、定时轮询、手动变量、列数（`DashboardColumns` 1~4）；详见 [§5](#5-数据看板与变量系统) |
| 系统信息 ★ | `SystemMonitor` | CPU / 内存 / 网络等系统监视 | 实时指标（键鼠计数只在底部状态栏展示，不进卡片） |
| 使用统计 | `UsageStats` | 应用 / 窗口使用统计（添加入口暂隐藏） | 由 `WindowUsageService` 供数 |
| 屏幕时长 ★ | `ScreenTime` | 当日屏幕使用时长统计 | 右键样式菜单；`ScreenTimeHourlyService` 逐小时记录 |
| 每日活跃 ★ | `DailyActivity` | 按日聚合的活跃计数（打卡、其它操作镜像写入） | `DailyActivityService.AddAsync(corpId, type, date, delta)` |
| 日历 ★ | `Calendar` | 月历 / 日期展示 | — |

### 3.4 内容与媒体类

| 组件 | 枚举值 | 说明 | 主要能力 |
|---|---|---|---|
| 文件区 ★ | `FileArea` | 文件夹 / 文件快捷区（`FileAreaItem`） | 布局 `LargeIcons/SmallIcons/List`（`FileAreaLayout`）、列数 1~4（`FileAreaColumns`）、拖放 |
| 图片轮播 ★ | `ImageCarousel` | 图片轮播展示 | 自动切换 |
| AI 对话 | `AIChat` | 内嵌 AI 对话面板 | 走本机 `/ai-chat` 代理，密钥不出本机 |
| 百度搜索 | `WebBaidu` | 内嵌网页组件（WebView2） | `WebViewModePanel` 承载 |
| 外部插件 ★ | `Plugin` | 第三方插件挂载的自定义组件 | 见 [§7](#7-插件系统) |

### 3.5 协作与组织类

| 组件 | 枚举值 | 说明 | 主要能力 |
|---|---|---|---|
| 分组 ★ | `Group` | 容器组件，折叠归类其它面板 | `ChildPanels`、`IsExpanded`、自定义背景色 |
| 数字员工 | `DigitalEmployee` | 展示数字员工（`DigitalEmployeeIds` 引用全局员工库） | 每排数量 1~5（`DigitalEmployeeColumns`）；`DigitalEmployeeApiService` |
| 好友 | `Friend` | 好友关系展示（`FriendName`/`FriendAvatarPath`） | `FriendService` |

### 3.6 组件通用外观能力

所有组件均支持以下通用配置（由 `ModuleControl` / `WrapWidget` 提供）：

- **自定义背景色**（`BGColor`，null = 跟随主题）。
- **隐藏外框**（`HideFrame`：去边框 / 标题 / 背景 / 内边距，融入桌面）。
- **内容区高度**（`PanelHeight`，null = 自适应）。
- **置顶**（`IsPinned`：渲染在滚动区上方的置顶区）。
- **右键菜单 / 标题栏 ▾ 菜单**：每个组件可注入自定义菜单项（如「列数」「样式」）。
- **添加到团队库 / 分享**：可将组件共享给他人或团队（见 [§4.3](#43-团队库与分享)）。

---

## 4. 横切功能

### 4.1 登录与账号

- **OAuth 登录**（`OAuthLoginService`）：客户端 `LoginWindow` 走浏览器授权，取得 JWT（`AppSettings.AccountToken`）后调用后端。
- 账号体系由后端 `oauth` / `system` / `team` 模块管理；个人密钥与团队密钥共用账号身份。

### 4.2 本地存储与云同步

- **本地数据库**（`LocalDbService`，SQLite）：窗口配置、行级数据（`local_item_row`）等落本机，保证离线可用与秒开。
  - 行类型 `row_type`（如 `check_in` 打卡行）按组件维度组织。
- **云同步**（`CloudSyncService` + `RowChangeTracker`）：
  - `RowChangeTracker.GetRowType / IsRowSyncable` 判定可同步行；
  - 同步走「快照 → 推送 → 回写 ServerId」闭环；后端 `sidebar/sync`、`sidebar/item-row` 模块承接。
  - 模块级设置存于 `item_data`（打卡的 `CheckInColumns`、`CheckInStyle` 等写这里），列表数据（`CheckInItems`）不入 `item_data`。

### 4.3 团队库与分享

- **团队**（`TeamService`）：一人可属多个团队，无需切换团队——任意桌面都可混放来自不同团队的共享模块。
- **分享**（后端 `sidebar/share`、`team` 模块 + 前端 `share` 视图）：
  - 选择若干元素，共享给他人或团队；
  - 对方用**共享码**添加进来；
  - 控件可设为所有人可操作或**只读**；
  - 支持窗口级分享（`window_share`）、分享导入（`share_import`）、分享权限（`share_permissions`）。

### 4.4 主题与外观

- **主题管理**（`ThemeManager` + `Themes/`、`resources/themes/`）：Dark / Light / System 三态，可主题色；MCP 提供 `set_theme`。
- 主题词典与插件页配色联动（见插件规范 `getConfig`）。

### 4.5 多语言（i18n）

- **13 种语言**：`zh-CN / en / de / es / fr / it / ja / ko / pl / pt-BR / ru / tr / zh-TW`，运行时由 `Framework/Lang.cs` 加载，改翻译无需重编译。
- 调用约定、键命名、新增流程与回退链详见 [§12.1](#121-多语言i18n-开发约定)。

### 4.6 快捷键与自动化

- **全局热键**（`GlobalHotkeyService`）+ **键盘钩子**（`KeyboardHookService`）：唤起、显隐、语音（`MainWindow.VoiceHotkey.cs`）等。
- **虚拟桌面**（`VirtualDesktopService`）、**焦点监视**（`FocusWatchService`）。

### 4.7 通知

- **通知服务**（`NotificationService`）：应用内通知栏渲染；
- **本机 HTTP** `POST /notify` 可由外部脚本推送一条通知（见 [§6.1](#61-本机-http-api-端口-19195)）；
- **团队通知**（`TeamNotificationService`）、**通知同步**（`NotificationSyncService`）、**移动端语音**（`MobileVoiceService`）。

#### 团队成员操作通知

成员改动团队卡片 / 微屏时，服务端向**其余成员**（不含操作者本人）落库并推送 `team_item_notice`；
客户端由 `App.HandleTeamNotice` → `NotificationSyncService.BuildTeamNoticeText` 按 `reason` 生成文案，同时进气泡与通知中心。

| `reason` | 含义 | 附带字段 |
|---|---|---|
| `created` | 新增团队卡片 / 新建团队微屏 | — |
| `renamed` | 卡片或微屏改名 | `oldTitle` |
| `updated` | 卡片内容（`itemData` / `itemType`）被修改 | — |
| `relocated` | 卡片被移到其他微屏或分组 | `barTitle` |
| `restored` | 已删除的卡片 / 微屏被恢复 | — |
| `joined` | 个人卡片被加入团队 | — |
| `rows_changed` | 卡片内行数据增改删 | `added` / `updated` / `deleted` |
| `deleted` / `moved` | 内容被移除（作者删除、改归属其他团队） | — |

关键约定：

- **按「真实变更」而非「每次同步」判定**。`sync()` 是全量结构上报，未变更的卡片每次也会被重写一遍；
  `SyncService` 逐项与库内旧值比对（`stableStringify` 消除客户端对象键序差异），只在真变了才发通知，
  否则一次同步会给全团队刷出与卡片数等量的通知。
- **两类文案语义不同**。`deleted` / `moved` 有配套的即时清理指令（`team_item_removed` / `team_bar_removed`），
  文案可承诺「已从您的微屏移除」；成员操作类只客观陈述「谁做了什么」，本地数据由 `share_item_meta_updated` /
  `bar_meta_updated` 刷新或等下次对账，文案不承诺本地已变化。
- **改名必须同时广播 meta 更新**。否则其他成员收到「已改名」通知却仍看到旧名；更要紧的是成员客户端持有旧名时，
  其下一次全量上报会把标题改回去，两个成员之间来回抢标题。
- **数据看板卡片只报增/删，不报纯修改**。`DataDashboardItem.Value` 是客户端 `DataPollingService` 定时抓取的值（默认 60 秒一轮），
  它随行数据一起上报；抓取结果变化不是「成员做了操作」，照发会让其他成员被持续刷屏。
  因此 `ItemRowService` 对 `itemType === 'DataDashboard'` 只在 `added`/`deleted` 非零时通知，且计数不带 `updated`，
  避免文案把轮询造成的变更说成是该成员改的。其他卡片类型（待办、剪贴板、打卡、快捷方式等）仍每次都发。
- **不覆盖卡片在微屏内的位置排序**。`sidedesk_item` 无排序列、`pull()` 也不排序，卡片在微屏内的先后顺序纯属客户端本地状态，
  服务端无从感知；微屏之间的顺序存 `user_bar`，是每人独立布局而非团队共享操作。

### 4.8 云盘与文件

- **百度网盘**（`BaiduPanService` + `BaiduPanWindow`）、**通用云盘**（`CloudDriveService`）、
- **对象存储**：`S3StorageService`、`WebDavStorageService`；
- 上传加密（`SyncEncryption`）。

### 4.9 数字员工 / MCP 工具市场

- **数字员工**（`DigitalEmployeeApiService` + 后端 `digital-employee` 模块 + 前端视图）。
- **MCP 工具注册**（`McpToolRegistryService` + 后端 `mcp-tool` 模块 + 前端 `mcp-tools` 视图 + `db/mcp-tool-preset.json` 预设）。
- **插件市场**（后端 `market`、`plugin-market` 迁移 + 前端 `plugin-market`/`app-market`/`web-market` 视图）。

### 4.10 自动更新

- **Velopack**（`VelopackUpdateService`）：从 `VelopackUpdateUrl` 拉取更新包（签名 + 时间戳），版本号规则见 [§11](#11-构建版本与发布)。
- **开机自启**（`AutoStartService`）。

---

## 5. 数据看板与变量系统

数据看板（`DataDashboard`）是 SideDesk 的「数据中枢」：它把**网页数据抓取**、**变量存储**、**外部写入**、**多端实时同步**统一在一个模型里。

### 5.1 数据流总览

```
网页数据源 ──(CDP 抓取 / 选择器)──► DataPollingService ──► 变量(varKey=值)
                                                         │
外部程序 ──(本机 HTTP /variable/set)──┐                  ├─► DataDashboard 组件渲染
AI 助手 ──(MCP 工具)──────────────────┤──► SetVariableValue（手动值优先）
其它设备 ──(后端 REST value/set)──────┘                  │
                                                         └─► WebSocket 推送 data_variable_value_changed
```

### 5.2 数据项（DataItem / DataDashboardItem）

一个数据项描述「从哪里、用什么方式、多久抓一次」：

| 字段 | 说明 |
|---|---|
| `label` | 数据项名称 |
| `targetUrl` | 目标网址 |
| `selector` | 选择器内容（CSS / XPath / 正则 / JS 脚本） |
| `selectorType` | `xpath` / `css` / `regex` / `js`（空 = 旧版自动识别） |
| `refreshInterval` | 刷新间隔（秒） |
| `browserId` / `profileId` | 目标浏览器与 Profile（CDP 连接定位） |
| `templateId` | 关联的数据模板 ID（见数据模板市场） |
| `itemLocalId` | 客户端本地 ID |

### 5.3 抓取引擎（DataPollingService）

`Services/DataPollingService.cs`（约 1900 行）是中央数据源：

- 统一管理所有看板项的**定时抓取**，面板组件只负责展示、不自行发请求。
- 通过 **CDP（Chrome DevTools Protocol）** 连接真实浏览器或自启动隐藏浏览器（端口自 `9300` 起自动分配）。
- **连接池** 以 `{browserId}_{profileId}` 分组复用，避免重复建连弹「是否允许远程调试」。
- **身份去重**：同一数据源分布到多个窗口时只轮询一次，值更新后广播给所有重复实例并各自按 Id 刷新。
- **防重叠**：上一次抓取未完成则跳过本轮。
- 支持 **app 模式**（独立无地址栏窗口）与隐藏浏览器；退出时统一清理自启动进程与打开的 target。

### 5.4 变量（Variable）

- `varKey` 作用域内唯一（个人或团队），`varValue` 为当前值。
- **手动值优先**：外部 API 写入后标记 `Manual`，轮询不再覆盖，直到 `/variable/clear` 解除。
- 客户端服务：`DataVariableService`（对接后端）+ `VariableSetService`（聚合）。

### 5.5 变量集（VariableSet）

把多个变量聚合出一个派生变量（本地计算）：

| 字段 | 说明 |
|---|---|
| `varKey` | 变量集名（即派生变量名） |
| `op` | 聚合逻辑：`SUM` / `AVG` / `MAX` / `MIN` / `COUNT` |
| `sourceKeys` | 显式选择的源变量名列表 |
| `pattern` | 通配符匹配（如 `qoder.count*`；空 = 不启用） |

> 变量与变量集**共用命名空间**：`/sidebar/data-variable/check` 会同时校验两张表，均无同名才算可用。

---

## 6. 开发者集成 API

SideDesk 对外提供 **4 类** 集成面：本机 HTTP、MCP、WebSocket（扩展）、后端 REST。外部程序 / AI / 网页 / 其它设备都能以合适的方式读写变量或驱动组件。

### 6.1 本机 HTTP API（端口 19195）

- 实现：`Services/HttpServerService.cs`（`System.Net.HttpListener`，无额外依赖）。
- 监听：`http://127.0.0.1:19195/`，**默认开启**，端口可在「设置 → 工具」修改。
- 面向**本机脚本 / 工具**，已开启 CORS（供网页版 AI Chat 前端跨域）。

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/` 或 `/status` | 服务状态（name / port / running / time） |
| POST | `/notify` | 推送通知到通知栏。Body：`{"title":"..","body":".."}`（非 JSON 时整体作为正文） |
| GET | `/variable/list` | 全部变量当前值快照：`[{key,value,label,manual}]`（含轮询项 / 变量集 / 手动值） |
| GET | `/variable/teams` | 当前登录用户可写团队列表 `[{corpId,name}]`（未登录返回 401，供外部获取 corpId） |
| GET | `/variable/set?key=NAME&value=VAL[&corpId=ZZ]` | 设置单个变量（手动值优先） |
| POST | `/variable/set` | 单个 `{key,value}` 或批量 `{items:[{key,value}]}`（兼容 `varKey`/`varValue`），顶层或单条可带 `corpId` |
| GET | `/variable/clear?key=NAME[&corpId=ZZ]` | 清除手动值，恢复轮询 / 聚合自动取值 |
| GET | `/ai-chat/config` | 本地 AI 配置（API Key 脱敏）+ 支持的模型列表 |
| * | `/ai-chat/**` | 网页版 AI 对话本地代理（密钥不出本机） |

**写语义与作用域**（`/variable/set`）：
- **已登录**：无论是否带 `corpId` 都会同步服务器。不带 → 个人作用域（后端 WS 推给本人其它设备）；带 `corpId`（团队加密 ID，可先 `GET /variable/teams` 查询）→ 校验属于本人团队后提交，WS 下发更新团队其它设备。
- **未登录**：只写本机手动值，响应 `forwarded=false`。
- 响应 `results[].found`（本机是否已有同名变量）、`forwarded`（是否成功同步后端）、失败时附 `forwardError`。

**示例**：

```bash
# 设置个人变量
curl -X POST http://127.0.0.1:19195/variable/set \
     -H "Content-Type: application/json" \
     -d '{"key":"qcoder.count","value":42}'

# 批量写入某团队变量（先查 corpId）
curl http://127.0.0.1:19195/variable/teams
curl -X POST http://127.0.0.1:19195/variable/set \
     -H "Content-Type: application/json" \
     -d '{"corpId":"<团队加密ID>","items":[{"key":"build.pass","value":"1"},{"key":"build.fail","value":"0"}]}'

# 推送通知
curl -X POST http://127.0.0.1:19195/notify \
     -H "Content-Type: application/json" \
     -d '{"title":"构建完成","body":"SideDesk v1.0.x 已打包"}'
```

### 6.2 MCP 服务（端口 19192）

- 实现：`Services/McpServerService.cs`，通过 **HTTP / SSE** 传输暴露 SideDesk 核心功能，供 AI 助手（Claude / 兼容 MCP 的客户端）调用。
- 端点：`http://127.0.0.1:19192/`（`_mcpApp.RunHttpAsync(port: 19192)`）。

**已注册工具（21 个）**：

| 分类 | 工具 | 说明 |
|---|---|---|
| 设置 / 状态 | `get_settings` | 当前设置概览（主题、布局、宽度…） |
| | `set_theme` | 切换主题（Dark/Light/System） |
| | `get_status` | 运行状态（MCP / WS / 窗口数量…） |
| | `set_layout` | 主显示器停靠布局（Left/Right/Both/None） |
| 窗口 | `list_windows` | 列出所有微屏窗口配置 |
| WebSocket | `list_ws_clients` | 列出已连接的浏览器扩展客户端 |
| | `broadcast_ws` | 向所有 WS 客户端广播消息 |
| 便签 | `list_notes` / `search_notes` / `create_note` / `delete_note` | 便签增删查（`delete_note` 两步确认：预览 → `confirm=true`） |
| 剪贴板 | `list_clipboard_history` / `search_clipboard_history` | 历史（最多 100 条，含文本/图片/文件） |
| | `list_clipboard_favorites` / `search_clipboard_favorites` | 收藏 |
| 番茄钟 | `get_pomodoro_status` / `start_pomodoro` / `stop_pomodoro` / `start_short_break` / `start_long_break` / `get_pomodoro_count` | 番茄工作法控制与统计 |

> AI 助手可通过自然语言驱动这些工具，例如「给 SideDesk 新建一条便签：买牛奶」「切到深色主题」「列出所有窗口」。工具实现集中在 `Services/AIToolService.cs`。

### 6.3 浏览器扩展 WebSocket（端口 19191）

- 实现：`Services/WebSocketServerService.cs`，默认 `127.0.0.1:19191`；本地调试 WS 回退直连 `127.0.0.1:19190`。
- 用途：与 [§10](#10-chrome-扩展) 的浏览器扩展通信，配合数据看板抓取；亦可被 MCP 的 `broadcast_ws` / `list_ws_clients` 驱动。

### 6.4 后端 REST API + API Key

面向**服务端 / 第三方机器调用**（不依赖本机 SideDesk 运行）。核心为数据看板变量接口，配合 API Key 认证。实现位置：`src/module/sidebar/data-variable/`、`src/common/guards/auth.guard.ts`、`src/module/sidebar/api-key/`。

#### 6.4.1 API Key 认证

- **获取密钥**（以下接口走 JWT 登录态，**不能**用 API Key 调用）：

  | 接口 | 方法 | 说明 |
  |---|---|---|
  | `/sidebar/api-key/create` | POST | 创建密钥。完整 Key 仅创建时返回一次；可绑定个人或团队、可设过期时间 |
  | `/sidebar/api-key/list` | GET | 列出我的密钥（仅显示前缀 `skd_xxxx`，附团队名与 corpCode） |
  | `/sidebar/api-key/delete` | POST | 删除密钥，立即失效 |

  密钥格式：`skd_` 前缀 + 32 位随机字符（base58 风格）。

- **携带方式**（优先级从高到低）：
  1. 请求头（推荐）：`X-API-Key: skd_xxxxxxxx...`
  2. GET 查询参数（回退，无法自定义 Header 时用）：`?apiKey=skd_xxxxxxxx...`
  > query 方式会进入网关 / access log / 浏览器历史，安全性低于 header，仅建议服务端机器调用；公网优先 `X-API-Key`。

- **认证与鉴权规则**：
  - 流程：格式校验（`skd_`）→ 查库匹配哈希 → 检查状态与过期 → 校验 scope 路径；任一失败返回 `401 API Key 无效、已过期或无权访问该接口`。
  - **scope 限制**：v1 固定为 `data-variable`，仅允许 `/sidebar/data-variable/**`；Key 不等价完整登录身份。
  - 成功后挂载 `req.user = { userId, apiKeyCorpId }`：`apiKeyCorpId` 为密钥绑定团队（个人密钥为 null），业务据此默认作用于团队范围。
  - 库中仅存 SHA256 哈希；`last_used_at` 按密钥维度 60 秒节流更新。

#### 6.4.2 变量接口清单（`/sidebar/data-variable`）

| 端点 | 方法 | 鉴权 | 说明 |
|---|---|---|---|
| `/items?corpId=` | GET | JWT / API Key | 列出作用域内全部**数据项**配置（白名单输出，corpId 返回加密串） |
| `/list?corpId=` | GET | JWT / API Key | 列出作用域内全部**变量**（`varKey/varValue/label/itemLocalId/corpId`） |
| `/sets?corpId=` | GET | JWT / API Key | 列出全部**变量集**（`varKey/op/sourceKeys/pattern/corpId`） |
| `/set/delete` | POST | JWT / API Key | 按「作用域 + 变量集名」软删除变量集 |
| `/value/set` | POST | JWT / API Key | **设置变量值**（外部程序主入口；不存在则新建并归属该作用域，WS 推送在线客户端立即生效） |
| `/check?varKey=&corpId=` | GET | JWT / API Key | 变量名在作用域内是否可用（同时校验变量表与变量集表） |

`/value/set` 请求体（`VariableValueSetDto`）：

| 字段 | 类型 | 必需 | 说明 |
|---|---|---|---|
| `key` | string | 是 | 变量名 |
| `value` | string \| number | 否 | 变量值；空 / 不传 = 清除（服务端置 null） |
| `corpId` | string \| number | 否 | 团队加密 ID（空 = 个人） |

服务端逻辑（`setValue`）：
- `corpId` 有值时校验调用者为该团队成员，否则 `400 您不是该团队成员，无法写入团队变量`；
- 存在则更新 `varValue`，不存在则以「调用者 + 作用域」**新建**并归属该作用域（带团队的 API Key 首次写入即在团队下建变量）；
- 以 **API Key 绑定的团队优先**（`req.user.apiKeyCorpId ?? dto.corpId`）；
- 更新后向作用域内**在线用户**经 WS 推送 `{event:'data_variable_value_changed', data:{key,value}}`（个人推本人，团队推全体成员），返回 `{key,value,pushed}`（`pushed`=实际推送的在线人数）。

**示例**：

```bash
# 用 API Key 写团队变量（不存在则自动在团队下新建，并推送全体成员设备）
curl -X POST https://api.sidedesk.bigjing.com/console/api/sidebar/data-variable/value/set \
     -H "Content-Type: application/json" \
     -H "X-API-Key: skd_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" \
     -d '{"key":"deploy.status","value":"success"}'

# 列出作用域内变量
curl -H "X-API-Key: skd_xxx..." \
     "https://api.sidedesk.bigjing.com/console/api/sidebar/data-variable/list"
```

> 本机 HTTP `/variable/set` 与后端 `/value/set` 的关系：本机接口在**已登录**时会用当前 JWT 转发到 `/value/set`（`ForwardVariableToBackendAsync`），从而跨设备同步；未登录仅写本机。二者字段名不同（本机 `key/value`，后端 `key/value/corpId`），语义一致。

---

## 7. 插件系统

规范详见 [`docs/html-plugin-spec.md`](file:///f:/_WORK/_SIDEDESK/sidedesk-win-exe/docs/html-plugin-spec.md)。SideDesk 支持两种插件模式，由 `plugin.json` 的 `mode` 区分：

| 模式 | `mode` | 形态 | 运行环境 |
|---|---|---|---|
| 原生模式 | `"native"`（缺省） | .NET DLL（实现 `ISideDeskPlugin`） | 宿主进程内（可回收 `AssemblyLoadContext` 热加载） |
| HTML 组件模式 | `"html"` | 纯静态 HTML/CSS/JS | WebView2 沙箱（每插件独立虚拟主机域） |

### 7.1 设计原则（对照 Chrome Extension 模型）

1. **manifest 声明**：`plugin.json` ↔ `manifest.json`，安装 / 启用 / 版本 / 入口全部声明式。
2. **受控运行环境**：HTML 插件运行在专属虚拟主机 `https://sd-plugin-<id>.local/`，与其它插件、宿主页面**相互隔离**（独立 origin / 存储）。
3. **最小能力暴露**：插件只能通过 `window.sideDesk` 桥调用宿主白名单方法（当前：`getConfig` / `setConfig` / `openUrl`），未注册方法结构化拒绝。
4. **消息桥通信**：版本化 JSON 信封（postMessage），宿主内部状态（账号、凭据、其它插件数据）对插件不可见。
5. **复用生命周期**：HTML 插件经 `HtmlPluginAdapter` 适配为 `ISideDeskPlugin`，安装 / 市场 / 挂载 / 启停 / 卸载复用 DLL 链路。

### 7.2 包结构

```
<插件根>/
├── plugin.json        # 必需：manifest
├── module.html        # 必需（html 模式）：模块显示页
├── settings.html      # 可选：设置页（有则在「设置-插件」出现「设置」入口）
└── assets/            # 可选：静态资源
```

- zip 单层或带一个根文件夹均可，安装器 `FindPluginRoot` 自动定位。
- **纯静态约束**：html 模式禁止包含任何可执行文件 / DLL。

### 7.3 Manifest 关键字段（`plugin.json`）

| 字段 | 类型 | 必需 | 说明 |
|---|---|---|---|
| `id` | string | 是 | 唯一标识（目录名 / 虚拟主机名 / 私有存储文件名），建议小写字母 / 数字 / 连字符 |
| `name` | string | 是 | 显示名称 |
| `version` | string | 是 | 插件版本 |
| `mode` | string | 否 | `"native"`（默认）或 `"html"` |
| `moduleHtml` | string | html 必需 | 模块页 HTML（相对插件目录），加载时强制校验存在 |
| `settingsHtml` | string | 否 | 设置页 HTML，存在时出现「插件设置」按钮 |
| `permissions` | string[] | 否 | 声明式权限（当前仅声明、未来兼容） |
| `apiVersion` | int | 否 | 依赖宿主 API 版本，默认 `1`；大于宿主支持版本则拒绝加载 |
| `icon` / `description` / `author` / `minHostVersion` / `enabled` | — | 否 | 常规元信息 |

> 原生模式另有 `entry` / `pluginClass` / `xaml` 字段（html 模式不使用）。相关实现见 `Services/Plugins/`（`PluginConfig` / `HtmlPluginHost` / `HtmlPluginAdapter` / `PluginLoaderService` / `PluginMarketBridge`）、`Windows/PluginSettingsWindow`、示例 `plugins/HelloWorldHtml/`。

---

## 8. 后端架构

技术栈：**NestJS + TypeORM + MySQL + Redis**，pnpm 管理，Docker 化部署（`Dockerfile` / `docker-compose-stg.yaml` / `-prod.yaml`）。入口 `src/main.ts`，聚合 `src/app.module.ts`。

### 8.1 顶层模块（`src/module/`）

| 模块 | 职责 |
|---|---|
| `main` | 全局 / 根装配 |
| `axios` | HTTP 客户端封装 |
| `redis` | 缓存 / 会话 |
| `oauth` | 第三方登录授权 |
| `system` | 系统 / 用户 / 权限基础 |
| `team` | 团队与成员（corp） |
| `friend` | 好友关系 |
| `corp`（team 内） | 企业 / 团队作用域 |
| `sidebar` | **SideDesk 客户端核心后端**（见下） |
| `digital-employee` | 数字员工 |
| `mcp-tool` | MCP 工具注册表 |
| `market` | 插件 / 应用市场 |
| `plugin-editor` | 插件在线编辑 |
| `cloud-drive` | 云盘对接 |
| `notification` | 通知 |
| `websocket` | 服务端 WS（向客户端推送 `data_variable_value_changed` 等事件） |
| `upload` | 文件上传 |
| `monitor` | 监控 |
| `operation` | 运营 / 埋点 |
| `client-install` | 客户端安装 / 系统安装信息 |
| `acctree` | 账号树 / 组织结构 |

### 8.2 sidebar 子模块（`src/module/sidebar/`）

| 子模块 | 职责 |
|---|---|
| `desktop` | 桌面 / 窗口配置 |
| `bar` | 侧边栏栏位 |
| `web-bar` | 网页栏位 |
| `group` | 分组 |
| `item` | 组件项（面板）主表 |
| `item-row` | 行级数据（`check_in` 等同步行；`ITEM_TYPE_ROW_TYPE` 映射组件 → 行类型） |
| `row-data` | 行数据附表 |
| `data-variable` | **变量系统**（变量 / 数据项 / 变量集），见 [§6.4](#64-后端-rest-api--api-key) |
| `data-template` | 数据看板模板（市场） |
| `daily-activity` | 每日活跃 |
| `screen-time` | 屏幕时长（逐小时） |
| `share` | 分享（元素 / 窗口 / 导入 / 权限） |
| `sync` | 云同步主通道（快照 / 拉取 / 回写） |
| `api-key` | API Key 管理 |
| `ai-token` | AI Token / LLM Key |
| `resource-pos` | 资源位 |
| `setting` | 用户设置 |

### 8.3 数据迁移

`db/` 下以 `migration_*.sql` + `run-migration-*.cjs` 组织（如 `migration_data_variable`、`migration_variable_set`、`migration_item_row`、`migration_daily_activity`、`migration_screen_time_hourly`、`migration_api_key`、`migration_share_*` 等），并有清理脚本（`cleanup-*.cjs`）与备份目录。

### 8.4 统一响应与工具

- 响应封装：`ResultData.ok(...)` / `ResultData.fail(code,msg)`（`src/common/utils/result`）。
- 作用域解析：`parseCorpId(corpId)` 兼容客户端传入的**团队加密串**；`encrypt2(id, true)` 输出加密 corpId。
- 鉴权：全局 `JwtAuthGuard`，内含 API Key 分支（`const apiKey = req.get('X-API-Key') || req.query.apiKey;`）。

---

## 9. 管理前端（Console）

技术栈：**Vue 3 + Vite + TypeScript**（含 Pinia、Vue Router、vue-i18n、Element Plus 等），部署于 `/console` 子路径。

主要视图（`src/views/`）：

| 视图 | 功能 |
|---|---|
| `auth` | 登录 / 注册 / 鉴权 |
| `dashboard` | 总览仪表盘 |
| `system` | 系统管理（用户 / 角色 / 权限） |
| `corp` | 企业 / 团队管理 |
| `acctree` | 账号树 |
| `resource` | 资源位管理 |
| `share` | 分享管理 |
| `data-template-market` | 数据模板市场 |
| `plugin-market` / `plugin-editor` | 插件市场 / 在线编辑 |
| `app-market` / `web-market` | 应用 / 网页市场 |
| `digital-employee` | 数字员工管理 |
| `mcp-tools` | MCP 工具管理 |
| `ai-chat` | AI 对话管理 |
| `web-bar` | 网页栏位管理 |
| `monitor` | 监控 |
| `phone-voice` | 移动端语音 |
| `privacy-policy` | 隐私政策 |
| `index` / `outside` / `result` / `exception` | 框架页 / 兜底页 |

API 封装见 `src/api/`（`auth`、`corp`、`share`、`system-manage`、`data-template`、`digital-employee`、`mcp`、`monitor`、`profile`、`web-bar` 等）。构建工具链含 husky / commitlint / eslint / prettier / stylelint。

---

## 10. Chrome 扩展

**CSS Selector Picker**（`chrome-extension-selector`，Manifest V3）。

- **核心功能**：点击页面元素自动获取 CSS Selector、实时高亮悬停元素、一键复制、可配置生成策略。
- **选项**：优先使用 ID / 使用 Class / 显示标签名 / 自动处理重复元素 `:nth-child`。
- **快捷键**：`ESC` 退出选择模式。
- **权限**：仅 `activeTab`、`tabs`。
- **与 SideDesk 的配合**：数据看板项（`selector` + `selectorType`）可用本扩展在目标网页上拾取 CSS 选择器，再填入数据项，交由 `DataPollingService`（CDP）定时抓取对应元素文本 → 更新变量 → 面板实时刷新。典型用法：抓取某个后台页面的「在线人数 / 待办数 / 构建状态」并常驻侧边栏。

文件结构：`manifest.json` / `popup.html` / `popup.js`（弹窗逻辑）/ `content.js` / `content.css`（页面脚本与高亮样式）/ `icons/`。

---

## 11. 构建、版本与发布

### 11.1 版本号规则

- 客户端版本由 `SideDesk.csproj` **按构建时间自动生成**：
  - `FileVersion = 1.0.<距 2025-01-01 天数>.<HHmm>`
  - 打包 `packVersion` 取前三段（SemVer，粒度到天）。
- 读取方式：
  ```powershell
  $exe = "...\bin\Release\net10.0-windows10.0.19041\SideDesk.exe"
  $vi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
  $packVer = "$($vi.FileMajorPart).$($vi.FileMinorPart).$($vi.FileBuildPart)"
  ```

### 11.2 编译

```powershell
# 在 sidedesk-win-exe 目录
dotnet build SideDesk.csproj -c Debug -v minimal -nologo
# 发布
dotnet publish SideDesk.csproj -c Release
```

### 11.3 打包发布（Velopack）

```powershell
vpk pack --packId com.desktxp.sidedesk `
  --packDir ...\bin\Release\net10.0-windows10.0.19041 `
  --mainExe SideDesk.exe `
  --outputDir ...\bin\updates `
  --signParams "/sha1 <thumbprint> /tr http://timestamp.sectigo.com?td=sha256 /td sha256 /fd sha256" `
  --shortcuts None `
  --exclude ".*\.pdb$|.*/logs/.*|.*\.xml$" `
  --packVersion 1.0.8
```

- 产物上传至 `VelopackUpdateUrl`（中文区 `cdn.bigjing.com/updates/sidedesk-cn`；国际区 `sidedesk.desktxp.com/updates`）。
- 客户端 `VelopackUpdateService` 检查并应用更新（签名 + 时间戳校验）。

---

## 12. 二次开发约定

### 12.1 多语言（i18n）开发约定

> 完整规范见 [`docs/用户手册-开发者文档.md`](file:///f:/_WORK/_SIDEDESK/sidedesk-win-exe/docs/用户手册-开发者文档.md)。

**基本原则**：
- 所有新增**用户可见文案**必须走多语言，禁止硬编码：
  - C#：`Lang.T("键")` / `Lang.TF("键", args)`（占位符 `{0}` `{1}`…）
  - XAML：`{l:T 键}`（需 `xmlns:l="clr-namespace:SideDesk.Framework"`），切换语言实时刷新。
- 日志、协议层固定文本（HTTP header、错误码常量）**不接入**。
- **用户数据默认值与数据值文本不翻译**（模型默认名、分类名、匹配标识固定名、品牌名等）——它们参与持久化与匹配；如需多语言在展示层用 `Lang.T` 转换。

**键命名**：`{域}.{区域}.{语义}`，小写驼峰，点分层，最多 4 层（如 `settings.apiKey.deleteConfirm`、`common.checkIn`、`nativePanel.columnCountValue`）。

**关键文件**：
- `tools/i18n/keymap.json`：中文键 → 语义键总映射。
- `langs/{语言代码}.json`：13 种语言词典（运行时加载）。
- `langs/_legacy.json`：中文旧键 → 语义键的**永久兼容层**（外部插件可能仍调 `Lang.T("中文")`）。
- `tools/i18n/translations-override.json`：非英语言补译覆盖，`gen-dicts.ps1` 应用，保证幂等。

**新增键流程**：
1. `tools/i18n/keymap.json` 增加 `中文键 → 语义键`；
2. `powershell -ExecutionPolicy Bypass -File tools\i18n\gen-dicts.ps1`（幂等重建 `langs/`，新键非英以英文占位并登记待译清单）；
3. 补译写入 `translations-override.json` 后重跑；
4. **提交前**跑 `tools\i18n\check-i18n.ps1` 校验（键集一致、占位符一致、双向孤儿检查、硬编码中文扫描）。
   - 亦可使用 `tools/i18n/add-keys.ps1 -Data <utf8-json>` 批量落地语义键。

**回退链**：当前语言词典 → en（懒加载缓存）→ 键本身；中文旧键经 `_legacy.json` 归一化后同轨查询。缺键退出时写入 `%LocalAppData%\SideDesk\Logs\lang-missing.log`。

**已知限制**：命令式一次性赋值（`textBlock.Text = Lang.T(...)`）与 Style/Template 内 Setter 绑定的文本无法随语言切换实时刷新，需重开窗口 / 重建控件；`{l:T}` 直接绑定与 `Lang.Register` 注册目标可实时刷新。

### 12.2 新增组件清单（Checklist）

新增一种 `PanelType` 组件时，至少涉及：

1. **模型**（`Models/WindowConfig.cs`）：`PanelType` 加枚举 + `PanelItem` 加数据字段（如 `CheckInItems` / `CheckInColumns` / `CheckInStyle`）。
2. **存储**（`Services/LocalDbService.cs`）：行读写（如需行级数据）。
3. **同步**（`Services/RowChangeTracker.cs` + `CloudSyncService.cs` + 后端 `item-row` 的 `ITEM_TYPE_ROW_TYPE`）：若数据需云同步。
4. **UI**（`Controls/NativeModePanel.<Name>.cs` partial）：`Build<Name>Widget` + 交互；`CreatePanelWidget` switch 加case。
5. **注册**（`Controls/NativeModePanel.xaml.cs` + `.xaml` + `Windows/TeamLibraryWindow.xaml.cs`）：添加处理器、分组菜单数组、`PopulateAddModuleMenu`、团队库图标 / 显示名。
6. **i18n**：按 [§12.1](#121-多语言i18n-开发约定) 走工具链，勿硬编码。
7. **构建**：`dotnet build` 零错误后再冒烟验证。

> 注意实现坑位：`UniformGrid` 位于 `System.Windows.Controls.Primitives`；局部函数捕获的局部变量必须在调用点之前赋值（否则 CS0165）；`WrapWidget` 的 `actionMenu` 参数只复制顶层菜单项，**不复制子菜单**——挂「列数 / 样式」这类子菜单须用 `CreateModuleControl` 返回的 `ModuleControl.AddCustomMenuItem`。

---

## 13. 端口与地址速查表

| 项目 | 地址 / 端口 | 说明 |
|---|---|---|
| 本机 HTTP API | `http://127.0.0.1:19195/` | 变量读写 / 通知 / AI Chat 代理 / 状态（可改端口） |
| MCP 服务 | `http://127.0.0.1:19192/` | AI 助手调用（21 工具） |
| 扩展 WebSocket | `127.0.0.1:19191` | 与浏览器扩展通信（调试回退 19190） |
| CDP 自动端口 | `9300+` 起 | 数据看板抓取连接浏览器 |
| 后端 API 基址（中） | `https://api.sidedesk.bigjing.com/console` | 生产 |
| 后端 API 基址（英） | `https://api.sidedesk.desktxp.com/console` | 生产 |
| 站点主页 | `https://sidedesk.bigjing.com` / `https://sidedesk.desktxp.com` | 中文区 / 国际区 |
| 更新源 | `cdn.bigjing.com/updates/sidedesk-cn` / `…desktxp.com/updates` | Velopack |
| 本地开发 | Vite `http://localhost:3007`（`/api` 代理后端 8082） | 前端联调 |

---

> **文档说明**：本文基于代码库实测整理。组件的细粒度交互、后端接口的字段级定义、以及插件桥方法的行为，如用于生产集成，请以对应源码为权威（客户端 `Services/`、`Controls/`；后端 `src/module/sidebar/`；插件规范 `docs/html-plugin-spec.md`）。
