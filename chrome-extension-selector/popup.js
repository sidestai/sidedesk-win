// Popup界面逻辑
const toggleBtn = document.getElementById('toggleBtn');
const copyBtn = document.getElementById('copyBtn');
const selectorText = document.getElementById('selectorText');
const statusDiv = document.getElementById('status');
const statusValue = document.getElementById('statusValue');

let isActive = false;

// 切换选择器状态
toggleBtn.addEventListener('click', async () => {
  isActive = !isActive;
  
  if (isActive) {
    toggleBtn.textContent = '停止选择';
    toggleBtn.classList.add('active');
    statusDiv.classList.add('active');
    statusValue.textContent = '选择中...';
  } else {
    toggleBtn.textContent = '开始选择';
    toggleBtn.classList.remove('active');
    statusDiv.classList.remove('active');
    statusValue.textContent = '未激活';
  }
  
  // 发送消息到content script
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  chrome.tabs.sendMessage(tab.id, {
    action: isActive ? 'activate' : 'deactivate'
  });
});

// 复制选择器
copyBtn.addEventListener('click', () => {
  const text = selectorText.value;
  if (text) {
    navigator.clipboard.writeText(text).then(() => {
      const originalText = copyBtn.textContent;
      copyBtn.textContent = '已复制!';
      setTimeout(() => {
        copyBtn.textContent = originalText;
      }, 1500);
    });
  }
});

// 监听来自content script的消息
chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message.action === 'selector') {
    selectorText.value = message.selector;
  }
});

// 保存选项
document.querySelectorAll('.options input').forEach(input => {
  input.addEventListener('change', () => {
    const options = {
      useId: document.getElementById('useId').checked,
      useClass: document.getElementById('useClass').checked,
      showTag: document.getElementById('showTag').checked
    };
    chrome.storage.local.set({ selectorOptions: options });
    
    // 发送选项到content script
    chrome.tabs.query({ active: true, currentWindow: true }, (tabs) => {
      if (tabs[0]) {
        chrome.tabs.sendMessage(tabs[0].id, {
          action: 'updateOptions',
          options: options
        });
      }
    });
  });
});

// 加载保存的选项
chrome.storage.local.get('selectorOptions', (result) => {
  if (result.selectorOptions) {
    document.getElementById('useId').checked = result.selectorOptions.useId;
    document.getElementById('useClass').checked = result.selectorOptions.useClass;
    document.getElementById('showTag').checked = result.selectorOptions.showTag;
  }
});
