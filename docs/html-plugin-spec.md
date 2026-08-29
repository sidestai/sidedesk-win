# SideDesk HTML 组件模式插件规范（apiVersion 1）

> 适用宿主：SideDesk Windows 客户端（WPF / .NET，内嵌 WebView2）
> 对应实现：`Services/Plugins/`（PluginConfig / HtmlPluginHost / HtmlPluginAdapter / PluginLoaderService / PluginMarketBridge）、`Windows/PluginSettingsWindow`、示例 `plugins/HelloWorldHtml/`

---

## 1. 概述与设计原则

SideDesk 插件体系支持两种加载模式，由 `plugin.json` 的 `mode` 字段区分：

| 模式 | `mode` | 形态 | 运行环境 |
|---|---|---|---|
| 原生模式 | `"native"`（缺省） | .NET DLL（实现 `ISideDeskPlugin`） | 宿主进程内（可回收 AssemblyLoadContext 热加载） |
| HTML 组件模式 | `"html"` | 纯静态 HTML/CSS/JS | WebView2 沙箱（每插件独立虚拟主机域） |

设计原则（对照 Chrome extension 模型）：

1. **manifest 声明**：`plugin.json` ↔ `manifest.json`。安装、启用、版本、入口全部由 manifest 声明，宿主按声明加载。
2. **受控运行环境**：插件页面不在任意 `file://` 或公网域运行，而是运行在宿主分配的专属虚拟主机 `https://sd-plugin-<id>.local/` 上，与本机其他插件、宿主自身页面相互隔离（独立 origin，独立存储）。
3. **最小能力暴露**：插件只能通过 `window.sideDesk` 桥调用宿主白名单内的方法（当前为 `getConfig` / `setConfig` / `openUrl`）。未注册方法一律结构化拒绝。
4. **消息桥而非直接互访**：插件与宿主之间仅通过版本化的 JSON 消息信封通信（postMessage 进程内通道，类比 `chrome.runtime.sendMessage`），宿主内部状态（账号、凭据、其他插件数据）对插件完全不可见。
5. **复用现有生命周期**：HTML 插件经 `HtmlPluginAdapter` 适配为 `ISideDeskPlugin`，安装 / 市场 / 挂载 / 启停 / 卸载全部复用现有 DLL 插件链路，不引入平行体系。

## 2. 包结构

插件以 zip 包分发（本地导入或市场下载），解压后结构：

```
<插件根>/
├── plugin.json        # 必需：manifest
├── module.html        # 必需（html 模式）：模块显示页
├── settings.html      # 可选：设置页（提供后设置-插件列表出现「设置」入口）
└── assets/            # 可选：图片 / 样式 / 脚本等静态资源
```

- zip 可以是单层（`plugin.json` 在压缩包根部）或带一个根文件夹，安装器按 `FindPluginRoot` 规则自动定位（与现有 DLL 插件一致）。
- **纯静态约束**：html 模式不允许包含任何可执行文件 / DLL；包内所有资源经虚拟主机只读映射被 WebView2 加载。

## 3. Manifest 字段表（plugin.json）

| 字段 | 类型 | 必需 | 说明 |
|---|---|---|---|
| `id` | string | 是 | 插件唯一标识；用于目录名、虚拟主机名、私有存储文件名。建议使用小写字母/数字/连字符 |
| `name` | string | 是 | 显示名称 |
| `version` | string | 是 | 插件版本 |
| `description` | string | 否 | 描述 |
| `author` | string | 否 | 作者 |
| `icon` | string | 否 | emoji 或图片路径（默认 🧩） |
| `minHostVersion` | string | 否 | 最低宿主版本 |
| `enabled` | bool | 否 | 默认 `true` |
| `mode` | string | 否 | **新增**：`"native"`（默认）或 `"html"` |
| `moduleHtml` | string | html 模式必需 | **新增**：模块页 HTML 文件（相对插件目录）。安装与加载时强制校验存在 |
| `settingsHtml` | string | 否 | **新增**：设置页 HTML 文件。文件存在时设置页出现「插件设置」按钮 |
| `permissions` | string[] | 否 | **新增（预留）**：声明式权限列表。当前版本不做强制网关，仅作声明与未来兼容 |
| `apiVersion` | int | 否 | **新增**：依赖的宿主 API 版本，默认 `1`。大于宿主支持版本时拒绝加载 |

