using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace ZLMediakitPlugin.SIP
{
    /// <summary>
    /// 轻量 SIP 客户端：面向 WVP PRO 的 INVITE + SDP 端口协商。
    /// </summary>
    public sealed class SIPClient : IDisposable
    {
        private readonly string serverAddress;
        private readonly int sipPort;
        private readonly string domain;
        private readonly string serverId;
        private readonly string sipPassword;
        private readonly int localSipPort;
        private readonly int requestTimeoutMs;
        private readonly int registerExpiresSeconds;
        private readonly int keepaliveIntervalSeconds;
        private readonly string deviceDisplayName;

        private UdpClient udpClient;
        private IPEndPoint remoteEndpoint;
        private long cseq = 1;
        private bool initialized;
        private bool isRegistered;
        private DateTime registerExpireAtUtc = DateTime.MinValue;
        private string registerFromTag;
        private string registeredDeviceId;
        private string activeDeviceId;
        private readonly SemaphoreSlim sipTransactionLock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource registerRefreshCts;
        private Task registerRefreshTask;
        private CancellationTokenSource keepaliveCts;
        private Task keepaliveTask;
        private int keepaliveSn = 1;
        private int keepaliveFailureCount;
        private CancellationTokenSource incomingLoopCts;
        private Task incomingLoopTask;
        private readonly object responseBufferLock = new object();
        private readonly List<string> responseBuffer = new List<string>();
        private readonly SemaphoreSlim responseSignal = new SemaphoreSlim(0, int.MaxValue);
        private int cachedLocalPort;
        private readonly object inviteWaitLock = new object();
        private TaskCompletionSource<int> pendingInvitePortTcs;
        private TaskCompletionSource<bool> pendingInviteAckTcs;
        private string pendingInviteAckExpectedCallId = string.Empty;
        private int lastInviteMediaPort;
        private string lastInviteCallId = string.Empty;
        private string lastInviteAckCallId = string.Empty;

        public event Action<int> OnPortAllocated;
        public event Action<string> OnPortAllocationFailed;
        public event Action<int, string> OnInviteSessionReady;

        private string Manufacturer;
        private string Model;
        private string Address;

        public SIPClient(
            string serverAddress,
            int sipPort,
            string domain,
            string serverId,
            string sipPassword = "",
            int localSipPort = 0,
            int requestTimeoutMs = 8000,
            int registerExpiresSeconds = 3600,
            int keepaliveIntervalSeconds = 10,
            string deviceDisplayName = "Unity IPC Camera",
            string manufacturer="", 
            string model="", 
            string address="")
        {
            this.serverAddress = serverAddress;
            this.sipPort = sipPort;
            this.domain = domain;
            this.serverId = serverId;
            this.sipPassword = sipPassword ?? string.Empty;
            this.localSipPort = Math.Max(0, localSipPort);
            this.requestTimeoutMs = Mathf.Clamp(requestTimeoutMs, 1000, 30000);
            this.registerExpiresSeconds = Mathf.Clamp(registerExpiresSeconds, 60, 86400);
            this.keepaliveIntervalSeconds = Mathf.Clamp(keepaliveIntervalSeconds, 5, 300);
            this.deviceDisplayName = string.IsNullOrWhiteSpace(deviceDisplayName) ? "Unity IPC Camera" : deviceDisplayName;

            this.Manufacturer = manufacturer;

            this.Model = model;

            this.Address = address;
        }

        public async Task<bool> RequestPort(string deviceId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                OnPortAllocationFailed?.Invoke("deviceId 不能为空");
                return false;
            }

            EnsureInitialized();
            activeDeviceId = deviceId;
            bool registered = await EnsureRegisteredAsync(deviceId, cancellationToken);
            if (!registered)
            {
                OnPortAllocationFailed?.Invoke($"设备 {deviceId} 端口协商失败：SIP 注册失败");
                return false;
            }

            string callId = Guid.NewGuid().ToString("N");
            string branch = "z9hG4bK-" + Guid.NewGuid().ToString("N");
            string fromTag = Guid.NewGuid().ToString("N").Substring(0, 8);
            long currentCSeq = Interlocked.Increment(ref cseq);

            string body = BuildSdpOffer(deviceId);
            string invite = BuildInviteRequest(deviceId, callId, branch, fromTag, currentCSeq, body, null);
            await sipTransactionLock.WaitAsync(cancellationToken);
            int port;
            try
            {
                port = await SendInviteAndAwaitPortAsync(
                    deviceId,
                    callId,
                    branch,
                    fromTag,
                    currentCSeq,
                    body,
                    invite,
                    cancellationToken);
            }
            finally
            {
                sipTransactionLock.Release();
            }
            if (port > 0)
            {
                OnPortAllocated?.Invoke(port);
                return true;
            }

            OnPortAllocationFailed?.Invoke($"设备 {deviceId} 端口协商失败");
            return false;
        }

        public async Task<int> WaitForInboundInvitePortAsync(int timeoutMs = 15000, CancellationToken cancellationToken = default)
        {
            if (!initialized)
            {
                EnsureInitialized();
            }

            if (lastInviteMediaPort > 0)
            {
                Debug.Log($"[SIPClient] 复用最近一次 INVITE 端口: {lastInviteMediaPort}");
                return lastInviteMediaPort;
            }

            TaskCompletionSource<int> tcs;
            lock (inviteWaitLock)
            {
                pendingInvitePortTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                tcs = pendingInvitePortTcs;
            }

            int clampedTimeout = Mathf.Clamp(timeoutMs, 1000, 60000);
            Debug.Log($"[SIPClient] 开始等待平台 INVITE，超时={clampedTimeout}ms");
            using var timeoutCts = new CancellationTokenSource(Mathf.Clamp(timeoutMs, 1000, 60000));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
            using (linked.Token.Register(() => tcs.TrySetResult(0)))
            {
                int port = await tcs.Task;
                if (port > 0)
                {
                    Debug.Log($"[SIPClient] 等待平台 INVITE 成功，端口={port}");
                }
                else
                {
                    Debug.LogWarning("[SIPClient] 等待平台 INVITE 超时或被取消");
                }
                return port;
            }
        }

        public string GetLastInboundInviteCallId()
        {
            return lastInviteCallId ?? string.Empty;
        }

        public void ResetInboundInviteState()
        {
            lock (inviteWaitLock)
            {
                lastInviteMediaPort = 0;
                lastInviteCallId = string.Empty;
                lastInviteAckCallId = string.Empty;
                pendingInviteAckExpectedCallId = string.Empty;
                pendingInvitePortTcs = null;
                pendingInviteAckTcs = null;
            }
            Debug.Log("[SIPClient] 已重置 INVITE/ACK 会话缓存");
        }

        public async Task<bool> WaitForInboundInviteAckAsync(string callId, int timeoutMs = 15000, CancellationToken cancellationToken = default)
        {
            if (!initialized)
            {
                EnsureInitialized();
            }

            if (!string.IsNullOrWhiteSpace(callId) && string.Equals(lastInviteAckCallId, callId, StringComparison.OrdinalIgnoreCase))
            {
                Debug.Log($"[SIPClient] 复用最近一次 INVITE ACK: callId={callId}");
                return true;
            }

            TaskCompletionSource<bool> tcs;
            lock (inviteWaitLock)
            {
                pendingInviteAckTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                pendingInviteAckExpectedCallId = callId ?? string.Empty;
                tcs = pendingInviteAckTcs;
            }

            int clampedTimeout = Mathf.Clamp(timeoutMs, 1000, 60000);
            Debug.Log($"[SIPClient] 开始等待平台 ACK，callId={callId}, 超时={clampedTimeout}ms");
            using var timeoutCts = new CancellationTokenSource(clampedTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
            using (linked.Token.Register(() => tcs.TrySetResult(false)))
            {
                bool ok = await tcs.Task;
                if (ok)
                {
                    Debug.Log($"[SIPClient] 等待平台 ACK 成功，callId={callId}");
                }
                else
                {
                    Debug.LogWarning($"[SIPClient] 等待平台 ACK 超时或被取消，callId={callId}");
                }
                return ok;
            }
        }

        public async Task<bool> RegisterDevice(string deviceId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                OnPortAllocationFailed?.Invoke("deviceId 不能为空");
                return false;
            }

            EnsureInitialized();
            activeDeviceId = deviceId;
            bool registered = await EnsureRegisteredAsync(deviceId, cancellationToken);
            if (!registered)
            {
                OnPortAllocationFailed?.Invoke($"设备 {deviceId} SIP 注册失败");
                return false;
            }

            return true;
        }

        /// <summary>
        /// 主动向平台发送设备离线（SIP 注销 REGISTER，Expires=0）。
        /// </summary>
        public async Task<bool> UnregisterDevice(CancellationToken cancellationToken = default, bool waitResponse = true)
        {
            if (!initialized)
            {
                return false;
            }

            await sipTransactionLock.WaitAsync(cancellationToken);
            try
            {
                await UnregisterAsync(cancellationToken, waitResponse);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SIPClient] 注销失败: {ex.Message}");
                return false;
            }
            finally
            {
                sipTransactionLock.Release();
            }
        }

        /// <summary>
        /// 主动上报 Catalog 状态（例如离线时上报 OFF）。
        /// 不等待平台响应，适合应用退出阶段快速发送。
        /// </summary>
        public bool ReportCatalogStatus(bool online)
        {
            try
            {
                EnsureInitialized();
                string responseBody = BuildCatalogResponseXml("1", online ? "ON" : "OFF");
                long cseqValue = Interlocked.Increment(ref cseq);
                string request = BuildQueryResponseMessageRequest(cseqValue, responseBody);
                byte[] data = Encoding.UTF8.GetBytes(request);
                udpClient.Send(data, data.Length, remoteEndpoint);
                Debug.Log($"[SIPClient] 主动上报 Catalog 状态: {(online ? "ON" : "OFF")}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SIPClient] 主动上报 Catalog 状态失败: {ex.Message}");
                return false;
            }
        }

        private async Task<int> SendInviteAndAwaitPortAsync(
            string deviceId,
            string callId,
            string branch,
            string fromTag,
            long cseqValue,
            string body,
            string inviteRequest,
            CancellationToken cancellationToken)
        {
            byte[] data = Encoding.UTF8.GetBytes(inviteRequest);
            Debug.Log($"[SIPClient] 发送 INVITE:\n{inviteRequest}");
            await udpClient.SendAsync(data, data.Length, remoteEndpoint);

            SipInviteResult result = await WaitForInviteResultAsync(callId, cseqValue, cancellationToken);
            if (result.Port > 0)
            {
                await SendAckAsync(result.ResponseText, callId, cseqValue);
                return result.Port;
            }

            if (result.StatusCode != 401 && result.StatusCode != 407)
            {
                if (result.StatusCode > 0)
                {
                    OnPortAllocationFailed?.Invoke($"设备 {deviceId} SIP INVITE 失败，状态码: {result.StatusCode}");
                }
                return 0;
            }

            if (string.IsNullOrWhiteSpace(sipPassword))
            {
                OnPortAllocationFailed?.Invoke("SIP 需要 Digest 鉴权，但未配置 sipPassword");
                return 0;
            }

            DigestChallenge challenge = ParseDigestChallenge(result.ResponseText);
            if (challenge == null)
            {
                OnPortAllocationFailed?.Invoke("SIP 返回 401/407，但未解析到 WWW-Authenticate");
                return 0;
            }

            string requestUri = $"sip:{serverId}@{domain}:{sipPort}";
            string auth = BuildDigestAuthorization(challenge, deviceId, sipPassword, "INVITE", requestUri);
            long authenticatedCSeq = Interlocked.Increment(ref cseq);
            string authenticatedInvite = BuildInviteRequest(deviceId, callId, branch, fromTag, authenticatedCSeq, body, auth);

            data = Encoding.UTF8.GetBytes(authenticatedInvite);
            Debug.Log($"[SIPClient] 发送鉴权 INVITE:\n{authenticatedInvite}");
            await udpClient.SendAsync(data, data.Length, remoteEndpoint);
            result = await WaitForInviteResultAsync(callId, authenticatedCSeq, cancellationToken);
            if (result.Port > 0)
            {
                await SendAckAsync(result.ResponseText, callId, authenticatedCSeq);
                return result.Port;
            }

            if (result.StatusCode > 0)
            {
                OnPortAllocationFailed?.Invoke($"设备 {deviceId} SIP INVITE 失败，状态码: {result.StatusCode}");
            }

            return 0;
        }

        public async Task<bool> ReleasePort(string deviceId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(deviceId) || !initialized)
            {
                return false;
            }

            await sipTransactionLock.WaitAsync(cancellationToken);
            try
            {
            string callId = Guid.NewGuid().ToString("N");
            string branch = "z9hG4bK-" + Guid.NewGuid().ToString("N");
            string fromTag = Guid.NewGuid().ToString("N").Substring(0, 8);
            long currentCSeq = Interlocked.Increment(ref cseq);

            string request = BuildByeRequest(deviceId, callId, branch, fromTag, currentCSeq);
            byte[] data = Encoding.UTF8.GetBytes(request);
            Debug.Log($"[SIPClient] 发送 BYE:\n{request}");
            await udpClient.SendAsync(data, data.Length, remoteEndpoint);

            return await WaitForSipResponseCodeAsync(callId, currentCSeq, cancellationToken) == 200;
            }
            finally
            {
                sipTransactionLock.Release();
            }
        }

        private void EnsureInitialized()
        {
            if (initialized)
            {
                return;
            }

            remoteEndpoint = new IPEndPoint(IPAddress.Parse(serverAddress), sipPort);
            int bindPort = localSipPort;
            if (bindPort == sipPort)
            {
                Debug.LogWarning($"[SIPClient] localSipPort({bindPort}) 与远端 SIP 端口相同，自动回退为临时端口。");
                bindPort = 0;
            }

            udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, bindPort));
            cachedLocalPort = (udpClient.Client.LocalEndPoint as IPEndPoint)?.Port ?? bindPort;
            initialized = true;
            StartIncomingLoop();
        }

        private void StartIncomingLoop()
        {
            if (incomingLoopTask != null && !incomingLoopTask.IsCompleted)
            {
                return;
            }

            incomingLoopCts?.Cancel();
            incomingLoopCts?.Dispose();
            incomingLoopCts = new CancellationTokenSource();
            CancellationToken token = incomingLoopCts.Token;

            incomingLoopTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    UdpReceiveResult result;
                    try
                    {
                        result = await udpClient.ReceiveAsync();
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch (SocketException ex) when (IsReceiveInterrupted(ex))
                    {
                        if (!token.IsCancellationRequested)
                        {
                            Debug.LogWarning($"[SIPClient] 接收循环中断: {ex.SocketErrorCode}");
                        }
                        return;
                    }
                    catch (Exception ex)
                    {
                        if (!token.IsCancellationRequested)
                        {
                            Debug.LogWarning($"[SIPClient] 接收循环异常: {ex.Message}");
                        }
                        continue;
                    }

                    string text = Encoding.UTF8.GetString(result.Buffer);
                    if (text.StartsWith("SIP/2.0", StringComparison.OrdinalIgnoreCase))
                    {
                        lock (responseBufferLock)
                        {
                            responseBuffer.Add(text);
                        }
                        responseSignal.Release();
                        continue;
                    }

                    await HandleIncomingRequestAsync(text, result.RemoteEndPoint, token);
                }
            }, token);
        }

        private async Task<bool> EnsureRegisteredAsync(string deviceId, CancellationToken cancellationToken)
        {
            if (isRegistered && DateTime.UtcNow < registerExpireAtUtc.AddSeconds(-30))
            {
                return true;
            }

            await sipTransactionLock.WaitAsync(cancellationToken);
            try
            {
                if (isRegistered && DateTime.UtcNow < registerExpireAtUtc.AddSeconds(-30))
                {
                    return true;
                }

                bool ok = await RegisterCoreAsync(deviceId, registerExpiresSeconds, cancellationToken);
                if (ok)
                {
                    StartRegisterRefreshLoop();
                }

                return ok;
            }
            finally
            {
                sipTransactionLock.Release();
            }
        }

        private async Task<bool> RegisterCoreAsync(string deviceId, int expires, CancellationToken cancellationToken)
        {
            activeDeviceId = deviceId;
            string callId = Guid.NewGuid().ToString("N");
            string branch = "z9hG4bK-" + Guid.NewGuid().ToString("N");
            registerFromTag ??= Guid.NewGuid().ToString("N").Substring(0, 8);
            long registerCSeq = Interlocked.Increment(ref cseq);
            string registerRequest = BuildRegisterRequest(deviceId, callId, branch, registerFromTag, registerCSeq, null, expires);

            byte[] data = Encoding.UTF8.GetBytes(registerRequest);
            Debug.Log($"[SIPClient] 发送 REGISTER:\n{registerRequest}");
            await udpClient.SendAsync(data, data.Length, remoteEndpoint);

            SipResponse response = await WaitForSipResponseAsync(callId, registerCSeq, cancellationToken);
            if (response.StatusCode == 200)
            {
                OnRegisterSuccess(deviceId, expires);
                return true;
            }

            if (response.StatusCode != 401 && response.StatusCode != 407)
            {
                Debug.LogWarning($"[SIPClient] REGISTER 失败，状态码: {response.StatusCode}");
                return false;
            }

            if (string.IsNullOrWhiteSpace(sipPassword))
            {
                Debug.LogWarning("[SIPClient] REGISTER 需要 Digest 鉴权，但未配置 sipPassword");
                return false;
            }

            DigestChallenge challenge = ParseDigestChallenge(response.ResponseText);
            if (challenge == null)
            {
                Debug.LogWarning("[SIPClient] REGISTER 返回 401/407，但未解析到鉴权挑战");
                return false;
            }

            long authCSeq = Interlocked.Increment(ref cseq);
            string registerUri = $"sip:{domain}";
            string auth = BuildDigestAuthorization(challenge, deviceId, sipPassword, "REGISTER", registerUri);
            string authRequest = BuildRegisterRequest(deviceId, callId, branch, registerFromTag, authCSeq, auth, expires);
            data = Encoding.UTF8.GetBytes(authRequest);
            Debug.Log($"[SIPClient] 发送鉴权 REGISTER:\n{authRequest}");
            await udpClient.SendAsync(data, data.Length, remoteEndpoint);

            response = await WaitForSipResponseAsync(callId, authCSeq, cancellationToken);
            if (response.StatusCode == 200)
            {
                OnRegisterSuccess(deviceId, expires);
                return true;
            }

            Debug.LogWarning($"[SIPClient] REGISTER 鉴权后失败，状态码: {response.StatusCode}");
            return false;
        }

        private void OnRegisterSuccess(string deviceId, int expires)
        {
            isRegistered = expires > 0;
            registerExpireAtUtc = DateTime.UtcNow.AddSeconds(expires);
            registeredDeviceId = deviceId;
            activeDeviceId = deviceId;
            keepaliveFailureCount = 0;
            Debug.Log($"[SIPClient] SIP REGISTER 成功，expires={expires}s");
            StartKeepaliveLoop();
        }

        private void StartRegisterRefreshLoop()
        {
            if (registerRefreshTask != null && !registerRefreshTask.IsCompleted)
            {
                return;
            }

            registerRefreshCts?.Cancel();
            registerRefreshCts?.Dispose();
            registerRefreshCts = new CancellationTokenSource();
            CancellationToken token = registerRefreshCts.Token;

            registerRefreshTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    if (!isRegistered)
                    {
                        return;
                    }

                    TimeSpan delay = registerExpireAtUtc.AddSeconds(-30) - DateTime.UtcNow;
                    if (delay < TimeSpan.FromSeconds(5))
                    {
                        delay = TimeSpan.FromSeconds(5);
                    }

                    try
                    {
                        await Task.Delay(delay, token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    if (token.IsCancellationRequested || !initialized)
                    {
                        return;
                    }

                    await sipTransactionLock.WaitAsync(token);
                    try
                    {
                        if (!isRegistered || DateTime.UtcNow < registerExpireAtUtc.AddSeconds(-25))
                        {
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(registeredDeviceId))
                        {
                            isRegistered = false;
                            return;
                        }

                        bool refreshed = await RegisterCoreAsync(registeredDeviceId, registerExpiresSeconds, token);
                        if (!refreshed)
                        {
                            isRegistered = false;
                            Debug.LogWarning("[SIPClient] SIP 注册续期失败");
                            return;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        isRegistered = false;
                        Debug.LogWarning($"[SIPClient] SIP 注册续期异常: {ex.Message}");
                        return;
                    }
                    finally
                    {
                        sipTransactionLock.Release();
                    }
                }
            }, token);
        }

        private async Task UnregisterAsync(CancellationToken cancellationToken = default, bool waitResponse = true)
        {
            if (!initialized || !isRegistered)
            {
                return;
            }

            string callId = Guid.NewGuid().ToString("N");
            string branch = "z9hG4bK-" + Guid.NewGuid().ToString("N");
            registerFromTag ??= Guid.NewGuid().ToString("N").Substring(0, 8);
            long cseqValue = Interlocked.Increment(ref cseq);
            string unregister = BuildRegisterRequest(registeredDeviceId, callId, branch, registerFromTag, cseqValue, null, 0);
            byte[] data = Encoding.UTF8.GetBytes(unregister);
            Debug.Log($"[SIPClient] 发送注销 REGISTER:\n{unregister}");
            await udpClient.SendAsync(data, data.Length, remoteEndpoint);

            SipResponse response = waitResponse
                ? await WaitForSipResponseAsync(callId, cseqValue, cancellationToken)
                : SipResponse.Empty;
            if (response.StatusCode == 401 || response.StatusCode == 407)
            {
                DigestChallenge challenge = ParseDigestChallenge(response.ResponseText);
                if (challenge != null && !string.IsNullOrWhiteSpace(sipPassword))
                {
                    long authCseq = Interlocked.Increment(ref cseq);
                    string auth = BuildDigestAuthorization(challenge, registeredDeviceId, sipPassword, "REGISTER", $"sip:{domain}");
                    string authUnregister = BuildRegisterRequest(registeredDeviceId, callId, branch, registerFromTag, authCseq, auth, 0);
                    data = Encoding.UTF8.GetBytes(authUnregister);
                    Debug.Log($"[SIPClient] 发送鉴权注销 REGISTER:\n{authUnregister}");
                    await udpClient.SendAsync(data, data.Length, remoteEndpoint);
                    if (waitResponse)
                    {
                        await WaitForSipResponseAsync(callId, authCseq, cancellationToken);
                    }
                }
            }

            isRegistered = false;
            registerExpireAtUtc = DateTime.MinValue;
            registeredDeviceId = null;
            keepaliveFailureCount = 0;
        }

        private string GetCurrentDeviceId(string fallback = "")
        {
            if (!string.IsNullOrWhiteSpace(registeredDeviceId))
            {
                return registeredDeviceId;
            }

            if (!string.IsNullOrWhiteSpace(activeDeviceId))
            {
                return activeDeviceId;
            }

            return fallback;
        }

        private void StartKeepaliveLoop()
        {
            if (keepaliveTask != null && !keepaliveTask.IsCompleted)
            {
                return;
            }

            keepaliveCts?.Cancel();
            keepaliveCts?.Dispose();
            keepaliveCts = new CancellationTokenSource();
            CancellationToken token = keepaliveCts.Token;

            keepaliveTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(keepaliveIntervalSeconds), token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    if (token.IsCancellationRequested || !initialized || !isRegistered)
                    {
                        return;
                    }

                    await sipTransactionLock.WaitAsync(token);
                    try
                    {
                        if (!isRegistered)
                        {
                            return;
                        }

                        bool ok = await SendKeepaliveCoreAsync(token);
                        if (ok)
                        {
                            keepaliveFailureCount = 0;
                            continue;
                        }

                        keepaliveFailureCount++;
                        Debug.LogWarning($"[SIPClient] Keepalive 失败，第 {keepaliveFailureCount} 次");
                        if (keepaliveFailureCount >= 3)
                        {
                            isRegistered = false;
                            Debug.LogWarning("[SIPClient] Keepalive 连续失败，标记注册失效");
                            return;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        keepaliveFailureCount++;
                        Debug.LogWarning($"[SIPClient] Keepalive 异常: {ex.Message}");
                        if (keepaliveFailureCount >= 3)
                        {
                            isRegistered = false;
                            return;
                        }
                    }
                    finally
                    {
                        sipTransactionLock.Release();
                    }
                }
            }, token);
        }

        private async Task<bool> SendKeepaliveCoreAsync(CancellationToken cancellationToken)
        {
            string callId = Guid.NewGuid().ToString("N");
            string branch = "z9hG4bK-" + Guid.NewGuid().ToString("N");
            string fromTag = registerFromTag ?? Guid.NewGuid().ToString("N").Substring(0, 8);
            long msgCseq = Interlocked.Increment(ref cseq);
            string body = BuildKeepaliveXml();
            string request = BuildKeepaliveMessageRequest(callId, branch, fromTag, msgCseq, body, null);
            byte[] data = Encoding.UTF8.GetBytes(request);
            Debug.Log($"[SIPClient] 发送 Keepalive MESSAGE:\n{request}");
            await udpClient.SendAsync(data, data.Length, remoteEndpoint);

            SipResponse response = await WaitForSipResponseAsync(callId, msgCseq, cancellationToken);
            if (response.StatusCode == 200)
            {
                return true;
            }

            if (response.StatusCode != 401 && response.StatusCode != 407)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(sipPassword))
            {
                return false;
            }

            DigestChallenge challenge = ParseDigestChallenge(response.ResponseText);
            if (challenge == null)
            {
                return false;
            }

            long authCseq = Interlocked.Increment(ref cseq);
            string keepaliveDeviceId = GetCurrentDeviceId(serverId);
            string requestUri = $"sip:{keepaliveDeviceId}@{domain}:{sipPort}";
            string auth = BuildDigestAuthorization(challenge, GetCurrentDeviceId(serverId), sipPassword, "MESSAGE", requestUri);
            string authRequest = BuildKeepaliveMessageRequest(callId, branch, fromTag, authCseq, body, auth);
            data = Encoding.UTF8.GetBytes(authRequest);
            Debug.Log($"[SIPClient] 发送鉴权 Keepalive MESSAGE:\n{authRequest}");
            await udpClient.SendAsync(data, data.Length, remoteEndpoint);

            response = await WaitForSipResponseAsync(callId, authCseq, cancellationToken);
            return response.StatusCode == 200;
        }

        private async Task<SipInviteResult> WaitForInviteResultAsync(string callId, long currentCSeq, CancellationToken ct)
        {
            while (true)
            {
                SipResponse response = await WaitForSipResponseAsync(callId, currentCSeq, ct);
                if (response.StatusCode == 0)
                {
                    return SipInviteResult.Empty;
                }

                if (response.StatusCode >= 300)
                {
                    return new SipInviteResult(response.StatusCode, 0, response.ResponseText);
                }

                if (response.StatusCode == 180 || response.StatusCode == 183)
                {
                    continue;
                }

                int port = ParseMediaPortFromSdp(response.ResponseText);
                if (port <= 0)
                {
                    continue;
                }

                return new SipInviteResult(response.StatusCode, port, response.ResponseText);
            }
        }

        private async Task<int> WaitForSipResponseCodeAsync(string callId, long currentCSeq, CancellationToken ct)
        {
            SipResponse response = await WaitForSipResponseAsync(callId, currentCSeq, ct);
            return response.StatusCode;
        }

        private async Task<SipResponse> WaitForSipResponseAsync(string callId, long currentCSeq, CancellationToken ct)
        {
            using var timeoutCts = new CancellationTokenSource(requestTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            CancellationToken token = linked.Token;

            while (!token.IsCancellationRequested)
            {
                string matched = TryDequeueMatchingResponse(callId, currentCSeq);
                if (!string.IsNullOrEmpty(matched))
                {
                    return new SipResponse(ParseStatusCode(matched), matched);
                }

                try
                {
                    await responseSignal.WaitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    return SipResponse.Empty;
                }
            }

            return SipResponse.Empty;
        }

        private string TryDequeueMatchingResponse(string callId, long currentCSeq)
        {
            lock (responseBufferLock)
            {
                for (int i = 0; i < responseBuffer.Count; i++)
                {
                    string candidate = responseBuffer[i];
                    if (!IsTargetResponse(candidate, callId, currentCSeq))
                    {
                        continue;
                    }

                    responseBuffer.RemoveAt(i);
                    return candidate;
                }
            }

            return string.Empty;
        }

        private static bool IsTargetResponse(string responseText, string callId, long cseq)
        {
            return responseText.Contains($"Call-ID: {callId}", StringComparison.OrdinalIgnoreCase)
                   && responseText.Contains($"CSeq: {cseq}", StringComparison.OrdinalIgnoreCase);
        }

        private async Task HandleIncomingRequestAsync(string requestText, IPEndPoint sourceEndPoint, CancellationToken cancellationToken)
        {
            string[] lines = requestText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            if (lines.Length == 0)
            {
                return;
            }

            string requestLine = lines[0];
            if (requestLine.StartsWith("MESSAGE ", StringComparison.OrdinalIgnoreCase))
            {
                await HandleIncomingMessageRequestAsync(requestText, sourceEndPoint, cancellationToken);
                return;
            }

            if (requestLine.StartsWith("INVITE ", StringComparison.OrdinalIgnoreCase))
            {
                await HandleIncomingInviteRequestAsync(requestText, sourceEndPoint, cancellationToken);
                return;
            }

            if (requestLine.StartsWith("ACK ", StringComparison.OrdinalIgnoreCase))
            {
                string ackCallId = ParseHeaderValue(requestText, "Call-ID");
                if (!string.IsNullOrWhiteSpace(ackCallId))
                {
                    lastInviteAckCallId = ackCallId;
                    lock (inviteWaitLock)
                    {
                        if (pendingInviteAckTcs != null)
                        {
                            if (string.IsNullOrWhiteSpace(pendingInviteAckExpectedCallId)
                                || string.Equals(pendingInviteAckExpectedCallId, ackCallId, StringComparison.OrdinalIgnoreCase))
                            {
                                pendingInviteAckTcs.TrySetResult(true);
                            }
                            else
                            {
                                Debug.Log($"[SIPClient] ACK callId 与当前等待不一致，忽略: ack={ackCallId}, expected={pendingInviteAckExpectedCallId}");
                            }
                        }
                    }
                }
                Debug.Log($"[SIPClient] 收到平台 ACK:\n{requestText}");

                if (!string.IsNullOrWhiteSpace(ackCallId)
                    && string.Equals(lastInviteCallId, ackCallId, StringComparison.OrdinalIgnoreCase)
                    && lastInviteMediaPort > 0)
                {
                    OnInviteSessionReady?.Invoke(lastInviteMediaPort, ackCallId);
                    Debug.Log($"[SIPClient] INVITE 会话就绪，port={lastInviteMediaPort}, callId={ackCallId}");
                }
            }
        }

        private async Task HandleIncomingMessageRequestAsync(string requestText, IPEndPoint sourceEndPoint, CancellationToken cancellationToken)
        {
            string[] lines = requestText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            if (lines.Length == 0)
            {
                return;
            }

            string callId = ParseHeaderValue(requestText, "Call-ID");
            string cseqHeader = ParseHeaderValue(requestText, "CSeq");
            string via = ParseHeaderValue(requestText, "Via");
            string from = ParseHeaderValue(requestText, "From");
            string to = ParseHeaderValue(requestText, "To");
            if (string.IsNullOrWhiteSpace(callId) || string.IsNullOrWhiteSpace(cseqHeader) || string.IsNullOrWhiteSpace(via))
            {
                return;
            }

            string ok = "SIP/2.0 200 OK\r\n"
                        + $"Via: {via}\r\n"
                        + $"From: {from}\r\n"
                        + $"To: {to}\r\n"
                        + $"Call-ID: {callId}\r\n"
                        + $"CSeq: {cseqHeader}\r\n"
                        + "Content-Length: 0\r\n\r\n";
            byte[] okData = Encoding.UTF8.GetBytes(ok);
            Debug.Log($"[SIPClient] 发送 MESSAGE 请求应答 200 OK -> {sourceEndPoint}:\n{ok}");
            await udpClient.SendAsync(okData, okData.Length, sourceEndPoint);

            string body = ParseSipBody(requestText);
            string cmdType = ParseXmlValue(body, "CmdType");
            if (!cmdType.Equals("DeviceInfo", StringComparison.OrdinalIgnoreCase)
                && !cmdType.Equals("Catalog", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string sn = ParseXmlValue(body, "SN");
            string responseBody = cmdType.Equals("DeviceInfo", StringComparison.OrdinalIgnoreCase)
                ? BuildDeviceInfoResponseXml(sn)
                : BuildCatalogResponseXml(sn, "ON");

            long cseqValue = Interlocked.Increment(ref cseq);
            string responseRequest = BuildQueryResponseMessageRequest(cseqValue, responseBody);
            byte[] data = Encoding.UTF8.GetBytes(responseRequest);
            Debug.Log($"[SIPClient] 发送查询响应 MESSAGE (CmdType={cmdType}) -> {sourceEndPoint}:\n{responseRequest}");
            await udpClient.SendAsync(data, data.Length, sourceEndPoint);
            Debug.Log($"[SIPClient] 已响应平台查询 CmdType={cmdType}, SN={sn}");
        }

        private async Task HandleIncomingInviteRequestAsync(string requestText, IPEndPoint sourceEndPoint, CancellationToken cancellationToken)
        {
            try
            {
                Debug.Log($"[SIPClient] 收到平台 INVITE:\n{requestText}");
                string callId = ParseHeaderValue(requestText, "Call-ID");
                string cseqHeader = ParseHeaderValue(requestText, "CSeq");
                string via = ParseHeaderValue(requestText, "Via");
                string from = ParseHeaderValue(requestText, "From");
                string to = ParseHeaderValue(requestText, "To");
                if (string.IsNullOrWhiteSpace(callId))
                {
                    Debug.LogWarning("[SIPClient] INVITE 缺少 Call-ID，无法回 200 OK");
                    return;
                }
                if (string.IsNullOrWhiteSpace(cseqHeader))
                {
                    Debug.LogWarning("[SIPClient] INVITE 缺少 CSeq，无法回 200 OK");
                    return;
                }
                if (string.IsNullOrWhiteSpace(via))
                {
                    Debug.LogWarning("[SIPClient] INVITE 缺少 Via，无法回 200 OK");
                    return;
                }

                int mediaPort = ParseMediaPortFromSdp(requestText);
                if (mediaPort > 0)
                {
                    Debug.Log($"[SIPClient] 收到平台 INVITE，下发端口={mediaPort}");
                    lastInviteMediaPort = mediaPort;
                    lastInviteCallId = callId;
                    lock (inviteWaitLock)
                    {
                        pendingInvitePortTcs?.TrySetResult(mediaPort);
                    }
                }
                else
                {
                    Debug.LogWarning("[SIPClient] INVITE SDP 未解析到媒体端口，仍尝试回 200 OK");
                }

                string invite200 = BuildInvite200OkResponse(callId, cseqHeader, via, from, to, mediaPort);
                byte[] okData = Encoding.UTF8.GetBytes(invite200);
                Debug.Log($"[SIPClient] 准备发送 INVITE 200 OK -> {sourceEndPoint}");
                Debug.Log($"[SIPClient] INVITE 200 OK 报文:\n{invite200}");
                try
                {
                    await udpClient.SendAsync(okData, okData.Length, sourceEndPoint);
                    Debug.Log($"[SIPClient] INVITE 200 OK 发送完成 -> {sourceEndPoint}, bytes={okData.Length}");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[SIPClient] INVITE 200 OK 发送失败 -> {sourceEndPoint}, error={ex.GetType().Name}: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SIPClient] 处理 INVITE 发生异常，未能发送 200 OK: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private string BuildInvite200OkResponse(string callId, string cseqHeader, string via, string from, string to, int mediaPort)
        {
            string localIp = GetLocalIp();
            string deviceId = GetCurrentDeviceId(serverId);
            int answerPort = mediaPort > 0 ? mediaPort : 0;
            var sdp = new StringBuilder();
            sdp.AppendLine("v=0");
            sdp.AppendLine($"o={deviceId} 0 0 IN IP4 {localIp}");
            sdp.AppendLine("s=Play");
            sdp.AppendLine($"c=IN IP4 {localIp}");
            sdp.AppendLine("t=0 0");
            sdp.AppendLine($"m=video {answerPort} RTP/AVP 96");
            sdp.AppendLine("a=rtpmap:96 H264/90000");
            string body = sdp.ToString();

            return "SIP/2.0 200 OK\r\n"
                   + $"Via: {via}\r\n"
                   + $"From: {from}\r\n"
                   + $"To: {EnsureTagInToHeader(to)}\r\n"
                   + $"Call-ID: {callId}\r\n"
                   + $"CSeq: {cseqHeader}\r\n"
                   + "User-Agent: XiaoJingSpace\r\n"
                   + "Content-Type: application/sdp\r\n"
                   + $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n"
                   + body;
        }

        private static string EnsureTagInToHeader(string toHeader)
        {
            if (string.IsNullOrWhiteSpace(toHeader))
            {
                return toHeader;
            }

            if (toHeader.IndexOf("tag=", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return toHeader;
            }

            return $"{toHeader};tag={Guid.NewGuid().ToString("N").Substring(0, 8)}";
        }

        private static bool IsReceiveInterrupted(SocketException ex)
        {
            return ex.SocketErrorCode == SocketError.ConnectionReset
                   || ex.SocketErrorCode == SocketError.OperationAborted
                   || ex.SocketErrorCode == SocketError.Interrupted
                   || ex.SocketErrorCode == SocketError.Shutdown
                   || ex.SocketErrorCode == SocketError.NotSocket;
        }

        private async Task SendAckAsync(string responseText, string callId, long inviteCSeq)
        {
            string toTag = ParseHeaderValue(responseText, "To");
            string from = ParseHeaderValue(responseText, "From");
            string via = ParseHeaderValue(responseText, "Via");

            string requestUri = $"sip:{serverId}@{domain}:{sipPort}";
            string builder = "ACK " + requestUri + " SIP/2.0\r\n"
                + $"Via: {via}\r\n"
                + $"From: {from}\r\n"
                + $"To: {toTag}\r\n"
                + $"Call-ID: {callId}\r\n"
                + $"CSeq: {inviteCSeq} ACK\r\n"
                + "Content-Length: 0\r\n\r\n";

            byte[] data = Encoding.UTF8.GetBytes(builder);
            Debug.Log($"[SIPClient] 发送 ACK:\n{builder}");
            await udpClient.SendAsync(data, data.Length, remoteEndpoint);
        }

        private string BuildInviteRequest(
            string deviceId,
            string callId,
            string branch,
            string fromTag,
            long currentCSeq,
            string body,
            string authorization)
        {
            string localIp = GetLocalIp();
            string requestUri = $"sip:{serverId}@{domain}:{sipPort}";
            string fromUser = GetCurrentDeviceId(deviceId);
            string fromDomain = ResolveTargetDomain(fromUser);

            var headers = new List<string>
            {
                $"INVITE {requestUri} SIP/2.0",
                $"Via: SIP/2.0/UDP {localIp}:{GetLocalBoundPort()};branch={branch};rport",
                "Max-Forwards: 70",
                $"From: <sip:{fromUser}@{fromDomain}>;tag={fromTag}",
                $"To: <sip:{serverId}@{domain}>",
                $"Call-ID: {callId}",
                $"CSeq: {currentCSeq} INVITE",
                $"Contact: <sip:{fromUser}@{localIp}:{GetLocalBoundPort()}>",
                // WVP expects "channelId,deviceId" semantics here.
                $"Subject: {deviceId}:0,{fromUser}:0",
                "User-Agent: XiaoJingSpace",
                "Content-Type: application/sdp",
                $"Content-Length: {Encoding.UTF8.GetByteCount(body)}"
            };
            if (!string.IsNullOrWhiteSpace(authorization))
            {
                headers.Add($"Authorization: {authorization}");
            }

            headers.Add(string.Empty);
            headers.Add(body);

            return string.Join("\r\n", headers);
        }

        private string BuildByeRequest(string deviceId, string callId, string branch, string fromTag, long currentCSeq)
        {
            string localIp = GetLocalIp();
            string targetDomain = ResolveTargetDomain(deviceId);
            string requestUri = $"sip:{deviceId}@{targetDomain}:{sipPort}";
            string fromUser = serverId;

            var headers = new List<string>
            {
                $"BYE {requestUri} SIP/2.0",
                $"Via: SIP/2.0/UDP {localIp}:{GetLocalBoundPort()};branch={branch};rport",
                "Max-Forwards: 70",
                $"From: <sip:{fromUser}@{domain}>;tag={fromTag}",
                $"To: <sip:{deviceId}@{targetDomain}>",
                $"Call-ID: {callId}",
                $"CSeq: {currentCSeq} BYE",
                $"Contact: <sip:{fromUser}@{localIp}:{GetLocalBoundPort()}>",
                "Content-Length: 0",
                string.Empty,
                string.Empty
            };

            return string.Join("\r\n", headers);
        }

        private string BuildRegisterRequest(
            string deviceId,
            string callId,
            string branch,
            string fromTag,
            long currentCSeq,
            string authorization,
            int expires)
        {
            string localIp = GetLocalIp();
            int localPort = GetLocalBoundPort();
            string requestUri = $"sip:{domain}";
            string localDeviceDomain = ResolveTargetDomain(deviceId);

            var headers = new List<string>
            {
                $"REGISTER {requestUri} SIP/2.0",
                $"Via: SIP/2.0/UDP {localIp}:{localPort};branch={branch};rport",
                "Max-Forwards: 70",
                $"From: <sip:{deviceId}@{localDeviceDomain}>;tag={fromTag}",
                $"To: <sip:{deviceId}@{localDeviceDomain}>",
                $"Call-ID: {callId}",
                $"CSeq: {currentCSeq} REGISTER",
                $"Contact: <sip:{deviceId}@{localIp}:{localPort}>",
                $"Expires: {Math.Max(0, expires)}",
                "User-Agent: XiaoJingSpace",
                "Content-Length: 0"
            };

            if (!string.IsNullOrWhiteSpace(authorization))
            {
                headers.Add($"Authorization: {authorization}");
            }

            headers.Add(string.Empty);
            headers.Add(string.Empty);
            return string.Join("\r\n", headers);
        }

        private string BuildKeepaliveMessageRequest(
            string callId,
            string branch,
            string fromTag,
            long currentCSeq,
            string body,
            string authorization)
        {
            string localIp = GetLocalIp();
            int localPort = GetLocalBoundPort();
            string deviceId = GetCurrentDeviceId(serverId);
            string localDeviceDomain = ResolveTargetDomain(deviceId);
            string requestUri = $"sip:{deviceId}@{domain}:{sipPort}";

            var headers = new List<string>
            {
                $"MESSAGE {requestUri} SIP/2.0",
                $"Via: SIP/2.0/UDP {localIp}:{localPort};branch={branch};rport",
                "Max-Forwards: 70",
                $"From: <sip:{deviceId}@{localDeviceDomain}>;tag={fromTag}",
                $"To: <sip:{deviceId}@{domain}>",
                $"Call-ID: {callId}",
                $"CSeq: {currentCSeq} MESSAGE",
                $"Contact: <sip:{deviceId}@{localIp}:{localPort}>",
                "User-Agent: XiaoJingSpace",
                "Content-Type: Application/MANSCDP+xml",
                $"Content-Length: {Encoding.UTF8.GetByteCount(body)}"
            };

            if (!string.IsNullOrWhiteSpace(authorization))
            {
                headers.Add($"Authorization: {authorization}");
            }

            headers.Add(string.Empty);
            headers.Add(body);
            return string.Join("\r\n", headers);
        }

        private string BuildKeepaliveXml()
        {
            int sn = Interlocked.Increment(ref keepaliveSn);
            string deviceId = GetCurrentDeviceId(serverId);
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\"?>");
            sb.AppendLine("<Notify>");
            sb.AppendLine("<CmdType>Keepalive</CmdType>");
            sb.AppendLine($"<SN>{sn}</SN>");
            sb.AppendLine($"<DeviceID>{deviceId}</DeviceID>");
            sb.AppendLine("<Status>OK</Status>");
            sb.AppendLine("</Notify>");
            return sb.ToString();
        }

        private string BuildQueryResponseMessageRequest(long currentCSeq, string body)
        {
            string localIp = GetLocalIp();
            int localPort = GetLocalBoundPort();
            string deviceId = GetCurrentDeviceId(serverId);
            string localDeviceDomain = ResolveTargetDomain(deviceId);
            string callId = Guid.NewGuid().ToString("N");
            string branch = "z9hG4bK-" + Guid.NewGuid().ToString("N");
            string fromTag = registerFromTag ?? Guid.NewGuid().ToString("N").Substring(0, 8);
            string requestUri = $"sip:{deviceId}@{domain}:{sipPort}";

            var headers = new List<string>
            {
                $"MESSAGE {requestUri} SIP/2.0",
                $"Via: SIP/2.0/UDP {localIp}:{localPort};branch={branch};rport",
                "Max-Forwards: 70",
                $"From: <sip:{deviceId}@{localDeviceDomain}>;tag={fromTag}",
                $"To: <sip:{deviceId}@{domain}>",
                $"Call-ID: {callId}",
                $"CSeq: {currentCSeq} MESSAGE",
                $"Contact: <sip:{deviceId}@{localIp}:{localPort}>",
                "User-Agent: XiaoJingSpace",
                "Content-Type: Application/MANSCDP+xml",
                $"Content-Length: {Encoding.UTF8.GetByteCount(body)}",
                string.Empty,
                body
            };

            return string.Join("\r\n", headers);
        }

        private string BuildDeviceInfoResponseXml(string sn)
        {
            string deviceId = GetCurrentDeviceId(serverId);
            string snValue = string.IsNullOrWhiteSpace(sn) ? "1" : sn;
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\"?>");
            sb.AppendLine("<Response>");
            sb.AppendLine("<CmdType>DeviceInfo</CmdType>");
            sb.AppendLine($"<SN>{snValue}</SN>");
            sb.AppendLine($"<DeviceID>{deviceId}</DeviceID>");
            sb.AppendLine($"<DeviceName>{this.deviceDisplayName}</DeviceName>");
            sb.AppendLine($"<Manufacturer>{ this.Manufacturer}</Manufacturer>");
            sb.AppendLine($"<Model>{  this.Model}</Model>");
            sb.AppendLine("<Firmware>1.0.0</Firmware>");
            sb.AppendLine("<Channel>1</Channel>");
            sb.AppendLine("</Response>");
            return sb.ToString();
        }

        private string BuildCatalogResponseXml(string sn, string status)
        {
            string deviceId = GetCurrentDeviceId(serverId);
            string snValue = string.IsNullOrWhiteSpace(sn) ? "1" : sn;
            string normalizedStatus = string.Equals(status, "OFF", StringComparison.OrdinalIgnoreCase) ? "OFF" : "ON";
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\"?>");
            sb.AppendLine("<Response>");
            sb.AppendLine("<CmdType>Catalog</CmdType>");
            sb.AppendLine($"<SN>{snValue}</SN>");
            sb.AppendLine($"<DeviceID>{deviceId}</DeviceID>");
            sb.AppendLine("<SumNum>1</SumNum>");
            sb.AppendLine("<DeviceList Num=\"1\">");
            sb.AppendLine("<Item>");
            sb.AppendLine($"<DeviceID>{deviceId}</DeviceID>");
            sb.AppendLine($"<Name>{deviceDisplayName}</Name>");
            sb.AppendLine($"<Manufacturer>{this.Manufacturer}</Manufacturer>");
            sb.AppendLine($"<Model>{this.Model}</Model>");
            sb.AppendLine($"<Owner>{this.Manufacturer}</Owner>");
            sb.AppendLine("<CivilCode>000000</CivilCode>");
            sb.AppendLine($"<Address>{this.Address}</Address>");
            sb.AppendLine("<Parental>0</Parental>");
            sb.AppendLine("<SafetyWay>0</SafetyWay>");
            sb.AppendLine("<RegisterWay>1</RegisterWay>");
            sb.AppendLine("<Secrecy>0</Secrecy>");
            sb.AppendLine($"<Status>{normalizedStatus}</Status>");
            sb.AppendLine("</Item>");
            sb.AppendLine("</DeviceList>");
            sb.AppendLine("</Response>");
            return sb.ToString();
        }

        private static string BuildSdpOffer(string deviceId)
        {
            var sb = new StringBuilder();
            sb.AppendLine("v=0");
            sb.AppendLine($"o={deviceId} 0 0 IN IP4 0.0.0.0");
            sb.AppendLine("s=Play");
            sb.AppendLine("c=IN IP4 0.0.0.0");
            sb.AppendLine("t=0 0");
            sb.AppendLine("m=video 0 RTP/AVP 96");
            sb.AppendLine("a=rtpmap:96 H264/90000");
            sb.AppendLine("m=audio 0 RTP/AVP 8");
            sb.AppendLine("a=rtpmap:8 PCMA/8000");
            return sb.ToString();
        }

        private string ResolveTargetDomain(string deviceId)
        {
            if (!string.IsNullOrWhiteSpace(deviceId) && deviceId.Length >= 10)
            {
                string maybeDomain = deviceId.Substring(0, 10);
                bool isNumeric = true;
                for (int i = 0; i < maybeDomain.Length; i++)
                {
                    if (!char.IsDigit(maybeDomain[i]))
                    {
                        isNumeric = false;
                        break;
                    }
                }

                if (isNumeric)
                {
                    return maybeDomain;
                }
            }

            return domain;
        }

        private static int ParseMediaPortFromSdp(string sipResponseText)
        {
            int bodyIndex = sipResponseText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (bodyIndex < 0)
            {
                return 0;
            }

            string sdp = sipResponseText.Substring(bodyIndex + 4);
            string[] lines = sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                if (!line.StartsWith("m=video", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string[] parts = line.Split(' ');
                if (parts.Length >= 2 && int.TryParse(parts[1], out int port))
                {
                    return port;
                }
            }

            return 0;
        }

        private static int ParseStatusCode(string sipResponseText)
        {
            Match m = Regex.Match(sipResponseText, @"SIP/2.0\s+(\d{3})");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int code))
            {
                return code;
            }

            return 0;
        }

        private static string ParseHeaderValue(string sipText, string headerName)
        {
            string[] lines = sipText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            foreach (string line in lines)
            {
                if (line.StartsWith(headerName + ":", StringComparison.OrdinalIgnoreCase))
                {
                    return line.Substring(headerName.Length + 1).Trim();
                }
            }

            return string.Empty;
        }

        private static string ParseSipBody(string sipText)
        {
            int bodyIndex = sipText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (bodyIndex < 0)
            {
                return string.Empty;
            }

            return sipText.Substring(bodyIndex + 4);
        }

        private static string ParseXmlValue(string xmlText, string elementName)
        {
            if (string.IsNullOrWhiteSpace(xmlText) || string.IsNullOrWhiteSpace(elementName))
            {
                return string.Empty;
            }

            Match match = Regex.Match(
                xmlText,
                $@"<\s*{elementName}\s*>(.*?)<\s*/\s*{elementName}\s*>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
        }

        private static DigestChallenge ParseDigestChallenge(string sipResponseText)
        {
            string line = ParseHeaderValue(sipResponseText, "WWW-Authenticate");
            if (string.IsNullOrWhiteSpace(line))
            {
                line = ParseHeaderValue(sipResponseText, "Proxy-Authenticate");
            }

            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("Digest", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string blob = line.Substring(6).Trim();
            string realm = ParseDigestParam(blob, "realm");
            string nonce = ParseDigestParam(blob, "nonce");
            if (string.IsNullOrWhiteSpace(realm) || string.IsNullOrWhiteSpace(nonce))
            {
                return null;
            }

            return new DigestChallenge
            {
                Realm = realm,
                Nonce = nonce,
                Opaque = ParseDigestParam(blob, "opaque"),
                Qop = ParseDigestParam(blob, "qop")
            };
        }

        private static string ParseDigestParam(string blob, string name)
        {
            Match match = Regex.Match(blob, $@"\b{name}\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            match = Regex.Match(blob, $@"\b{name}\s*=\s*([^,\s]+)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private static string BuildDigestAuthorization(
            DigestChallenge challenge,
            string username,
            string password,
            string method,
            string uri)
        {
            string ha1 = Md5Hex($"{username}:{challenge.Realm}:{password}");
            string ha2 = Md5Hex($"{method}:{uri}");
            string qop = SelectQop(challenge.Qop);
            string cnonce = Guid.NewGuid().ToString("N").Substring(0, 16);
            const string nc = "00000001";

            string response = string.IsNullOrWhiteSpace(qop)
                ? Md5Hex($"{ha1}:{challenge.Nonce}:{ha2}")
                : Md5Hex($"{ha1}:{challenge.Nonce}:{nc}:{cnonce}:{qop}:{ha2}");

            var sb = new StringBuilder();
            sb.Append($"Digest username=\"{username}\"");
            sb.Append($", realm=\"{challenge.Realm}\"");
            sb.Append($", nonce=\"{challenge.Nonce}\"");
            sb.Append($", uri=\"{uri}\"");
            sb.Append($", response=\"{response}\"");
            sb.Append(", algorithm=MD5");

            if (!string.IsNullOrWhiteSpace(qop))
            {
                sb.Append($", cnonce=\"{cnonce}\"");
                sb.Append($", nc={nc}");
                sb.Append($", qop={qop}");
            }

            if (!string.IsNullOrWhiteSpace(challenge.Opaque))
            {
                sb.Append($", opaque=\"{challenge.Opaque}\"");
            }

            return sb.ToString();
        }

        private static string SelectQop(string qopValue)
        {
            if (string.IsNullOrWhiteSpace(qopValue))
            {
                return string.Empty;
            }

            foreach (string candidate in qopValue.Split(','))
            {
                string trimmed = candidate.Trim();
                if (trimmed.Equals("auth", StringComparison.OrdinalIgnoreCase))
                {
                    return "auth";
                }
            }

            return qopValue.Split(',')[0].Trim();
        }

        private static string Md5Hex(string input)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(input);
            byte[] hash;
            using (MD5 md5 = MD5.Create())
            {
                hash = md5.ComputeHash(bytes);
            }
            var sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
            {
                sb.Append(b.ToString("x2"));
            }

            return sb.ToString();
        }

        private string GetLocalIp()
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Connect(serverAddress, sipPort);
                var endPoint = socket.LocalEndPoint as IPEndPoint;
                return endPoint?.Address.ToString() ?? "127.0.0.1";
            }
            catch
            {
                return "127.0.0.1";
            }
        }

        private int GetLocalBoundPort()
        {
            int port = (udpClient?.Client?.LocalEndPoint as IPEndPoint)?.Port ?? 0;
            if (port > 0)
            {
                cachedLocalPort = port;
                return port;
            }

            return cachedLocalPort;
        }

        private sealed class DigestChallenge
        {
            public string Realm;
            public string Nonce;
            public string Opaque;
            public string Qop;
        }

        private readonly struct SipInviteResult
        {
            public readonly int StatusCode;
            public readonly int Port;
            public readonly string ResponseText;

            public static SipInviteResult Empty => new SipInviteResult(0, 0, string.Empty);

            public SipInviteResult(int statusCode, int port, string responseText)
            {
                StatusCode = statusCode;
                Port = port;
                ResponseText = responseText;
            }
        }

        private readonly struct SipResponse
        {
            public readonly int StatusCode;
            public readonly string ResponseText;

            public static SipResponse Empty => new SipResponse(0, string.Empty);

            public SipResponse(int statusCode, string responseText)
            {
                StatusCode = statusCode;
                ResponseText = responseText;
            }
        }

        public void Dispose()
        {
            try
            {
                registerRefreshCts?.Cancel();
                keepaliveCts?.Cancel();
                incomingLoopCts?.Cancel();
            }
            catch
            {
                // ignore background task cancellation exceptions
            }

            try
            {
                // Non-blocking shutdown: close socket immediately to unblock all ReceiveAsync.
                udpClient?.Close();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SIPClient] Dispose socket close warning: {e.Message}");
            }

            registerRefreshCts?.Dispose();
            registerRefreshCts = null;
            registerRefreshTask = null;
            keepaliveCts?.Dispose();
            keepaliveCts = null;
            keepaliveTask = null;
            incomingLoopCts?.Dispose();
            incomingLoopCts = null;
            incomingLoopTask = null;
            udpClient = null;
            initialized = false;
            isRegistered = false;
            registerExpireAtUtc = DateTime.MinValue;
            registeredDeviceId = null;
            activeDeviceId = null;
            keepaliveFailureCount = 0;
            cachedLocalPort = 0;
        }
    }
}
