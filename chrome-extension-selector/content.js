// Content Script - 在页面中运行
let isActive = false;
let highlightOverlay = null;
let options = {
  useId: true,
  useClass: true,
  showTag: true
};

// ===== WebSocket 连接（连接 SideDesk 本地服务）=====
const WS_URL = 'ws://127.0.0.1:19191/';
let ws = null;
let wsReconnectTimer = null;

function connectWebSocket() {
  // 仅在顶层页面连接，避免每个 iframe 都建立连接
  if (window.self !== window.top) return;

  try {
    ws = new WebSocket(WS_URL);
  } catch (err) {
    console.warn('WS 创建失败，稍后重试', err);
    scheduleReconnect();
    return;
  }

  ws.addEventListener('open', () => {
    console.log('成功链接WS');
  });

  ws.addEventListener('message', (event) => {
    console.log('[WS] 收到消息:', event.data);
    handleWsMessage(event.data);
  });

  ws.addEventListener('close', () => {
    ws = null;
    scheduleReconnect();
  });

  ws.addEventListener('error', () => {
    // 出错时关闭，交由 close 处理重连
    try { ws && ws.close(); } catch (e) {}
  });
}

function scheduleReconnect() {
  if (wsReconnectTimer) return;
  wsReconnectTimer = setTimeout(() => {
    wsReconnectTimer = null;
    connectWebSocket();
  }, 3000);
}

// 处理 SideDesk 下发的抓取请求
function handleWsMessage(data) {
  let msg;
  try {
    msg = JSON.parse(data);
  } catch (e) {
    return; // 非 JSON 消息，忽略
  }
  if (!msg || msg.type !== 'fetch') {
    console.log('[WS] 消息type不是fetch，忽略, msg:', msg);
    return;
  }

  // 仅当当前页面 URL 与目标匹配时才处理并回复（不匹配的页面保持静默）
  if (!urlMatches(msg.url)) {
    console.log('[WS] URL不匹配，跳过. 目标URL:', msg.url, '当前页面:', location.href);
    return;
  }

  console.log('[WS] URL匹配，开始执行抓取. selector:', msg.selector);

  const result = {
    type: 'fetchResult',
    requestId: msg.requestId,
    label: msg.label || '',
    url: location.href
  };

  // 带重试的抓取：页面可能是SPA动态渲染，元素尚未出现
  const MAX_RETRIES = 5;
  const RETRY_DELAYS = [500, 1000, 2000, 3000, 5000]; // 累计最多等 ~11.5s

  function findElement(sel) {
    if (sel && (sel.startsWith('/') || sel.startsWith('('))) {
      console.log('[WS] 使用XPath查找:', sel);
      const xpathResult = document.evaluate(sel, document, null, XPathResult.FIRST_ORDERED_NODE_TYPE, null);
      return xpathResult.singleNodeValue;
    } else {
      console.log('[WS] 使用CSS Selector查找:', sel);
      return document.querySelector(sel);
    }
  }

  function describeElement(el) {
    if (!el) return '(null)';
    const tag = el.tagName ? el.tagName.toLowerCase() : '(non-element)';
    const text = (el.textContent || '').trim().substring(0, 100);
    const inner = (el.innerHTML || '').substring(0, 200);
    return `tag=${tag}, textContent="${text}", innerHTML="${inner}"`;
  }

  function sendResult(res) {
    try {
      if (ws && ws.readyState === WebSocket.OPEN) {
        const payload = JSON.stringify(res);
        console.log('[WS] 发送消息:', payload);
        ws.send(payload);
      } else {
        console.warn('[WS] WebSocket未连接，无法发送结果. readyState:', ws ? ws.readyState : 'null');
      }
    } catch (e) {
      console.error('[WS] 发送结果时异常:', e);
    }
  }

  const _diag = { found: false, desc: '', pageUrl: location.href };

  function tryFetch(retryIndex) {
    try {
      const sel = msg.selector;
      const el = findElement(sel);
      const desc = describeElement(el);
      console.log('[WS] 第' + (retryIndex + 1) + '次查找结果:', desc);
      _diag.found = !!el;
      _diag.desc = desc;
      _diag.retryNum = retryIndex + 1;
      if (el) {
        const textContent = (el.textContent || '').trim();
        const innerText = (el.innerText || '').trim();
        const valueAttr = el.getAttribute ? (el.getAttribute('value') || '') : '';
        const inputVal = el.value || '';
        console.log('[WS] 各属性值: textContent="' + textContent + '", innerText="' + innerText + '", value="' + valueAttr + '", el.value="' + inputVal + '"');
        const value = textContent || innerText || inputVal || valueAttr;
        if (value) {
          result.success = true;
          result.value = value;
          result._diag = _diag;
          console.log('[WS] 抓取成功(第' + (retryIndex + 1) + '次), value:', value);
          sendResult(result);
          return;
        } else {
          console.log('[WS] 元素已找到但所有属性均为空(第' + (retryIndex + 1) + '次)');
        }
      } else {
        console.log('[WS] 元素未找到(第' + (retryIndex + 1) + '次), selector:', sel);
      }
    } catch (err) {
      console.log('[WS] 抓取异常(第' + (retryIndex + 1) + '次):', err);
      _diag.desc = '异常: ' + (err.message || String(err));
    }

    // 未成功，判断是否还有重试机会
    if (retryIndex < MAX_RETRIES - 1) {
      const delay = RETRY_DELAYS[retryIndex] || 3000;
      console.log('[WS] ' + delay + 'ms 后重试...');
      setTimeout(() => tryFetch(retryIndex + 1), delay);
    } else {
      // 全部重试完毕，返回失败
      result.success = false;
      result.error = '重试' + MAX_RETRIES + '次后仍未找到有效数据: ' + msg.selector;
      result._diag = _diag; // 附带最后一次诊断信息
      console.log('[WS] 抓取最终失败:', result.error);
      sendResult(result);
    }
  }

  tryFetch(0);
}

// URL 匹配规则：协议+主机相同，且当前路径以目标路径为前缀
function urlMatches(target) {
  if (!target) return false;
  try {
    const t = new URL(target, location.href);
    return location.origin === t.origin && location.pathname.startsWith(t.pathname);
  } catch (e) {
    return location.href.indexOf(target) === 0;
  }
}

// 插件启动即连接
connectWebSocket();

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