原生模式字段（`entry` / `pluginClass` / `xaml`）html 模式不使用。

示例：

```json
{
  "id": "hello-world-html",
  "name": "Hello World（HTML 模式）",
  "version": "0.1.0",
  "mode": "html",
  "moduleHtml": "module.html",
  "settingsHtml": "settings.html",
  "permissions": [],
  "apiVersion": 1,
  "icon": "🌐",
  "minHostVersion": "1.0.0",
  "enabled": true
}
```

## 4. 运行环境与沙箱

实现位于 `Services/Plugins/HtmlPluginHost.cs`（模块页与设置页共用的唯一 WebView2 封装）：

1. **虚拟主机映射**：`EnsureCoreWebView2Async`（共享默认 Environment）后调用
   `SetVirtualHostNameToFolderMapping("sd-plugin-<id>.local", 插件目录, Allow)`，
   插件静态资源仅能从自己的域解析；卸载时 `ClearVirtualHostNameToFolderMapping` 清除。
2. **CSP 由宿主注入**：文档响应不经由映射直出，而是由宿主经 `WebResourceRequested` 自服务并附加
   `Content-Security-Policy` **响应头**（不信任插件自带 `<meta>`）：

   ```
   default-src 'self';
   script-src 'self' 'unsafe-inline';
   style-src 'self' 'unsafe-inline';
   img-src 'self' data:; font-src 'self' data:;
   connect-src 'self'; frame-src 'none'; object-src 'none'; base-uri 'self'
   ```

   效果：外链脚本 / 外链资源 / iframe 嵌入被阻断；仅允许插件域内资源与内联脚本样式。
3. **导航白名单**：`NavigationStarting` 拦截非本虚拟主机导航 —— 一律取消；`http/https` 外链转系统默认浏览器打开（参照 `NotificationActionService.OpenUrl`）。
4. **窗口与调试限制**：`AreDevToolsEnabled = false`、`IsStatusBarEnabled = false`、`AreDefaultContextMenusEnabled = false`；`NewWindowRequested` 全量接管（`window.open` / `target=_blank` 不放行，外链转系统浏览器）。
5. **路径越界防护**：文档自服务时对相对路径做 `GetFullPath` 前缀校验，拒绝跳出插件目录的请求（返回 404）。

> 注意：`localStorage` 等 Web 存储以虚拟主机 origin 隔离，插件可用，但**状态持久化请一律使用 `getConfig/setConfig`**（见第 8 章），宿主卸载插件时私有存储语义清晰且跨实例共享。

## 5. 消息协议（版本化）

所有宿主 ↔ 插件消息均为 JSON 信封，携带协议版本 `v`（当前为 `1`）。宿主对 `v != 1` 的消息直接忽略。

**请求（插件 → 宿主）**

```json
{ "v": 1, "id": 3, "method": "setConfig", "params": { "userName": "小王" } }
```

**响应（宿主 → 插件）**

```json
{ "v": 1, "id": 3, "ok": true, "data": null }
```

```json
{ "v": 1, "id": 3, "ok": false, "error": { "code": "unknownMethod", "message": "未知方法: foo" } }
```

**事件（宿主 → 插件，无 id、不期待响应）**

```json
{ "v": 1, "event": "themeChanged", "data": { "theme": "dark" } }
```

**错误码**

| code | 含义 |
|---|---|
| `unknownMethod` | 未注册的方法名（白名单之外一律拒绝） |
| `invalidParams` | 参数缺失或类型不符（如 `setConfig` 传了非对象、`openUrl` 非 http/https） |

### 注入 SDK：window.sideDesk

