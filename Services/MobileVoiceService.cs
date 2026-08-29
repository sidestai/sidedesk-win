using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SideDesk.Services
{
    /// <summary>
    /// 手机扫码远程语音输入服务（单例）：
    /// 悬停"手机扫码"时经 <see cref="ClaimNewCodeAsync"/> 生成 12 位配对码并声明给后端（voice_pair_claim），
    /// 手机扫码配对后经后端 WS 中继音频分片，本程序流式语音识别后把文字粘贴到当前焦点。
    ///
    /// 协议契约（三端冻结，事件名/字段名不得更改）：
    /// - exe → 后端：voice_pair_claim  data {code}
    /// - 后端 → exe：voice_phone_joined  data {code}（手机配对成功，启动远程识别会话）
    /// - 后端 → exe：voice_chunk  data {code, seq, pcmB64}（16kHz/16bit/单声道裸 PCM，base64）
    /// - 后端 → exe：voice_stop  data {code}（手机松手 → 停止识别并粘贴）
    /// - 后端 → exe：voice_session_closed  data {code}（手机断连/超时 → 终止会话）
    /// </summary>
    public sealed class MobileVoiceService
    {
        private static readonly Lazy<MobileVoiceService> _instance = new(() => new MobileVoiceService());
        public static MobileVoiceService Instance => _instance.Value;

        /// <summary>手机配对成功（携带配对码）：UI 层应取 <see cref="CurrentAudioStream"/> 喂给识别引擎</summary>
        public event Action<string>? SessionStarted;
        /// <summary>手机松手：缓冲流写入端已关闭，UI 层停止识别并粘贴累计文本</summary>
        public event Action? SessionStopRequested;
        /// <summary>手机断连/超时：会话异常终止，UI 层停止识别并恢复麦克风</summary>
        public event Action? SessionAborted;

        private readonly object _gate = new();
        /// <summary>当前活动配对码（悬停二维码时生成；活动会话期间不轮换）</summary>
        private string? _activeCode;
        /// <summary>当前会话的音频缓冲流（识别引擎后台线程阻塞读取）</summary>
        private BlockingAudioStream? _sessionStream;
        /// <summary>UI 认领归属：多主窗口时同一会话仅首个认领窗口处理（带归属校验，非 owner 窗口不得动标志/停面板）</summary>
        private object? _uiClaimOwner;

        /// <summary>当前会话的音频流（无会话时为 null）</summary>
        public Stream? CurrentAudioStream
        {
            get { lock (_gate) return _sessionStream; }
        }

        private MobileVoiceService()
        {
            // 复用现有后端 WS 长连接：自行按 event 字段过滤远程语音消息（同 DataPollingService 模式）
            BackendWebSocketService.Instance.MessageReceived += OnBackendMessage;
            // 断连看门狗：后端 WS 断开时中继必然中断，终止当前会话（停止识别 + 恢复麦克风，走既有 SessionAborted 路径）
            BackendWebSocketService.Instance.Disconnected += OnBackendDisconnected;
        }

        /// <summary>
        /// 后端 WS 断连：作废当前会话（完成缓冲流 + 清空配对码），已认领时触发 SessionAborted。
        /// </summary>
        private void OnBackendDisconnected()
        {
            bool hadSession;
            lock (_gate)
            {
                hadSession = _sessionStream != null || _uiClaimOwner != null;
                try { _sessionStream?.Complete(); } catch { }
                _sessionStream = null;
                _activeCode = null;
                _uiClaimOwner = null;
            }
            if (!hadSession) return;
            Debug.WriteLine("[MobileVoice] backend WS disconnected, session aborted");
            try { SessionAborted?.Invoke(); }
            catch (Exception ex) { Debug.WriteLine($"[MobileVoice] SessionAborted handler error (swallowed): {ex.Message}"); }
        }

        /// <summary>
        /// 获取当前配对码并声明给后端（悬停二维码时调用）。
        /// 活动会话期间（缓冲流存在或 UI 已认领）不轮换：直接返回当前码，二维码复用现有码渲染，避免截断进行中的会话；
        /// 无活动会话时才生成新码。返回配对码供 UI 生成二维码（内容 = FrontendBaseUrl + "/#/voice?code=" + code）。
        /// </summary>
        public async Task<string> ClaimNewCodeAsync()
        {
            lock (_gate)
            {
                // 活动会话期间复用当前码（不轮换、不重发声明），避免截断进行中的识别会话；
                // 断连等异常路径已清空 _activeCode，此时退化为生成新码（见下方）
                if ((_sessionStream != null || _uiClaimOwner != null) && !string.IsNullOrEmpty(_activeCode))
                {
                    Debug.WriteLine($"[MobileVoice] active session, reuse pair code: {_activeCode}");
                    return _activeCode;
                }
            }

            // 仅本地/测试调试用固定配对码（Constants.URLs.IsDev：ServerBaseUrl 指向 localhost/127.0.0.1），
            // 避免每次悬停都换码、需反复扫码；生产环境保持随机生成。字符取自已去掉 0/O/1/I/l 的大写字母数字集。
            var code = Constants.URLs.IsDev ? DevFixedCode : GenerateCode(12);
            lock (_gate)
            {
                // 新配对码生效：旧会话流收尾作废（仅无活动会话时才走到这里）
                try { _sessionStream?.Complete(); } catch { }
                _sessionStream = null;
                _activeCode = code;
            }

            var msg = JsonSerializer.Serialize(new { @event = "voice_pair_claim", data = new { code } });
            await BackendWebSocketService.Instance.SendAsync(msg).ConfigureAwait(false);
            Debug.WriteLine($"[MobileVoice] pair code claimed: {code}");
            return code;
        }

        /// <summary>
        /// 识别启动失败时终结会话（契约：exe 可发 voice_stop 终结会话）：
        /// 发送 voice_stop 让后端销毁配对并通知手机会话结束，同时作废本地缓冲流，
        /// 避免其它窗口的滞后处理拿流半路启动识别。
        /// </summary>
        public async Task FailSessionAsync(string code)
        {
            lock (_gate)
            {
                try { _sessionStream?.Complete(); } catch { }
                _sessionStream = null;
            }
            var msg = JsonSerializer.Serialize(new { @event = "voice_stop", data = new { code } });
            await BackendWebSocketService.Instance.SendAsync(msg).ConfigureAwait(false);
            Debug.WriteLine($"[MobileVoice] start failed, voice_stop sent to terminate session: {code}");
        }

        /// <summary>多主窗口去重（带归属）：同一会话仅首个窗口认领成功返回 true，记录 owner</summary>
        public bool TryClaimUi(object owner)
        {
            lock (_gate)
            {
                if (_uiClaimOwner != null) return false;
                _uiClaimOwner = owner;
                return true;
            }
        }
        /// <summary>当前会话是否由指定 owner 认领</summary>
        public bool IsUiClaimedBy(object owner)
        {
            lock (_gate) return ReferenceEquals(_uiClaimOwner, owner);
        }
        /// <summary>当前会话是否已有窗口认领</summary>
        public bool IsUiClaimed
        {
            get { lock (_gate) return _uiClaimOwner != null; }
        }
        /// <summary>释放认领（校验归属匹配才清零；会话结束/认领窗口启动失败时调用）</summary>
        public void ReleaseUiClaim(object owner)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_uiClaimOwner, owner)) _uiClaimOwner = null;
            }
        }
        /// <summary>是否存在活动会话（缓冲流存在或 UI 已认领；悬停时据此决定是否轮换配对码）</summary>
        public bool HasActiveSession
        {
            get { lock (_gate) return _sessionStream != null || _uiClaimOwner != null; }
        }

        // ==================== 后端消息分发 ====================

        private void OnBackendMessage(string text)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (!doc.RootElement.TryGetProperty("event", out var evEl)) return;
                var ev = evEl.GetString();
                switch (ev)
                {
                    case "voice_phone_joined":
                        HandlePhoneJoined(ReadCode(doc));
                        break;
                    case "voice_chunk":
                        HandleChunk(doc);
                        break;
                    case "voice_stop":
                        HandleStop(ReadCode(doc));
                        break;
                    case "voice_session_closed":
                        HandleSessionClosed(ReadCode(doc));
                        break;
                    // 其它消息：忽略
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MobileVoice] message parse error (ignored): {ex.Message}");
            }
        }

        private static string? ReadCode(JsonDocument doc)
        {
            return doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("code", out var codeEl)
                ? codeEl.GetString()
                : null;
        }

        private void HandlePhoneJoined(string? code)
        {
            if (string.IsNullOrEmpty(code) || !IsActiveCode(code)) return;

            BlockingAudioStream stream;
            lock (_gate)
            {
                // 配对成功：新建本次会话的缓冲流（识别引擎后台线程持续阻塞读，流关闭即识别收尾）
                stream = new BlockingAudioStream();
                _sessionStream = stream;
                _uiClaimOwner = null; // 新会话重置认领归属，由窗口重新竞争认领
            }
            Debug.WriteLine($"[MobileVoice] phone joined, session started: {code}");
            try { SessionStarted?.Invoke(code); }
            catch (Exception ex) { Debug.WriteLine($"[MobileVoice] SessionStarted handler error (swallowed): {ex.Message}"); }
        }

        private void HandleChunk(JsonDocument doc)
        {
            if (!doc.RootElement.TryGetProperty("data", out var data)) return;
            var code = data.TryGetProperty("code", out var codeEl) ? codeEl.GetString() : null;
            if (string.IsNullOrEmpty(code) || !IsActiveCode(code)) return;
            var pcmB64 = data.TryGetProperty("pcmB64", out var pcmEl) ? pcmEl.GetString() : null;
            if (string.IsNullOrEmpty(pcmB64)) return;

            try
            {
                var pcm = Convert.FromBase64String(pcmB64);
                BlockingAudioStream? stream;
                lock (_gate) stream = _sessionStream;
                stream?.Write(pcm);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MobileVoice] chunk decode/write failed (ignored): {ex.Message}");
            }
        }

        private void HandleStop(string? code)
        {
            if (string.IsNullOrEmpty(code) || !IsActiveCode(code)) return;
            Debug.WriteLine($"[MobileVoice] voice_stop received: {code}");

            // 关闭缓冲流写入端：识别引擎读到流尾自然收尾（UI 层同时会 RecognizeAsyncStop 确保收尾）
            lock (_gate) { try { _sessionStream?.Complete(); } catch { } }
            try { SessionStopRequested?.Invoke(); }
            catch (Exception ex) { Debug.WriteLine($"[MobileVoice] SessionStopRequested handler error (swallowed): {ex.Message}"); }
        }

        private void HandleSessionClosed(string? code)
        {
            if (string.IsNullOrEmpty(code) || !IsActiveCode(code)) return;
            Debug.WriteLine($"[MobileVoice] voice_session_closed received: {code}");

            lock (_gate)
            {
                try { _sessionStream?.Complete(); } catch { }
                _sessionStream = null;
            }
            try { SessionAborted?.Invoke(); }
            catch (Exception ex) { Debug.WriteLine($"[MobileVoice] SessionAborted handler error (swallowed): {ex.Message}"); }
        }

        private bool IsActiveCode(string code)
        {
            lock (_gate) return string.Equals(_activeCode, code, StringComparison.Ordinal);
        }

        // ==================== 配对码生成 ====================

        /// <summary>本地/测试环境固定配对码（12 位、大写字母数字、不含易混淆字符），仅 IsDev 时使用，生产随机生成</summary>
        private const string DevFixedCode = "TESTDEV23456";

        /// <summary>生成指定长度的随机配对码（去除易混淆字符 0/O/1/I/l）</summary>
        private static string GenerateCode(int length)
        {
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789abcdefghjkmnpqrstuvwxyz";
            var chars = new char[length];
            for (int i = 0; i < length; i++)
                chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            return new string(chars);
        }

        // ==================== 阻塞音频缓冲流 ====================

        /// <summary>
        /// 线程安全、可阻塞读的音频缓冲流：写入端收 voice_chunk 分片，
        /// 读取端由 SpeechRecognitionEngine 后台线程持续阻塞读，直到 <see cref="Complete"/> 后数据耗尽返回 0（识别收尾）。
        /// </summary>
        private sealed class BlockingAudioStream : Stream
        {
            private readonly BlockingCollection<byte[]> _chunks = new();
            private byte[]? _current;
            private int _offset;

            /// <summary>写入一段 PCM 分片（会话结束后静默丢弃）</summary>
            public void Write(byte[] data)
            {
                if (data.Length == 0) return;
                try
                {
                    if (!_chunks.IsAddingCompleted) _chunks.Add(data);
                }
                catch (InvalidOperationException) { /* 已 CompleteAdding：丢弃 */ }
            }

            /// <summary>关闭写入端：读取端耗尽剩余数据后返回 0</summary>
            public void Complete()
            {
                try { _chunks.CompleteAdding(); } catch { }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (count == 0) return 0; // 防御：零长度读直接返回，避免下方循环以 written==0 死等
                int written = 0;
                while (written == 0)
                {
                    if (_current == null)
                    {
                        try
                        {
                            // 阻塞等待分片；会话结束且数据耗尽时 Take 抛 InvalidOperationException → 返回 0
                            if (!_chunks.TryTake(out _current, -1)) return 0;
                        }
                        catch (InvalidOperationException) { return 0; }
                        catch (OperationCanceledException) { return 0; }
                        if (_current == null) return 0;
                        _offset = 0;
                    }
                    int n = Math.Min(count - written, _current.Length - _offset);
                    Buffer.BlockCopy(_current, _offset, buffer, offset + written, n);
                    _offset += n;
                    written += n;
                    if (_offset >= _current.Length) _current = null;
                }
                return written;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
