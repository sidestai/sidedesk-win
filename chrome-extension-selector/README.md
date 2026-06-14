# CSS Selector Picker - Chrome 扩展

一个简单实用的元素选择器工具，可以帮助你快速获取网页元素的 CSS Selector 路径。

## 功能特性

✨ **核心功能**
- 点击页面元素自动获取 CSS Selector
- 实时高亮显示鼠标悬停的元素
- 一键复制选择器到剪贴板
- 支持自定义选择器生成策略

⚙️ **可配置选项**
- 优先使用 ID（如果元素有ID）
- 使用 Class 名称
- 显示/隐藏标签名
- 自动处理重复元素的 `:nth-child`

## 安装步骤

1. **打开 Chrome 扩展管理页面**
   - 地址栏输入：`chrome://extensions/`
   - 或者：菜单 → 更多工具 → 扩展程序

2. **启用开发者模式**
   - 点击右上角的"开发者模式"开关

3. **加载扩展**
   - 点击"加载已解压的扩展程序"
   - 选择 `chrome-extension-selector` 文件夹
   - 确认加载

4. **开始使用**
   - 点击浏览器工具栏中的扩展图标
   - 点击"开始选择"按钮
   - 在页面上点击想要获取选择器的元素
   - 复制生成的 CSS Selector

## 使用方法

### 基础使用

1. 打开任意网页
2. 点击扩展图标打开面板
3. 点击"开始选择"按钮（按钮变绿表示已激活）
4. 在页面上移动鼠标，会看到蓝色高亮框
5. 点击目标元素
6. 选择器会显示在面板的文本框中
7. 点击"复制"按钮复制到剪贴板

### 快捷键

- **ESC**：退出选择模式

### 选项说明

- **优先使用 ID**：如果元素有 `id` 属性，会优先使用 `#id` 格式
- **使用 Class**：在选择器中包含 `class` 名称
- **显示标签名**：在选择器中显示 HTML 标签名（如 `div`、`span`）

## 示例

### 输入元素
```html
<div id="main">
  <article class="post featured">
    <h2 class="title">文章标题</h2>
  </article>
</div>
```

### 生成的选择器

**启用所有选项**：
```css
#main > article.post.featured > h2.title
```

**仅使用 ID**：
```css
#main > article:nth-child(1) > h2:nth-child(1)
```

**不使用标签名**：
```css
#main > .post.featured:nth-child(1) > .title:nth-child(1)
```

## 技术细节

### Manifest V3
使用最新的 Chrome Extension Manifest V3 规范

### 文件结构
```
chrome-extension-selector/
├── manifest.json      # 扩展配置文件
├── popup.html         # 弹出窗口界面
├── popup.js          # 弹出窗口逻辑
├── content.js        # 页面内容脚本
├── content.css       # 页面样式
├── icons/            # 图标文件（需要添加）
└── README.md         # 说明文档
```

### 权限说明
- `activeTab`：访问当前活动标签页
- 不需要任何其他敏感权限

## 注意事项

⚠️ **重要提示**

1. 需要添加图标文件到 `icons/` 目录：
   - `icon16.png` (16x16)
   - `icon48.png` (48x48)
   - `icon128.png` (128x128)

2. 某些动态生成的元素可能选择器不够稳定，建议：
   - 优先选择有唯一 ID 的元素
   - 避免选择纯样式类
   - 测试选择器在页面刷新后是否仍然有效

3. 在 Shadow DOM 内的元素可能需要特殊处理

## 开发 & 调试

### 查看控制台日志
1. 右键点击扩展图标 → "选项"
2. 或在扩展管理页面点击 "Service Worker"
3. 查看 Console 输出

### 重新加载扩展
在 `chrome://extensions/` 页面点击刷新按钮

## 许可证

MIT License

## 反馈 & 建议

如有问题或建议，欢迎反馈！