宿主经 `AddScriptToExecuteOnDocumentCreated` 在每个文档创建时注入桥脚本，插件直接使用 `window.sideDesk`（Promise 化）：

| 成员 | 签名 | 说明 |
|---|---|---|
| `apiVersion` | `number` | 桥协议版本（当前 `1`） |
| `call` | `call(method, params) => Promise` | 通用调用；未注册方法 reject 结构化错误 |
| `getConfig` | `() => Promise<object>` | 读取插件私有配置 |
| `setConfig` | `(data: object) => Promise` | 覆盖式写入插件私有配置 |
| `openUrl` | `(url: string) => Promise` | 用系统默认浏览器打开链接（仅 http/https） |
| `onEvent` | `(cb) => unsubscribe` | 订阅宿主事件；回调签名 `cb(data, eventName)` |

## 6. 宿主 API 参考

### 6.1 getConfig / setConfig（插件私有存储）

- 存储位置：`%LocalAppData%\SideDesk\plugin-data\<pluginId>.json`（按插件命名空间，文件名经清洗）。
- 读写一律经桥（`HtmlPluginDataStore`），插件无法触及其他插件或宿主数据。
- `setConfig` 为**整体覆盖**语义：请先 `getConfig` 合并再写回（示例插件即此模式）。
- 值必须是可 JSON 序列化的对象；建议保持小体积（偏好/状态类数据）。

### 6.2 openUrl

`window.sideDesk.openUrl('https://example.com')`：宿主用系统默认浏览器打开。仅接受 `http://` / `https://`，其余协议以 `invalidParams` 拒绝。**不存在任何由宿主代登录 / 代请求的变体。**

### 6.3 事件

| 事件 | data | 触发时机 |
|---|---|---|
| `themeChanged` | `{ "theme": "dark" \| "light" }` | 宿主主题切换时（挂接全局 `App.ThemeChanged`），取值为 `App.FrontendThemeParam` |

### 6.4 安全红线

- **桥绝不透传 `AccountToken` 或任何凭据**。导航 URL、消息信封、注入脚本中均不含宿主登录态；插件页面所在虚拟主机域与宿主前端域不同源，也无法读取宿主 Cookie / 存储。
- 未注册消息类型 / 未知 `method` 一律返回结构化错误，不做任何副作用。
- 方法白名单新增必须经宿主代码评审，默认拒绝。

## 7. 主题与语言

- **初始主题**：模块页 / 设置页导航地址附带 `?theme=dark|light`（沿用宿主 `App.FrontendThemeParam` 约定，与登录页、市场页等内嵌前端一致）。插件从 `location.search` 读取。
- **运行中切换**：宿主切换主题时广播 `themeChanged` 事件，插件经 `onEvent` 订阅并即时适配（示例插件以 `data-theme` 属性切换 CSS 变量）。
- **多语言**：本期宿主不向 html 插件提供词条服务。插件自行内嵌多语言（建议：按 `navigator.language` 自选文案，或经 `getConfig` 持久化用户语言偏好）。

## 8. 生命周期

| 阶段 | 行为（与现有 DLL 插件一致） |
|---|---|
| 安装 | 本地 zip 导入或市场下载 → 暂存目录安全解压 → 校验 `plugin.json`（html 模式额外校验 `moduleHtml` 存在）→ 移动到 `plugins/<id>` → 热加载 |
| 启用 / 禁用 | 设置页开关写入 `Settings.DisabledPlugins` 并重载；`mode` 无特殊分支 |
| 更新 | 同 ID 覆盖安装：先卸载旧实例（`OnUnloaded` 释放全部宿主控件）再部署加载 |
| 卸载 | 设置页「删除」：卸载实例 + 删除插件目录。私有存储文件 `%LocalAppData%\SideDesk\plugin-data\<id>.json` 当前保留（可手动清理） |
| 模块页显示 | 「添加组件 → 插件」选择后由 `CreatePluginUI()` 返回 `HtmlPluginHost` 控件；同一插件可多实例，各实例相互独立 |
| 状态持久化 | **模块页是显示态**：不保证常驻。一切需要跨实例 / 跨重启保留的状态一律经 `getConfig`/`setConfig` 落盘 |
| 应用退出 | `App.DisposeAllWebView2` 既有机制 + 适配器 `OnUnloaded` 显式 `Dispose`（清映射、退订阅、释放控件） |

