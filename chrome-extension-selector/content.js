// Content Script - 在页面中运行
let isActive = false;
let highlightOverlay = null;
let options = {
  useId: true,
  useClass: true,
  showTag: true
};

// 加载选项
chrome.storage.local.get('selectorOptions', (result) => {
  if (result.selectorOptions) {
    options = result.selectorOptions;
  }
});

// 监听来自popup的消息
chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message.action === 'activate') {
    activate();
  } else if (message.action === 'deactivate') {
    deactivate();
  } else if (message.action === 'updateOptions') {
    options = message.options;
  }
});

// 激活选择器
function activate() {
  isActive = true;
  // 使用捕获阶段（true），在事件到达目标前就拦截
  document.addEventListener('mousemove', handleMouseMove, true);
  document.addEventListener('click', handleClick, true);
  document.addEventListener('mousedown', handleMouseDown, true);
  document.addEventListener('keydown', handleKeyDown, true);
  
  // 添加样式提示用户
  document.body.style.cursor = 'crosshair';
}

// 停用选择器
function deactivate() {
  isActive = false;
  document.removeEventListener('mousemove', handleMouseMove, true);
  document.removeEventListener('click', handleClick, true);
  document.removeEventListener('mousedown', handleMouseDown, true);
  document.removeEventListener('keydown', handleKeyDown, true);
  removeHighlight();
  
  // 恢复鼠标样式
  document.body.style.cursor = '';
}

// 鼠标按下时阻止默认行为（防止链接跳转、按钮点击等）
function handleMouseDown(e) {
  if (!isActive) return;
  
  // 阻止所有默认行为
  e.preventDefault();
  e.stopPropagation();
  e.stopImmediatePropagation();
  
  return false;
}

// 鼠标移动时高亮元素
function handleMouseMove(e) {
  if (!isActive) return;
  
  const target = e.target;
  if (target === highlightOverlay) return;
  
  highlightElement(target);
}

// 点击时获取选择器
function handleClick(e) {
  if (!isActive) return;
  
  // 阻止所有默认行为（包括链接跳转、表单提交等）
  e.preventDefault();
  e.stopPropagation();
  e.stopImmediatePropagation();
  
  const target = e.target;
  if (target === highlightOverlay) return;
  
  const selector = generateSelector(target);
  
  // 发送选择器到popup
  chrome.runtime.sendMessage({
    action: 'selector',
    selector: selector
  });
  
  // 显示提示
  showNotification(selector);
  
  // 返回false进一步阻止事件传播
  return false;
}

// 按ESC退出
function handleKeyDown(e) {
  if (e.key === 'Escape') {
    deactivate();
    chrome.runtime.sendMessage({
      action: 'deactivate'
    });
  }
}

// 高亮元素
function highlightElement(element) {
  removeHighlight();
  
  const rect = element.getBoundingClientRect();
  
  highlightOverlay = document.createElement('div');
  highlightOverlay.style.cssText = `
    position: fixed;
    left: ${rect.left}px;
    top: ${rect.top}px;
    width: ${rect.width}px;
    height: ${rect.height}px;
    border: 2px solid #2196F3;
    background: rgba(33, 150, 243, 0.1);
    pointer-events: none;
    z-index: 999999;
    box-sizing: border-box;
  `;
  
  document.body.appendChild(highlightOverlay);
}

// 移除高亮
function removeHighlight() {
  if (highlightOverlay && highlightOverlay.parentNode) {
    highlightOverlay.parentNode.removeChild(highlightOverlay);
    highlightOverlay = null;
  }
}

// 生成CSS Selector
function generateSelector(element) {
  const path = [];
  let current = element;
  
  while (current && current.nodeType === Node.ELEMENT_NODE) {
    let selector = '';
    
    // 优先使用ID
    if (options.useId && current.id) {
      selector = `#${current.id}`;
      path.unshift(selector);
      break;
    }
    
    // 使用标签名
    if (options.showTag) {
      selector = current.tagName.toLowerCase();
      
      // 使用Class
      if (options.useClass && current.className && typeof current.className === 'string') {
        const classes = current.className.trim().split(/\s+/).filter(c => c).slice(0, 2);
        if (classes.length > 0) {
          selector += '.' + classes.join('.');
        }
      }
      
      // 添加:nth-child来区分同名元素
      const parent = current.parentNode;
      if (parent) {
        const siblings = Array.from(parent.children).filter(
          child => child.tagName === current.tagName
        );
        
        if (siblings.length > 1) {
          const index = siblings.indexOf(current) + 1;
          selector += `:nth-child(${index})`;
        }
      }
    } else {
      // 不使用标签名，只用class和位置
      if (options.useClass && current.className && typeof current.className === 'string') {
        const classes = current.className.trim().split(/\s+/).filter(c => c).slice(0, 2);
        if (classes.length > 0) {
          selector = '.' + classes.join('.');
        } else {
          selector = '*';
        }
      } else {
        selector = '*';
      }
      
      const parent = current.parentNode;
      if (parent) {
        const siblings = Array.from(parent.children);
        if (siblings.length > 1) {
          const index = siblings.indexOf(current) + 1;
          selector += `:nth-child(${index})`;
        }
      }
    }
    
    path.unshift(selector);
    current = current.parentNode;
  }
  
  return path.join(' > ');
}

// 显示通知
function showNotification(selector) {
  const notification = document.createElement('div');
  notification.style.cssText = `
    position: fixed;
    top: 20px;
    right: 20px;
    background: #4CAF50;
    color: white;
    padding: 12px 16px;
    border-radius: 8px;
    font-size: 13px;
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
    box-shadow: 0 4px 12px rgba(0,0,0,0.2);
    z-index: 1000000;
    max-width: 300px;
    animation: slideIn 0.3s ease;
  `;
  
  notification.innerHTML = `
    <div style="font-weight: 600; margin-bottom: 4px;">✓ 已获取选择器</div>
    <div style="font-size: 11px; opacity: 0.9; word-break: break-all;">${selector}</div>
  `;
  
  document.body.appendChild(notification);
  
  setTimeout(() => {
    notification.style.animation = 'slideOut 0.3s ease';
    setTimeout(() => {
      if (notification.parentNode) {
        notification.parentNode.removeChild(notification);
      }
    }, 300);
  }, 2000);
}

// 添加动画样式
const style = document.createElement('style');
style.textContent = `
  @keyframes slideIn {
    from {
      transform: translateX(400px);
      opacity: 0;
    }
    to {
      transform: translateX(0);
      opacity: 1;
    }
  }
  
  @keyframes slideOut {
    from {
      transform: translateX(0);
      opacity: 1;
    }
    to {
      transform: translateX(400px);
      opacity: 0;
    }
  }
`;
document.head.appendChild(style);
