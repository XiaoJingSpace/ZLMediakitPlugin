using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Serialization;
using ZLMediakitPlugin.Models;
using ZLMediakitPlugin.SIP;
using ZLMediakitPlugin.WebRTC;

namespace ZLMediakitPlugin.Core
{
    /// <summary>
    /// 插件总入口：协调 SIP 端口协商与 WebRTC 推流。
    /// </summary>
    public sealed class ZLMediakitPluginManager : MonoBehaviour
    {
        private static ZLMediakitPluginManager instance;
        public static ZLMediakitPluginManager Instance => instance;
        [Header("厂家")]
        public string Manufacturer = "NOLO";
        [Header("设备类型")]
        public string Model = "ARIPC";
        [Header("安装地址")]
        public string Address = "ECOENV";

        [Header("SIP (WVP PRO)")]
        [SerializeField] private string sipServerAddress = "127.0.0.1";
        [SerializeField] private int sipPort = 5060;
        [SerializeField] private string sipDomain = "4101050000";
        [SerializeField] private string sipServerId = "41010500002000000001";
        [SerializeField] private string sipPassword = string.Empty;
        [SerializeField] private int localSipPort = 0;
        [SerializeField] private string localDeviceId = "34020000001320000001";
        [SerializeField] private string localDeviceName = "Unity IPC Camera";
        [SerializeField] private bool registerOnStartup = true;
        [SerializeField] private bool activeInviteOnStart = true;
        [Tooltip("SIP Keepalive 间隔（秒），默认 10")]
        [SerializeField] private int keepaliveIntervalSeconds = 10;
        [Tooltip("应用退出时在后台发送 REGISTER(Expires=0) 注销的最大等待时间（毫秒），含 Digest 二次报文；过短可能导致平台未受理")]
        [SerializeField] private int sipUnregisterWaitMsOnQuit = 2000;
        [Header("WVP HTTP Trigger")]
        [SerializeField] private bool triggerInviteViaWvpApi = true;
        [SerializeField] private string wvpPlayApiTemplate = "http://127.0.0.1:18080/api/play/start/{deviceId}/{channelId}";
        [SerializeField] private int waitInviteTimeoutMs = 30000;
        [Header("WVP HTTP Auth")]
        [SerializeField] private bool wvpAutoLoginBeforePlay = false;
        [SerializeField] private string wvpLoginApi = "http://127.0.0.1:18080/api/user/login";
        [SerializeField] private string wvpLoginUsername = "admin";
        [SerializeField] private string wvpLoginPassword = "admin";
         private string wvpBearerToken = string.Empty;
          private string wvpCookie = string.Empty;
         private string wvpExtraHeaderName = string.Empty;
        private string wvpExtraHeaderValue = string.Empty;
        [SerializeField] private int wvpHttpTimeoutSeconds = 15;
        [SerializeField] private string wvpPushKey = string.Empty;

        [Header("ZLMediaKit")]
        [SerializeField] private string zlmWebRtcApi = "http://127.0.0.1:8080/index/api/webrtc";
        [SerializeField] private string zlmApp = "live";
        [SerializeField] private string zlmVhost = "__defaultVhost__";
        [SerializeField] private string zlmApiSecret = string.Empty;

        
        [Tooltip("留空则从 zlmWebRtcApi 推导为同主机同端口下的 /index/api/getMediaList")]
        [SerializeField] private string zlmGetMediaListUrlOverride = string.Empty;
        [Tooltip("WebRTC 推流成功后调用 getMediaList 并打印原始 JSON（需配置 zlmApiSecret）")]
        [SerializeField] private bool zlmQueryGetMediaListAfterPublish = true;
        [Tooltip("推流成功后再等待若干毫秒再请求 getMediaList，避免 ZLM 尚未登记流导致 data 为空")]
        [SerializeField] private int zlmGetMediaListDelayMs = 300;

        [Header("ZLM 拉流地址（推流成功时打印；各协议端口可不同）")]
        [Tooltip("为空则从 zlmWebRtcApi 解析 Host")]
        [SerializeField] private string zlmPullHostOverride = string.Empty;
        [Tooltip("HTTP：FLV/FMP4/HLS/TS 的 http:// 端口；0=与 zlmWebRtcApi 中端口一致（若 URI 无端口则 80/443）")]
        [FormerlySerializedAs("zlmPullHttpPortOverride")]
        [SerializeField] private int zlmPullHttpPlayPort = 0;
        [Tooltip("HTTPS：上述资源的 https:// 端口")]
        [FormerlySerializedAs("zlmPullHttpsPort")]
        [SerializeField] private int zlmPullHttpsPlayPort = 443;
        [Tooltip("WebSocket：ws:// 端口；0=与 HTTP 拉流端口相同")]
        [SerializeField] private int zlmPullWsPort = 0;
        [Tooltip("WebSocket：wss:// 端口；0=与 HTTPS 拉流端口相同")]
        [SerializeField] private int zlmPullWssPort = 0;
        [Tooltip("RTC：/index/api/webrtc 的 http:// 端口；0=使用 zlmWebRtcApi 解析出的端口（可与 HTTP 拉流端口不同）")]
        [SerializeField] private int zlmPullRtcHttpPort = 0;
        [Tooltip("RTC：/index/api/webrtc 的 https:// 端口；0=与 HTTPS 拉流端口相同")]
        [SerializeField] private int zlmPullRtcHttpsPort = 0;
        [SerializeField] private int zlmPullRtmpPort = 1935;
        [SerializeField] private int zlmPullRtspPort = 554;
        [Tooltip("拉流 URL 查询串，不含前导 ?")]
        [SerializeField] private string zlmPullStreamQuery = "originTypeStr=rtc_push&audioCodec=AAC&videoCodec=H264";

        public SIPClient sipClient;
        public ZLMediakitSender currentSender;
        private string currentDeviceId;
        private int allocatedPort;
        private readonly object autoStartLock = new object();
        private bool hasPendingAutoStart;
        private int pendingAutoStartPort;
        private string pendingAutoStartCallId = string.Empty;
        private string lastAutoStartedCallId = string.Empty;
        private bool isAutoRestartingForInvite;
        private Task quitUnregisterTask;