> 设置页保存后，已显示的模块实例不会自动刷新；模块在下次创建（添加 / 重渲染 / 重启）时读取新配置。需要即时联动的插件可自行轮询 `getConfig` 或约定用户操作后重建模块。

## 9. 分发与安全

### 分发

- **本地导入**：设置 → 插件 → 导入插件（选择 zip）。
- **插件市场**：市场后端（`sidedesk_plugin` 表 / download 接口）与插件形态无关，html 插件零改动上架。
- **哈希 / 签名**：规范预留项，本期不实现。未来版本将在安装链路追加 sha256 校验与发布者签名。

### 安装加固（已实现）

| 防护 | 手段 |
|---|---|
| zip slip | 解压条目路径前缀校验，拒绝越界（既有） |
| zip bomb | 条目数上限 **5000**、解压累计字节上限 **200MB**（声明长度与实际写入字节双累计） |
| 非法包 | 缺 `plugin.json` / 缺 `id` / html 模式缺 `moduleHtml` → 弹窗提示并丢弃暂存目录 |

### 开发者安全清单

- ✅ 所有脚本 / 样式 / 图片内置于包内（CSP 阻断外链脚本，`connect-src 'self'` 限制网络请求）。
- ✅ 状态用 `getConfig/setConfig`，不要依赖 `localStorage`。
- ❌ 禁止尝试加载外链脚本 / 字体 / 远程框架（会被 CSP 阻断）。
- ❌ 禁止在页面内收集或存储任何用户凭据；桥与宿主不提供、也不应被要求提供登录态。
- ❌ 不要依赖 DevTools / 右键菜单 / `window.open`（沙箱已禁用）。

## 10. 兼容策略

- **`mode` 缺省 = `"native"`**：现存 DLL 插件（DeskClock / HelloWorld / SystemCleaner 等）manifest 无需任何修改，加载路径逐行不变。
- **`apiVersion`**：宿主当前支持 `1`；`apiVersion > 1` 的包记日志并拒绝加载（安装可完成但加载失败时宿主弹窗提示重启重试）。
- **WebView2 Runtime 缺失**：复用既有 `common.webview2InitializationFailed` 文案在模块区域展示错误。
- **旧客户端装新包**：不认识 `mode:"html"` 字段的旧版宿主会把其按 native 处理并因无 `entry` 加载失败（不崩溃），建议市场标注最低宿主版本（`minHostVersion`）。

## 11. 示例插件全文（附录）

`plugins/HelloWorldHtml/`：问候语 + 经桥读写配置的计数器（模块页），设置页修改称呼；深浅主题自适应。

### plugin.json

```json
{
  "id": "hello-world-html",
  "name": "Hello World（HTML 模式）",
  "version": "0.1.0",
  "description": "HTML 组件模式示例插件：问候语 + 经 window.sideDesk 桥读写配置的计数器，支持深浅主题",
  "author": "SideDesk 社区",
  "mode": "html",
  "moduleHtml": "module.html",
  "settingsHtml": "settings.html",
  "permissions": [],
  "apiVersion": 1,
  "icon": "🌐",
  "minHostVersion": "1.0.0",
  "enabled": true
}
```

### module.html

