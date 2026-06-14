<!-- 这是一个说明文件，请按照以下步骤生成图标 -->

# 图标生成步骤

## 方法一：使用图标生成器（推荐）

1. 在浏览器中打开 `create-icons.html` 文件
2. 点击"生成并下载图标"按钮
3. 会自动下载 3 个 PNG 文件：
   - icon16.png (16x16像素)
   - icon48.png (48x48像素)
   - icon128.png (128x128像素)
4. 将这3个文件保存到当前 `icons/` 目录
5. 在 `chrome://extensions/` 页面重新加载扩展

## 方法二：使用在线工具

访问以下任一在线图标生成器：
- https://favicon.io/
- https://www.favicon-generator.org/
- https://realfavicongenerator.net/

生成一个蓝色的瞄准镜/选择器图标，尺寸为 128x128，然后缩放到其他尺寸。

## 方法三：手动创建

使用任何图片编辑工具（如 Photoshop、GIMP、Paint.NET）：
1. 创建 128x128 的画布
2. 填充蓝色背景（#2196F3）
3. 绘制一个白色瞄准镜或选择器图标
4. 另存为 icon128.png
5. 缩小到 48x48 保存为 icon48.png
6. 缩小到 16x16 保存为 icon16.png

## 临时方案（如果没有图标也能用）

我已经修改了 manifest.json，暂时注释掉了 `icons` 字段，这样即使没有图标文件，扩展也能正常运行，只是扩展管理页面会显示默认图标。

要启用自定义图标，将以下代码添加回 `manifest.json`：

```json
"icons": {
  "16": "icons/icon16.png",
  "48": "icons/icon48.png",
  "128": "icons/icon128.png"
}
```