        /// <summary>WebRTC 异常断线时已触发 <see cref="OnStreamStopped"/>，避免随后 <see cref="StopStreaming"/> 再通知一次。</summary>
        private bool suppressNextStopStreamingStoppedEvent;

        public event Action<int> OnPortAllocated;
        public event Action<string> OnPortAllocationFailed;
        public event Action OnStreamStarted;
        public event Action<string> OnStreamFailed;
        public event Action OnStreamStopped;

        /// <summary>
        /// WebRTC 连接进入 Disconnected / Failed / Closed，且 <see cref="ZLMediakitSender"/> 内部已完成停止推流与 WebRTC 驱动重置后触发（参数为连接状态名）。
        /// </summary>
        public event Action<string> OnWebRtcDisconnected;
       
        [HideInInspector] public WebCameraIndex cameraIndex;
        [HideInInspector] public RenderTexture renderTexture;
        [HideInInspector] public AudioSource audioSource;
     


        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }

            instance = this;
            DontDestroyOnLoad(gameObject);
            Initialize();
        }

        private void Initialize()
        {
            sipClient = new SIPClient(
                sipServerAddress,
                sipPort,
                sipDomain,
                sipServerId,
                sipPassword,
                localSipPort,
                8000,
                3600,
                keepaliveIntervalSeconds,
                localDeviceName, Manufacturer, Model, Address);
            sipClient.OnPortAllocated += HandlePortAllocated;
            sipClient.OnPortAllocationFailed += HandlePortAllocationFailed;
            sipClient.OnInviteSessionReady += HandleInviteSessionReady;

            if (registerOnStartup)
            {
                _ = RegisterOnStartupAsync();
            }
        }

        private void Update()
        {
            if (!hasPendingAutoStart)
            {
                return;
            }

            int port;
            string callId;
            lock (autoStartLock)
            {
                if (!hasPendingAutoStart)
                {
                    return;
                }
                port = pendingAutoStartPort;
                callId = pendingAutoStartCallId;
                hasPendingAutoStart = false;
            }

            if (!string.IsNullOrWhiteSpace(callId) && string.Equals(lastAutoStartedCallId, callId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (isAutoRestartingForInvite)
            {
                Debug.Log($"[ZLMediakitPluginManager] 正在处理上一轮 INVITE 重启，暂缓本次自动启动，callId={callId}, port={port}");
                return;
            }

            _ = RestartStreamingForInviteAsync(port, callId);
        }

        private void HandleInviteSessionReady(int port, string callId)
        {
            lock (autoStartLock)
            {
                hasPendingAutoStart = true;
                pendingAutoStartPort = port;
                pendingAutoStartCallId = callId ?? string.Empty;
            }
        }

        private async Task RestartStreamingForInviteAsync(int port, string callId)
        {
            isAutoRestartingForInvite = true;
            try
            {
                if (currentSender != null)
                {
                    Debug.Log($"[ZLMediakitPluginManager] 收到新的 INVITE，会先关闭旧推流再重启，newCallId={callId}, port={port}");
                    UnsubscribeWebRtcSenderEvents(currentSender);
                    await currentSender.StopPublishing();
                    currentSender.Dispose();
                    currentSender = null;
                }

                lastAutoStartedCallId = callId;
                allocatedPort = port;
                if (string.IsNullOrWhiteSpace(currentDeviceId))
                {
                    currentDeviceId = localDeviceId;
                }

                Debug.Log($"[ZLMediakitPluginManager] INVITE->ACK 已完成，自动启动 WebRTC 推流，callId={callId}, port={port}");
                await StartWebRTCStreaming(cameraIndex,renderTexture,audioSource);
            }
            finally
            {
                isAutoRestartingForInvite = false;
            }
        }

        private async Task RegisterOnStartupAsync()
        {
            if (string.IsNullOrWhiteSpace(localDeviceId))
            {
                Debug.LogWarning("[ZLMediakitPluginManager] registerOnStartup 已启用，但 localDeviceId 为空");
                return;
            }

            bool ok = await sipClient.RegisterDevice(localDeviceId);
            if (ok)
            {
                Debug.Log($"[ZLMediakitPluginManager] 设备已完成启动注册: {localDeviceId}");
                if (wvpAutoLoginBeforePlay)
                {
                    bool loginOk = await EnsureWvpLoginAsync();
                    if (loginOk)
                    {
                        Debug.Log("[ZLMediakitPluginManager] 设备注册成功后已完成 WVP 登录");
                    }
                    else
                    {
                        Debug.LogWarning("[ZLMediakitPluginManager] 设备注册成功，但 WVP 登录失败");
                    }
                }
            }
            else
            {
                Debug.LogError($"[ZLMediakitPluginManager] 设备启动注册失败: {localDeviceId}");
            }
        }

        public async Task<bool> StartStreaming(
            string deviceId,
            WebCameraIndex cameraIndex = null,
            RenderTexture renderTexture = null,
            AudioSource audioSource = null)
        {
            Debug.Log($"[ZLMediakitPluginManager] StartStreaming 被调用，deviceId={deviceId}");
            sipClient?.ResetInboundInviteState();
            currentDeviceId = deviceId;
            allocatedPort = 0;

            int port;
            if (activeInviteOnStart)
            {
                Debug.Log("[ZLMediakitPluginManager] 触发开始推流流程（由按钮发起）...");
                Task<bool> triggerTask = null;
                if (triggerInviteViaWvpApi)
                {
                    // 并行化：触发点播后不阻塞等待接口返回，立即进入 INVITE 端口等待。
                    triggerTask = TriggerWvpInviteAsync(deviceId, deviceId);
                    Debug.Log("[ZLMediakitPluginManager] WVP 点播请求已发起，开始并行等待平台 INVITE...");
                }
                else
                {
                    Debug.LogWarning("[ZLMediakitPluginManager] 未启用 WVP API 触发，将回退为主动 INVITE（可能被平台拒绝）");
                    bool ok = await sipClient.RequestPort(deviceId);
                    if (!ok)
                    {
                        Debug.LogWarning("[ZLMediakitPluginManager] 主动 INVITE 端口协商失败");
                        OnStreamFailed?.Invoke("主动 INVITE 端口协商失败");
                        return false;
                    }
                }

                Debug.Log("[ZLMediakitPluginManager] 等待平台下发 INVITE 端口...");
                port = await sipClient.WaitForInboundInvitePortAsync(waitInviteTimeoutMs);
                if (port <= 0)
                {
                    if (triggerTask != null)
                    {
                        bool triggerOk = await triggerTask;
                        if (!triggerOk)
                        {
                            OnStreamFailed?.Invoke("调用 WVP 点播接口失败");
                            return false;
                        }
                    }
                    Debug.LogWarning("[ZLMediakitPluginManager] 等待平台 INVITE 超时或未获取端口");
                    OnStreamFailed?.Invoke("等待平台 INVITE 超时或未获取端口");
                    return false;
                }

                if (triggerTask != null)
                {
                    _ = ObserveTriggerInviteResultAsync(triggerTask);
                }

                string inviteCallId = sipClient.GetLastInboundInviteCallId();
                if (!string.IsNullOrWhiteSpace(inviteCallId))
                {
                    Debug.Log($"[ZLMediakitPluginManager] 等待平台 ACK 后再启动 WebRTC，callId={inviteCallId}");
                    bool ackOk = await sipClient.WaitForInboundInviteAckAsync(inviteCallId, waitInviteTimeoutMs);
                    if (!ackOk)
                    {
                        OnStreamFailed?.Invoke("等待平台 ACK 超时");
                        return false;
                    }
                }
                else
                {
                    Debug.LogWarning("[ZLMediakitPluginManager] 未获取到 INVITE callId，跳过 ACK 等待");
                }
            }
            else
            {
                Debug.Log("[ZLMediakitPluginManager] 等待平台下发 INVITE 端口...");
                port = await sipClient.WaitForInboundInvitePortAsync(waitInviteTimeoutMs);
                if (port <= 0)
                {
                    Debug.LogWarning("[ZLMediakitPluginManager] 等待平台 INVITE 超时或未获取端口");
                    OnStreamFailed?.Invoke("等待平台 INVITE 超时或未获取端口");
                    return false;
                }
            }
            allocatedPort = port;
            Debug.Log($"[ZLMediakitPluginManager] 端口协商完成: {port}");
            Debug.Log($"[ZLMediakitPluginManager] 准备启动 WebRTC 推流, deviceId={currentDeviceId}, port={allocatedPort}");

            // 平台 INVITE 下发端口后启动 WebRTC 推流。
            return await StartWebRTCStreaming(cameraIndex, renderTexture, audioSource);
        }

        private async Task ObserveTriggerInviteResultAsync(Task<bool> triggerTask)
        {
            try
            {
                bool ok = await triggerTask;
                if (!ok)
                {
                    Debug.LogWarning("[ZLMediakitPluginManager] WVP 点播接口最终返回失败，但已继续按 INVITE 结果启动流程");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ZLMediakitPluginManager] WVP 点播接口异步结果异常: {ex.Message}");
            }
        }

        private async Task<bool> TriggerWvpInviteAsync(string deviceId, string channelId)
        {
            if (string.IsNullOrWhiteSpace(wvpPlayApiTemplate))
            {
                Debug.LogError("[ZLMediakitPluginManager] WVP 点播接口模板为空");
                return false;
            }

            string url = wvpPlayApiTemplate
                .Replace("{deviceId}", UnityWebRequest.EscapeURL(deviceId))
                .Replace("{channelId}", UnityWebRequest.EscapeURL(channelId));

            Debug.Log($"[ZLMediakitPluginManager] 调用 WVP 点播接口: {url}");
            if (wvpAutoLoginBeforePlay)
            {
                bool loginOk = await EnsureWvpLoginAsync();
                if (!loginOk)
                {
                    Debug.LogError("[ZLMediakitPluginManager] WVP 自动登录失败，取消点播触发");
                    return false;
                }
            }

            using var request = UnityWebRequest.Get(url);
            ApplyWvpAuthHeaders(request);
            var op = request.SendWebRequest();
            while (!op.isDone)
            {
                await Task.Yield();
            }

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[ZLMediakitPluginManager] WVP 点播接口调用失败: {request.error}, body={request.downloadHandler?.text}");
                return false;
            }

            Debug.Log($"[ZLMediakitPluginManager] WVP 点播接口返回: {request.downloadHandler?.text}");
            return true;
        }

        private async Task<bool> EnsureWvpLoginAsync()
        {
            if (string.IsNullOrWhiteSpace(wvpLoginApi))
            {
                Debug.LogError("[ZLMediakitPluginManager] wvpLoginApi 为空");
                return false;
            }

            if (!string.IsNullOrWhiteSpace(wvpBearerToken) || !string.IsNullOrWhiteSpace(wvpCookie))
            {
                Debug.Log("[ZLMediakitPluginManager] 已存在 WVP 登录态，跳过重复登录");
                return true;
            }

            Debug.Log($"[ZLMediakitPluginManager] 调用 WVP 登录接口: {wvpLoginApi}");
            string username = UnityWebRequest.EscapeURL(wvpLoginUsername ?? string.Empty);
            string passwordMd5 = ToMd5Lower32(wvpLoginPassword ?? string.Empty);
            string password = UnityWebRequest.EscapeURL(passwordMd5);
            string loginWithQuery = $"{wvpLoginApi}?username={username}&password={password}";
            return await TryLoginRequestAsync(() => UnityWebRequest.Get(loginWithQuery), "GET query");
        }

        private async Task<bool> TryLoginRequestAsync(Func<UnityWebRequest> requestFactory, string mode)
        {
            using var request = requestFactory.Invoke();
            request.timeout = Mathf.Clamp(wvpHttpTimeoutSeconds, 3, 120);
            Debug.Log($"[ZLMediakitPluginManager] 尝试登录模式: {mode}, url={request.url}");
            var op = request.SendWebRequest();
            float startedAt = Time.realtimeSinceStartup;
            while (!op.isDone)
            {
                if (Time.realtimeSinceStartup - startedAt > request.timeout + 2)
                {
                    Debug.LogWarning($"[ZLMediakitPluginManager] 登录请求等待超时({mode})，主动中断");
                    request.Abort();
                    break;
                }
                await Task.Yield();
            }

            Debug.Log($"[ZLMediakitPluginManager] 登录请求已结束({mode})，http={request.responseCode}, result={request.result}, error={request.error}");

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[ZLMediakitPluginManager] WVP 登录尝试失败({mode}): code={request.responseCode}, error={request.error}, body={request.downloadHandler?.text}");
                return false;
            }

            string setCookie = request.GetResponseHeader("Set-Cookie");
            if (!string.IsNullOrWhiteSpace(setCookie))
            {
                int split = setCookie.IndexOf(';');
                wvpCookie = split > 0 ? setCookie.Substring(0, split) : setCookie;
                Debug.Log($"[ZLMediakitPluginManager] 登录获取 Cookie({mode}): {wvpCookie}");
            }

            string body = request.downloadHandler?.text ?? string.Empty;
            string token = TryExtractJsonToken(body);
            if (!string.IsNullOrWhiteSpace(token))
            {
                wvpBearerToken = token;
                Debug.Log($"[ZLMediakitPluginManager] 登录获取 Bearer Token({mode})");
            }

            LoginApiResponse parsed = TryParseLoginResponse(body);
            bool hasCode = parsed != null;
            bool codeSuccess = parsed != null && parsed.code == 0;
            bool cookieSuccess = !string.IsNullOrWhiteSpace(wvpCookie);
            if (parsed?.data != null && !string.IsNullOrWhiteSpace(parsed.data.pushKey))
            {
                if (string.IsNullOrWhiteSpace(wvpPushKey))
                {
                    wvpPushKey = parsed.data.pushKey;
                    Debug.Log("[ZLMediakitPluginManager] 登录获取 pushKey 并写入 wvpPushKey");
                }
                else
                {
                    Debug.Log("[ZLMediakitPluginManager] 已配置 wvpPushKey，忽略登录返回 pushKey");
                }
            }

            // If API returns a business code, it is authoritative.
            // Only treat as success when code == 0.
            if (hasCode)
            {
                if (codeSuccess)
                {
                    Debug.Log($"[ZLMediakitPluginManager] WVP 登录成功({mode}) code={parsed.code}, msg={parsed.msg}, cookie={(cookieSuccess ? "yes" : "no")}, body={body}");
                    return true;
                }

                Debug.LogError($"[ZLMediakitPluginManager] WVP 登录业务失败({mode}): code={parsed.code}, msg={parsed.msg}, body={body}");
                return false;
            }

            // Some deployments may return non-standard bodies without code.
            if (cookieSuccess || !string.IsNullOrWhiteSpace(wvpBearerToken))
            {
                Debug.LogWarning($"[ZLMediakitPluginManager] WVP 登录响应无code，按会话信息判成功({mode}): cookie={(cookieSuccess ? "yes" : "no")}, token={(string.IsNullOrWhiteSpace(wvpBearerToken) ? "no" : "yes")}, body={body}");
                return true;
            }

            Debug.LogError($"[ZLMediakitPluginManager] WVP 登录返回失败({mode}): http={request.responseCode}, body={body}");
            return false;
        }

        private void ApplyWvpAuthHeaders(UnityWebRequest request)
        {
            if (request == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(wvpBearerToken))
            {
                //兼容不同后端网关的 token 读取方式
                request.SetRequestHeader("Authorization", $"Bearer {wvpBearerToken}");
                request.SetRequestHeader("Authorization-Token", wvpBearerToken);
                request.SetRequestHeader("X-Access-Token", wvpBearerToken);
                request.SetRequestHeader("Access-Token", wvpBearerToken);
                request.SetRequestHeader("accessToken", wvpBearerToken);
                request.SetRequestHeader("token", wvpBearerToken);
            }

            if (!string.IsNullOrWhiteSpace(wvpCookie))
            {
                request.SetRequestHeader("Cookie", wvpCookie);
            }

            if (!string.IsNullOrWhiteSpace(wvpExtraHeaderName) && !string.IsNullOrWhiteSpace(wvpExtraHeaderValue))
            {
                request.SetRequestHeader(wvpExtraHeaderName, wvpExtraHeaderValue);
            }

            Debug.Log($"[ZLMediakitPluginManager] 已附加鉴权头: token={(string.IsNullOrWhiteSpace(wvpBearerToken) ? "no" : "yes")}, cookie={(string.IsNullOrWhiteSpace(wvpCookie) ? "no" : "yes")}");
        }

        private static string TryExtractJsonToken(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return string.Empty;
            }

            string[] keys = { "token", "access_token", "accessToken", "data" };
            foreach (string key in keys)
            {
                string pattern = $"\"{key}\"\\s*:\\s*\"([^\"]+)\"";
                var match = System.Text.RegularExpressions.Regex.Match(json, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }

            return string.Empty;
        }

        private static LoginApiResponse TryParseLoginResponse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return JsonUtility.FromJson<LoginApiResponse>(json);
            }
            catch
            {
                return null;
            }
        }

        private static string ToMd5Lower32(string input)
        {
            if (IsLowerHex32(input))
            {
                return input;
            }

            using var md5 = MD5.Create();
            byte[] bytes = Encoding.UTF8.GetBytes(input ?? string.Empty);
            byte[] hash = md5.ComputeHash(bytes);
            var sb = new StringBuilder(32);
            foreach (byte b in hash)
            {
                sb.Append(b.ToString("x2"));
            }
            return sb.ToString();
        }

        private static string ToMd5AlwaysLower32(string input)
        {
            using var md5 = MD5.Create();
            byte[] bytes = Encoding.UTF8.GetBytes(input ?? string.Empty);
            byte[] hash = md5.ComputeHash(bytes);
            var sb = new StringBuilder(32);
            foreach (byte b in hash)
            {
                sb.Append(b.ToString("x2"));
            }
            return sb.ToString();
        }

        private static bool IsLowerHex32(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 32)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }

        [Serializable]
        private class LoginApiResponse
        {
            public int code;
            public string msg;
            public LoginApiData data;
        }

        [Serializable]
        private class LoginApiData
        {
            public string accessToken;
            public string pushKey;
        }

        public async Task<bool> StartWebRTCStreaming(
            WebCameraIndex cameraIndex = null,
            RenderTexture renderTexture = null,
            AudioSource audioSource = null)
        {
            if (allocatedPort <= 0)
            {
                OnStreamFailed?.Invoke("端口未就绪");
                return false;
            }

            string inviteCallId = sipClient != null ? sipClient.GetLastInboundInviteCallId() : string.Empty;
            // WVP 点播会话默认按 deviceId_channelId 组织流 ID，鉴权也通常基于该流名匹配。
            // 这里按当前调用约定（channelId 与 deviceId 相同）使用 deviceId_deviceId。
            string streamId = $"{currentDeviceId}_{currentDeviceId}";
            bool started;
            string zlmPushSign = string.IsNullOrWhiteSpace(wvpPushKey) ? string.Empty : ToMd5AlwaysLower32(wvpPushKey);
            if (currentSender != null)
            {
                UnsubscribeWebRtcSenderEvents(currentSender);
                currentSender.Dispose();
            }

            currentSender = new ZLMediakitSender(zlmWebRtcApi, zlmApp, zlmVhost, zlmPushSign, string.Empty, zlmApiSecret);
            currentSender.SetCoroutineHost(this);//当合并其他模块一起工作的时候设置webRtcUpdate=false,保证只有一个WebRTC.Update()
            currentSender.OnError += HandleWebRtcSenderError;
            currentSender.OnDisconnected += HandleWebRtcSenderDisconnected;

            // 自动触发场景（INVITE->ACK）通常不会携带 UI 选择的采集源；兜底使用默认摄像头索引 0。
            if (cameraIndex == null && renderTexture == null)
            {
                cameraIndex = new WebCameraIndex { Index = 0 };
                Debug.Log("[ZLMediakitPluginManager] 未指定视频源，自动回退到默认摄像头(Index=0)");
            }

            if (cameraIndex != null)
            {
                string resolvedDeviceName = cameraIndex.ResolveDeviceName();
                Debug.Log($"[ZLMediakitPluginManager] 绑定摄像头，index={cameraIndex.Index}, device={resolvedDeviceName}");
                currentSender.BindCamera(cameraIndex);
            }
            else if (renderTexture != null)
            {
                Debug.Log("[ZLMediakitPluginManager] 绑定 RenderTexture 作为视频源");
                currentSender.BindRenderTexture(renderTexture);
            }

            if (audioSource != null)
            {
                try
                {
                    AudioListener listenerOnSameObject = audioSource.GetComponent<AudioListener>();
                    if (listenerOnSameObject != null)
                    {
                        Debug.LogWarning("[ZLMediakitPluginManager] 检测到 AudioSource 与 AudioListener 在同一对象，已跳过音频轨绑定，改为仅视频推流");
                    }
                    else
                    {
                        currentSender.BindAudioSource(audioSource);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ZLMediakitPluginManager] 音频轨绑定失败，已降级为仅视频推流: {ex.Message}");
                }
            }

            Debug.Log($"[ZLMediakitPluginManager] 开始 WebRTC Publish, streamId={streamId}, sign={(string.IsNullOrWhiteSpace(zlmPushSign) ? "no" : "yes")} (md5(wvpPushKey)), callId=no, secret={(string.IsNullOrWhiteSpace(zlmApiSecret) ? "no" : "yes")}");
            started = await currentSender.StartPublishing(streamId, allocatedPort);
            if (started)
            {
                suppressNextStopStreamingStoppedEvent = false;
                Debug.Log($"[ZLMediakitPluginManager] WebRTC Publish 启动成功，streamId={streamId}");
                LogZlmPullPlaybackUrls(streamId);
                if (zlmQueryGetMediaListAfterPublish)
                {
                    int delayMs = Mathf.Max(0, zlmGetMediaListDelayMs);
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs);
                    }

                    string mediaListJson = await QueryZlmGetMediaListAsync(zlmApp, streamId, zlmVhost);
                    if (!string.IsNullOrEmpty(mediaListJson))
                    {
                        DebugLogLong("[ZLMediakitPluginManager] ZLM getMediaList 原始应答", mediaListJson);
                        if (TryResolveZlmPullPorts(out ZlmPullPorts pullPorts)
                            && ZlmGetMediaListParser.TryBuildWebrtcPlayUrlsFromGetMediaListJson(
                                mediaListJson,
                                pullPorts.Host,
                                pullPorts.RtcHttp,
                                pullPorts.RtcHttps,
                                out string rtcPlayHttp,
                                out string rtcPlayHttps,
                                out string rtcDetail))
                        {
                            Debug.Log(
                                "[ZLMediakitPluginManager] 由 getMediaList.data 推导的 WebRTC 拉流（type=play）\n"
                                + $"RTC:{rtcPlayHttp}\nRTCS:{rtcPlayHttps}\n{rtcDetail}");
                        }
                    }
                }

                OnStreamStarted?.Invoke();
            }
            else
            {
                suppressNextStopStreamingStoppedEvent = false;
                Debug.LogError("[ZLMediakitPluginManager] WebRTC Publish 启动失败");
                OnStreamFailed?.Invoke("WebRTC 推流启动失败");
            }

            return started;
        }

        public async Task StopStreaming()
        {
            if (currentSender != null)
            {
                UnsubscribeWebRtcSenderEvents(currentSender);
                await currentSender.StopPublishing();
                currentSender.Dispose();
                currentSender = null;
            }

            if (sipClient != null && !string.IsNullOrWhiteSpace(currentDeviceId))
            {
                await sipClient.ReleasePort(currentDeviceId);
            }

            currentDeviceId = string.Empty;
            allocatedPort = 0;
            if (!suppressNextStopStreamingStoppedEvent)
            {
                OnStreamStopped?.Invoke();
            }

            suppressNextStopStreamingStoppedEvent = false;
        }

        /// <summary>
        /// 调用 ZLM <c>/index/api/getMediaList</c>（需 <see cref="zlmApiSecret"/>）。
        /// 返回原始 JSON 文本；失败时返回 null 并打日志。
        /// </summary>
        /// <param name="app">筛选 app，null 则用 Inspector 中的 zlmApp。</param>
        /// <param name="stream">筛选 stream，null 则不传 stream 参数（列出全部匹配项）。</param>
        /// <param name="vhost">筛选 vhost，null 则用 zlmVhost。</param>
        public async Task<string> QueryZlmGetMediaListAsync(string app = null, string stream = null, string vhost = null)
        {
            if (string.IsNullOrWhiteSpace(zlmApiSecret))
            {
                Debug.LogWarning("[ZLMediakitPluginManager] getMediaList 已跳过：未配置 zlmApiSecret");
                return null;
            }

            if (!TryBuildZlmGetMediaListBaseUrl(out string listUrl))
            {
                Debug.LogWarning("[ZLMediakitPluginManager] getMediaList 已跳过：无法从 zlmWebRtcApi 推导接口地址（可填写 zlmGetMediaListUrlOverride）");
                return null;
            }

            var qs = new StringBuilder(256);
            qs.Append("secret=").Append(UnityWebRequest.EscapeURL(zlmApiSecret.Trim()));
            string v = vhost ?? zlmVhost;
            if (!string.IsNullOrWhiteSpace(v))
            {
                qs.Append("&vhost=").Append(UnityWebRequest.EscapeURL(v.Trim()));
            }

            string a = app ?? zlmApp;
            if (!string.IsNullOrWhiteSpace(a))
            {
                qs.Append("&app=").Append(UnityWebRequest.EscapeURL(a.Trim()));
            }

            if (!string.IsNullOrWhiteSpace(stream))
            {
                qs.Append("&stream=").Append(UnityWebRequest.EscapeURL(stream.Trim()));
            }

            string url = $"{listUrl}?{qs}";
            string queryForLog = qs.ToString();
            int secretIdx = queryForLog.IndexOf("secret=", StringComparison.Ordinal);
            if (secretIdx >= 0)
            {
                int amp = queryForLog.IndexOf('&', secretIdx + 7);
                queryForLog = amp > 0
                    ? queryForLog.Substring(0, secretIdx + 7) + "***" + queryForLog.Substring(amp)
                    : queryForLog.Substring(0, secretIdx + 7) + "***";
            }

            Debug.Log($"[ZLMediakitPluginManager] ZLM getMediaList GET {listUrl}?{queryForLog}");

            using var request = UnityWebRequest.Get(url);
            request.timeout = Mathf.Clamp(wvpHttpTimeoutSeconds, 3, 120);
            var op = request.SendWebRequest();
            while (!op.isDone)
            {
                await Task.Yield();
            }

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[ZLMediakitPluginManager] getMediaList 请求失败: {request.error}, code={request.responseCode}, body={request.downloadHandler?.text}");
                return null;
            }

            string body = request.downloadHandler?.text ?? string.Empty;
            int rawLen = request.downloadHandler?.data?.Length ?? 0;
            Debug.Log($"[ZLMediakitPluginManager] getMediaList 收到应答: http={request.responseCode}, rawBytes={rawLen}, textChars={body.Length}（Unity 单条 Log 过长会截断，已用分片打印）");
            return body;
        }

        /// <summary>
        /// Unity <see cref="Debug.Log"/> 对单条字符串长度有限制，长 JSON 会被截断；按块输出完整内容。
        /// </summary>
        private static void DebugLogLong(string prefix, string text)
        {
            if (text == null)
            {
                Debug.Log($"{prefix}\n(null)");
                return;
            }

            if (text.Length == 0)
            {
                Debug.Log($"{prefix}\n(empty)");
                return;
            }

            const int maxChunk = 12000;
            int len = text.Length;
            if (len <= maxChunk)
            {
                Debug.Log($"{prefix}\n{text}");
                return;
            }

            int parts = (len + maxChunk - 1) / maxChunk;
            for (int i = 0; i < parts; i++)
            {
                int start = i * maxChunk;
                int count = Math.Min(maxChunk, len - start);
                Debug.Log($"{prefix} 分片 {i + 1}/{parts}（总字符 {len}）\n{text.Substring(start, count)}");
            }
        }

        private bool TryBuildZlmGetMediaListBaseUrl(out string listUrl)
        {
            listUrl = string.Empty;
            if (!string.IsNullOrWhiteSpace(zlmGetMediaListUrlOverride))
            {
                string trimmed = zlmGetMediaListUrlOverride.Trim();
                string candidate = trimmed.Split('?')[0].TrimEnd('/');
                if (Uri.TryCreate(candidate, UriKind.Absolute, out _))
                {
                    listUrl = candidate;
                    return true;
                }

                listUrl = string.Empty;
                return false;
            }

            if (!Uri.TryCreate(zlmWebRtcApi, UriKind.Absolute, out Uri u))
            {
                return false;
            }

            var builder = new UriBuilder(u.Scheme, u.Host, u.IsDefaultPort ? -1 : u.Port, "/index/api/getMediaList");
            listUrl = builder.Uri.ToString();
            if (listUrl.EndsWith("/", StringComparison.Ordinal))
            {
                listUrl = listUrl.TrimEnd('/');
            }

            return true;
        }

        private readonly struct ZlmPullPorts
        {
            public readonly string Host;
            public readonly int HttpPlay;
            public readonly int HttpsPlay;
            public readonly int Ws;
            public readonly int Wss;
            public readonly int RtcHttp;
            public readonly int RtcHttps;
            public readonly int Rtmp;
            public readonly int Rtsp;

            public ZlmPullPorts(
                string host,
                int httpPlay,
                int httpsPlay,
                int ws,
                int wss,
                int rtcHttp,
                int rtcHttps,
                int rtmp,
                int rtsp)
            {
                Host = host;
                HttpPlay = httpPlay;
                HttpsPlay = httpsPlay;
                Ws = ws;
                Wss = wss;
                RtcHttp = rtcHttp;
                RtcHttps = rtcHttps;
                Rtmp = rtmp;
                Rtsp = rtsp;
            }
        }

        private bool TryResolveZlmPullPorts(out ZlmPullPorts ports)
        {
            ports = default;
            string host = !string.IsNullOrWhiteSpace(zlmPullHostOverride) ? zlmPullHostOverride.Trim() : string.Empty;
            int apiPortFromUri = 0;
            bool uriOk = Uri.TryCreate(zlmWebRtcApi, UriKind.Absolute, out Uri uri);
            if (uriOk)
            {
                if (string.IsNullOrEmpty(host))
                {
                    host = uri.Host;
                }

                apiPortFromUri = uri.Port > 0
                    ? uri.Port
                    : (string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);
            }
            else if (string.IsNullOrEmpty(host))
            {
                return false;
            }

            int defaultHttpPlay = uriOk ? apiPortFromUri : 80;
            int httpPlay = zlmPullHttpPlayPort > 0 ? zlmPullHttpPlayPort : defaultHttpPlay;
            int httpsPlay = zlmPullHttpsPlayPort > 0 ? zlmPullHttpsPlayPort : 443;
            int ws = zlmPullWsPort > 0 ? zlmPullWsPort : httpPlay;
            int wss = zlmPullWssPort > 0 ? zlmPullWssPort : httpsPlay;
            int rtcHttp = zlmPullRtcHttpPort > 0 ? zlmPullRtcHttpPort : (uriOk ? apiPortFromUri : httpPlay);
            int rtcHttps = zlmPullRtcHttpsPort > 0 ? zlmPullRtcHttpsPort : httpsPlay;
            int rtmp = zlmPullRtmpPort > 0 ? zlmPullRtmpPort : 1935;
            int rtsp = zlmPullRtspPort > 0 ? zlmPullRtspPort : 554;

            if (string.IsNullOrEmpty(host) || httpPlay <= 0 || httpsPlay <= 0 || ws <= 0 || wss <= 0 || rtcHttp <= 0 || rtcHttps <= 0)
            {
                return false;
            }

            ports = new ZlmPullPorts(host, httpPlay, httpsPlay, ws, wss, rtcHttp, rtcHttps, rtmp, rtsp);
            return true;
        }

        private void LogZlmPullPlaybackUrls(string streamId)
        {
            if (string.IsNullOrWhiteSpace(streamId))
            {
                return;
            }

            if (!TryResolveZlmPullPorts(out ZlmPullPorts p))
            {
                Debug.LogWarning("[ZLMediakitPluginManager] 无法解析拉流主机/端口，已跳过拉流地址打印（请检查 zlmWebRtcApi / zlmPullHostOverride / 各端口配置）");
                return;
            }

            string app = string.IsNullOrWhiteSpace(zlmApp) ? "live" : zlmApp.Trim();
            string q = string.IsNullOrWhiteSpace(zlmPullStreamQuery)
                ? "originTypeStr=rtc_push&audioCodec=AAC&videoCodec=H264"
                : zlmPullStreamQuery.Trim().TrimStart('?');
            string suffix = "?" + q;

            string httpPlayAuthority = $"{p.Host}:{p.HttpPlay}";
            string httpsPlayAuthority = $"{p.Host}:{p.HttpsPlay}";
            string wsAuthority = $"{p.Host}:{p.Ws}";
            string wssAuthority = $"{p.Host}:{p.Wss}";
            string rtcHttpAuthority = $"{p.Host}:{p.RtcHttp}";
            string rtcHttpsAuthority = $"{p.Host}:{p.RtcHttps}";
            string rtcQuery =
                $"app={Uri.EscapeDataString(app)}&stream={Uri.EscapeDataString(streamId)}&type=play&{q}";

            var sb = new StringBuilder(1200);
            sb.AppendLine($"[ZLMediakitPluginManager] ZLM 拉流地址（streamId={streamId}）");
            sb.Append("(端口摘要: HTTP=").Append(p.HttpPlay).Append(", HTTPS=").Append(p.HttpsPlay)
                .Append(", WS=").Append(p.Ws).Append(", WSS=").Append(p.Wss)
                .Append(", RTC_http=").Append(p.RtcHttp).Append(", RTC_https=").Append(p.RtcHttps)
                .Append(", RTMP=").Append(p.Rtmp).Append(", RTSP=").Append(p.Rtsp).AppendLine(")");
            sb.Append("FLV:http://").Append(httpPlayAuthority).Append('/').Append(app).Append('/').Append(streamId).Append(".live.flv").AppendLine(suffix);
            sb.Append("FLV(https):https://").Append(httpsPlayAuthority).Append('/').Append(app).Append('/').Append(streamId).Append(".live.flv").AppendLine(suffix);
            sb.Append("FLV(ws):ws://").Append(wsAuthority).Append('/').Append(app).Append('/').Append(streamId).Append(".live.flv").AppendLine(suffix);
            sb.Append("FLV(wss):wss://").Append(wssAuthority).Append('/').Append(app).Append('/').Append(streamId).Append(".live.flv").AppendLine(suffix);
            sb.Append("FMP4:http://").Append(httpPlayAuthority).Append('/').Append(app).Append('/').Append(streamId).Append(".live.mp4").AppendLine(suffix);
            sb.Append("FMP4(https):https://").Append(httpsPlayAuthority).Append('/').Append(app).Append('/').Append(streamId).Append(".live.mp4").AppendLine(suffix);
            sb.Append("FMP4(ws):ws://").Append(wsAuthority).Append('/').Append(app).Append('/').Append(streamId).Append(".live.mp4").AppendLine(suffix);
            sb.Append("FMP4(wss):wss://").Append(wssAuthority).Append('/').Append(app).Append('/').Append(streamId).Append(".live.mp4").AppendLine(suffix);
            sb.Append("HLS:http://").Append(httpPlayAuthority).Append('/').Append(app).Append('/').Append(streamId).Append("/hls.m3u8").AppendLine(suffix);
            sb.Append("HLS(https):https://").Append(httpsPlayAuthority).Append('/').Append(app).Append('/').Append(streamId).Append("/hls.m3u8").AppendLine(suffix);
            sb.Append("HLS(ws):ws://").Append(wsAuthority).Append('/').Append(app).Append('/').Append(streamId).Append("/hls.m3u8").AppendLine(suffix);
            sb.Append("HLS(wss):wss://").Append(wssAuthority).Append('/').Append(app).Append('/').Append(streamId).Append("/hls.m3u8").AppendLine(suffix);
            sb.Append("TS:http://").Append(httpPlayAuthority).Append('/').Append(app).Append('/').Append(streamId).Append(".live.ts").AppendLine(suffix);
            sb.Append("TS(https):https://").Append(httpsPlayAuthority).Append('/').Append(app).Append('/').Append(streamId).Append(".live.ts").AppendLine(suffix);
            sb.Append("TS(ws):ws://").Append(wsAuthority).Append('/').Append(app).Append('/').Append(streamId).Append(".live.ts").AppendLine(suffix);
            sb.Append("RTC:http://").Append(rtcHttpAuthority).Append("/index/api/webrtc?").AppendLine(rtcQuery);
            sb.Append("RTCS:https://").Append(rtcHttpsAuthority).Append("/index/api/webrtc?").AppendLine(rtcQuery);
            sb.Append("RTMP:rtmp://").Append(p.Host).Append(':').Append(p.Rtmp).Append('/').Append(app).Append('/').Append(streamId).AppendLine(suffix);
            sb.Append("RTSP:rtsp://").Append(p.Host).Append(':').Append(p.Rtsp).Append('/').Append(app).Append('/').Append(streamId).Append(suffix);

            Debug.Log(sb.ToString());
        }

        private void UnsubscribeWebRtcSenderEvents(ZLMediakitSender sender)
        {
            if (sender == null)
            {
                return;
            }

            sender.OnError -= HandleWebRtcSenderError;
            sender.OnDisconnected -= HandleWebRtcSenderDisconnected;
        }

        private void HandleWebRtcSenderError(string reason)
        {
            OnStreamFailed?.Invoke(reason);
        }

        private void HandleWebRtcSenderDisconnected(WebRTCSender source, string connectionState)
        {
            if (currentSender == null || !ReferenceEquals(currentSender, source))
            {
                return;
            }

            UnsubscribeWebRtcSenderEvents(currentSender);
            ZLMediakitSender sender = currentSender;
            currentSender = null;

            try
            {
                sender.Dispose();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ZLMediakitPluginManager] WebRTC 断开后 Dispose sender: {ex.Message}");
            }

            Debug.Log($"[ZLMediakitPluginManager] WebRTC 已断开 ({connectionState})，已解除 sender 引用");
            OnWebRtcDisconnected?.Invoke(connectionState);
            suppressNextStopStreamingStoppedEvent = true;
            OnStreamStopped?.Invoke();
        }

        private void HandlePortAllocated(int port)
        {
            allocatedPort = port;
            OnPortAllocated?.Invoke(port);
            Debug.Log($"[ZLMediakitPluginManager] WVP 分配端口: {port}");
        }

        private void HandlePortAllocationFailed(string reason)
        {
            OnPortAllocationFailed?.Invoke(reason);
            Debug.LogError($"[ZLMediakitPluginManager] 端口协商失败: {reason}");
        }

        private void OnDestroy()
        {
            if (quitUnregisterTask != null)
            {
                int waitMs = Mathf.Clamp(sipUnregisterWaitMsOnQuit + 400, 500, 8000);
                try
                {
                    if (!quitUnregisterTask.Wait(waitMs))
                    {
                        Debug.LogWarning("[ZLMediakitPluginManager] 退出 SIP 注销等待超时，将继续释放客户端");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ZLMediakitPluginManager] 等待退出 SIP 注销: {ex.Message}");
                }

                quitUnregisterTask = null;
            }

            if (sipClient != null)
            {
                sipClient.OnPortAllocated -= HandlePortAllocated;
                sipClient.OnPortAllocationFailed -= HandlePortAllocationFailed;
                sipClient.OnInviteSessionReady -= HandleInviteSessionReady;
                sipClient.Dispose();
                sipClient = null;
            }

            if (currentSender != null)
            {
                UnsubscribeWebRtcSenderEvents(currentSender);
                currentSender.Dispose();
                currentSender = null;
            }

            if (instance == this)
            {
                instance = null;
            }
        }

        private void OnApplicationQuit()
        {
            if (sipClient == null)
            {
                return;
            }

            int budgetMs = Mathf.Clamp(sipUnregisterWaitMsOnQuit, 500, 8000);
            quitUnregisterTask = Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(budgetMs);
                    bool ok = await sipClient.UnregisterDevice(cts.Token, waitResponse: true);
                    Debug.Log($"[ZLMediakitPluginManager] 应用退出 SIP 注销(Expires=0): {(ok ? "已发送并完成" : "未执行或失败")}");
                }
                catch (OperationCanceledException)
                {
                    Debug.LogWarning("[ZLMediakitPluginManager] 应用退出 SIP 注销已超时取消");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ZLMediakitPluginManager] 应用退出 SIP 注销异常: {ex.Message}");
                }
            });
        }
    }
}