```html
<!DOCTYPE html>
<html lang="zh-CN" data-theme="dark">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>Hello World（HTML 模式）</title>
<style>
  /* 主题令牌：宿主经 ?theme= 与 themeChanged 事件下发，页面据此切换深浅色 */
  :root, [data-theme="dark"] {
    --bg: #16181d;
    --panel: #1e2128;
    --ink: #e8e4d8;
    --muted: #8a8fa0;
    --accent: #e8b04b;
    --accent-ink: #16181d;
    --line: rgba(232, 228, 216, 0.14);
  }
  [data-theme="light"] {
    --bg: #f4f1e8;
    --panel: #fffdf6;
    --ink: #23252b;
    --muted: #84806f;
    --accent: #b96a2a;
    --accent-ink: #fffdf6;
    --line: rgba(35, 37, 43, 0.14);
  }

  * { margin: 0; padding: 0; box-sizing: border-box; }
  html, body { height: 100%; }
  body {
    background: var(--bg);
    color: var(--ink);
    font-family: Georgia, "Palatino Linotype", "Songti SC", "SimSun", serif;
    display: flex; align-items: stretch; justify-content: stretch;
    overflow: hidden;
    transition: background .35s ease, color .35s ease;
  }

  .ticket {
    flex: 1;
    display: flex; flex-direction: column; justify-content: space-between;
    background: var(--panel);
    border: 1px dashed var(--line);
    border-radius: 14px;
    padding: 18px 20px;
    margin: 8px;
    transition: background .35s ease, border-color .35s ease;
  }

  .kicker {
    font-family: Consolas, "Courier New", monospace;
    font-size: 10px; letter-spacing: .32em; text-transform: uppercase;
    color: var(--muted);
  }

  .greeting {
    font-size: 21px; line-height: 1.45; font-weight: 600;
    margin-top: 8px;
  }
  .greeting em { font-style: normal; color: var(--accent); }

  .counter-row {
    display: flex; align-items: flex-end; justify-content: space-between;
    gap: 12px;
    border-top: 1px solid var(--line);
    padding-top: 12px;
  }
  .count-label {
    font-family: Consolas, "Courier New", monospace;
    font-size: 10px; letter-spacing: .24em; color: var(--muted);
    text-transform: uppercase; margin-bottom: 2px;
  }
  .count {
    font-family: Consolas, "Courier New", monospace;
    font-size: 34px; font-weight: 700; line-height: 1;
    font-variant-numeric: tabular-nums;
  }
  .count.bump { animation: bump .28s ease; }
  @keyframes bump { 40% { transform: scale(1.14); color: var(--accent); } }

  button.punch {
    font-family: inherit; font-size: 14px;
    background: var(--accent); color: var(--accent-ink);
    border: none; border-radius: 10px;
    padding: 10px 18px;
    cursor: pointer;
    box-shadow: 0 3px 0 rgba(0,0,0,.22);
    transition: transform .12s ease, box-shadow .12s ease, background .35s ease;
  }
  button.punch:hover { transform: translateY(-1px); box-shadow: 0 4px 0 rgba(0,0,0,.26); }
  button.punch:active { transform: translateY(2px); box-shadow: 0 1px 0 rgba(0,0,0,.22); }

  .stub {
    display: flex; justify-content: space-between;
    font-family: Consolas, "Courier New", monospace;
    font-size: 9px; letter-spacing: .18em; color: var(--muted);
    margin-top: 10px;
  }
</style>
</head>
<body>
  <div class="ticket">
    <div>
      <div class="kicker">SideDesk · Html Plugin</div>
      <div class="greeting" id="greeting">你好，朋友。</div>
    </div>
    <div>
      <div class="counter-row">
        <div>
          <div class="count-label">Punch Count</div>
          <div class="count" id="count">0</div>
        </div>
        <button class="punch" id="addBtn">+1</button>
      </div>
      <div class="stub">
        <span>HELLO-WORLD-HTML</span>
        <span id="stamp">API v1</span>
      </div>
    </div>
  </div>

<script>
(function () {
  var countEl = document.getElementById('count');
  var greetingEl = document.getElementById('greeting');
  var cfg = { count: 0, userName: '' };
  var saving = false;

  // 主题：初始取 URL 上的 ?theme=，之后跟随宿主 themeChanged 事件
  var theme = new URLSearchParams(location.search).get('theme') || 'dark';
  applyTheme(theme);
  window.sideDesk.onEvent(function (data, event) {
    if (event === 'themeChanged' && data && data.theme) applyTheme(data.theme);
  });

  function applyTheme(t) {
    document.documentElement.setAttribute('data-theme', t === 'light' ? 'light' : 'dark');
  }

  function render() {
    var name = (cfg.userName || '').trim() || '朋友';
    greetingEl.innerHTML = '';
    greetingEl.appendChild(document.createTextNode('你好，'));
    var em = document.createElement('em');
    em.textContent = name;
    greetingEl.appendChild(em);
    greetingEl.appendChild(document.createTextNode('。'));
    countEl.textContent = String(cfg.count | 0);
  }

  async function persist() {
    if (saving) return;
    saving = true;
    try { await window.sideDesk.setConfig(cfg); } catch (e) { /* 宿主返回结构化错误 */ }
    saving = false;
  }

  document.getElementById('addBtn').addEventListener('click', function () {
    cfg.count = (cfg.count | 0) + 1;
    render();
    countEl.classList.remove('bump');
    void countEl.offsetWidth; // 重启动画
    countEl.classList.add('bump');
    persist();
  });

  // 初始读取插件私有配置（在「设置」页修改用户名后，下次创建模块即生效）
  window.sideDesk.getConfig()
    .then(function (data) { if (data) { cfg = Object.assign(cfg, data); } render(); })
    .catch(render);
})();
</script>
</body>
</html>
```

### settings.html

```html
<!DOCTYPE html>
<html lang="zh-CN" data-theme="dark">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>Hello World（HTML 模式）· 设置</title>
<style>
  :root, [data-theme="dark"] {
    --bg: #16181d;
    --panel: #1e2128;
    --ink: #e8e4d8;
    --muted: #8a8fa0;
    --accent: #e8b04b;
    --accent-ink: #16181d;
    --line: rgba(232, 228, 216, 0.14);
    --input: #121418;
  }
  [data-theme="light"] {
    --bg: #f4f1e8;
    --panel: #fffdf6;
    --ink: #23252b;
    --muted: #84806f;
    --accent: #b96a2a;
    --accent-ink: #fffdf6;
    --line: rgba(35, 37, 43, 0.14);
    --input: #f4f1e8;
  }

  * { margin: 0; padding: 0; box-sizing: border-box; }
  html, body { height: 100%; }
  body {
    background: var(--bg);
    color: var(--ink);
    font-family: Georgia, "Palatino Linotype", "Songti SC", "SimSun", serif;
    display: flex; align-items: center; justify-content: center;
    transition: background .35s ease, color .35s ease;
  }

  .card {
    width: min(440px, calc(100% - 48px));
    background: var(--panel);
    border: 1px dashed var(--line);
    border-radius: 14px;
    padding: 28px 30px 24px;
    transition: background .35s ease, border-color .35s ease;
  }

  .kicker {
    font-family: Consolas, "Courier New", monospace;
    font-size: 10px; letter-spacing: .32em; text-transform: uppercase;
    color: var(--muted);
  }
  h1 { font-size: 20px; margin: 10px 0 4px; }
  .hint { font-size: 12px; color: var(--muted); line-height: 1.6; margin-bottom: 18px; }

  label {
    display: block;
    font-family: Consolas, "Courier New", monospace;
    font-size: 10px; letter-spacing: .24em; text-transform: uppercase;
    color: var(--muted);
    margin-bottom: 6px;
  }
  input[type="text"] {
    width: 100%;
    font-family: inherit; font-size: 15px;
    color: var(--ink);
    background: var(--input);
    border: 1px solid var(--line);
    border-radius: 10px;
    padding: 11px 14px;
    outline: none;
    transition: border-color .2s ease, background .35s ease;
  }
  input[type="text"]:focus { border-color: var(--accent); }

  .row { display: flex; align-items: center; gap: 14px; margin-top: 18px; }
  button.save {
    font-family: inherit; font-size: 14px;
    background: var(--accent); color: var(--accent-ink);
    border: none; border-radius: 10px;
    padding: 10px 22px;
    cursor: pointer;
    box-shadow: 0 3px 0 rgba(0,0,0,.22);
    transition: transform .12s ease, box-shadow .12s ease, background .35s ease;
  }
  button.save:hover { transform: translateY(-1px); box-shadow: 0 4px 0 rgba(0,0,0,.26); }
  button.save:active { transform: translateY(2px); box-shadow: 0 1px 0 rgba(0,0,0,.22); }
  button.save:disabled { opacity: .55; cursor: default; transform: none; }

  .status { font-size: 12px; color: var(--muted); min-height: 16px; }
  .status.ok { color: var(--accent); }
</style>
</head>
<body>
  <div class="card">
    <div class="kicker">SideDesk · Html Plugin · Settings</div>
    <h1>Hello World 设置</h1>
    <p class="hint">配置经 window.sideDesk.setConfig 保存到插件私有存储，模块页下次创建时生效。</p>

    <label for="userName">你的称呼</label>
    <input type="text" id="userName" maxlength="24" placeholder="朋友" autocomplete="off">

    <div class="row">
      <button class="save" id="saveBtn">保存</button>
      <span class="status" id="status"></span>
    </div>
  </div>

<script>
(function () {
  var input = document.getElementById('userName');
  var saveBtn = document.getElementById('saveBtn');
  var status = document.getElementById('status');
  var cfg = {};
  var statusTimer = 0;

  var theme = new URLSearchParams(location.search).get('theme') || 'dark';
  applyTheme(theme);
  window.sideDesk.onEvent(function (data, event) {
    if (event === 'themeChanged' && data && data.theme) applyTheme(data.theme);
  });

  function applyTheme(t) {
    document.documentElement.setAttribute('data-theme', t === 'light' ? 'light' : 'dark');
  }

  function flash(text, ok) {
    status.textContent = text;
    status.className = 'status' + (ok ? ' ok' : '');
    clearTimeout(statusTimer);
    statusTimer = setTimeout(function () { status.textContent = ''; }, 2500);
  }

  saveBtn.addEventListener('click', async function () {
    cfg.userName = input.value.trim();
    saveBtn.disabled = true;
    try {
      await window.sideDesk.setConfig(cfg);
      flash('已保存 ✓', true);
    } catch (err) {
      flash('保存失败：' + ((err && err.message) || '未知错误'), false);
    }
    saveBtn.disabled = false;
  });

  input.addEventListener('keydown', function (e) {
    if (e.key === 'Enter') saveBtn.click();
  });

  window.sideDesk.getConfig()
    .then(function (data) {
      if (data) { cfg = data; input.value = data.userName || ''; }
    })
    .catch(function () { flash('读取配置失败', false); });
})();
</script>
</body>
</html>
```

---

## 附：实现文件索引

| 文件 | 角色 |
|---|---|
| `Services/Plugins/PluginConfig.cs` | manifest 模型（新增 Mode / ModuleHtml / SettingsHtml / Permissions / ApiVersion） |
| `Services/Plugins/HtmlPluginHost.cs` | 唯一 WebView2 沙箱封装 + 消息桥 + 插件私有存储（HtmlPluginDataStore） |
| `Services/Plugins/HtmlPluginAdapter.cs` | ISideDeskPlugin 适配器 |
| `Services/Plugins/PluginLoaderService.cs` | `LoadPluginFromDirectory` 前置 `mode == "html"` 分支 |
| `Services/Plugins/PluginMarketBridge.cs` | 安装校验（moduleHtml）+ zip bomb 上限 |
| `SettingsWindow.HtmlPlugins.cs` | 设置页「插件设置」按钮（分部类） |
| `Windows/PluginSettingsWindow.xaml(.cs)` | 设置窗口（装载 settings.html） |
| `plugins/HelloWorldHtml/` | 本规范参照实现 |
