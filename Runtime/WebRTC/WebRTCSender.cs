using System;
using System.Collections;
using System.Text;
using System.Threading.Tasks;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.Networking;
using ZLMediakitPlugin.Models;

namespace ZLMediakitPlugin.WebRTC
{
    /// <summary>
    /// WebRTC 发送基类，负责轨道绑定与 PeerConnection 生命周期。
    /// 子类仅需实现信令交换（Offer -> Answer）。
    /// </summary>
    public abstract class WebRTCSender : IDisposable
    {
        protected RTCPeerConnection PeerConnection;
        protected MediaStream MediaStream;
        protected VideoStreamTrack VideoTrack;
        protected AudioStreamTrack AudioTrack;

        public WebCamTexture CameraTexture;
        protected WebCameraIndex BoundCameraIndex;
        protected RenderTexture BoundRenderTexture;
        protected AudioSource BoundAudioSource;

        protected string StreamId;
        protected int AllocatedPort;
        protected bool IsPublishing;

        /// <summary>若为 true，表示本类通过 <see cref="Microphone.Start"/> 绑定了麦克风，需在停推时 <see cref="Microphone.End"/>。</summary>
        private bool micCaptureOwnedBySender;
        private string micDeviceName;

        private bool webRtcInitialized;
        private MonoBehaviour coroutineHost;
        private Coroutine webRtcUpdateCoroutine;
        private bool disconnectCleanupScheduled;

        public event Action<string> OnConnectionStateChanged;
        public event Action<string> OnError;
        /// <summary>当 PeerConnection 进入 Disconnected / Failed / Closed，且内部已执行完停止推流与 WebRTC 驱动重置后触发（参数为本 sender 实例与状态名）。</summary>
        public event Action<WebRTCSender, string> OnDisconnected;

        private bool m_webRtcUpdate = true;
        public void SetCoroutineHost(MonoBehaviour host, bool webRtcUpdate =true)
        {
            coroutineHost = host;
            m_webRtcUpdate = webRtcUpdate;
        }

        public void BindCamera(WebCameraIndex cameraIndex, int width = 1280, int height = 720, int fps = 25)
        {
            BoundCameraIndex = cameraIndex;
            string deviceName = cameraIndex?.ResolveDeviceName() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                throw new InvalidOperationException("没有可用的摄像头设备。");
            }

            CameraTexture = new WebCamTexture(deviceName, width, height, fps);
            CameraTexture.Play();
            VideoTrack = new VideoStreamTrack(CameraTexture);
        }

        public void BindRenderTexture(RenderTexture renderTexture)
        {
            BoundRenderTexture = renderTexture;
            if (renderTexture == null)
            {
                throw new ArgumentNullException(nameof(renderTexture));
            }

            VideoTrack = new VideoStreamTrack(renderTexture);
        }

        /// <summary>
        /// 绑定要送入 WebRTC 的 <see cref="AudioSource"/>。
        /// Unity WebRTC 的 <see cref="AudioStreamTrack"/> 需要该 AudioSource 正在播放有效音频；若 <c>clip == null</c>，
        /// 会自动用 <see cref="Microphone.Start"/> 把默认（或 <paramref name="microphoneDeviceName"/>）麦克风接到该 AudioSource。
        /// </summary>
        /// <param name="audioSource">用于承载采集的 AudioSource（勿与 <see cref="AudioListener"/> 同物体）。</param>
        /// <param name="microphoneDeviceName">可选，<see cref="Microphone.devices"/> 中的设备名；为空则用第一个设备。</param>
        public void BindAudioSource(AudioSource audioSource, string microphoneDeviceName = null)
        {
            StopMicCaptureIfOwned();
            AudioTrack?.Dispose();
            AudioTrack = null;
            BoundAudioSource = audioSource;

            if (audioSource == null)
            {
                return;
            }

            if (audioSource.clip == null)
            {
                if (Microphone.devices == null || Microphone.devices.Length == 0)
                {
                    Debug.LogWarning("[WebRTCSender] BindAudioSource: AudioSource 无 clip，且本机无麦克风设备，已跳过音频轨");
                    return;
                }

                micDeviceName = string.IsNullOrWhiteSpace(microphoneDeviceName)
                    ? Microphone.devices[0]
                    : ResolveMicrophoneDeviceName(microphoneDeviceName);
                Debug.Log("[WebRTCSender] BindAudioSource->micDeviceName:" + micDeviceName);
                if (string.IsNullOrEmpty(micDeviceName))
                {
                    micDeviceName = Microphone.devices[0];
                }

                if (Microphone.IsRecording(micDeviceName))
                {
                    Microphone.End(micDeviceName);
                }

                Microphone.GetDeviceCaps(micDeviceName, out int minFreq, out int maxFreq);
                int sampleRate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
                if (minFreq > 0)
                {
                    int hi = maxFreq > 0 ? maxFreq : minFreq;
                    sampleRate = Mathf.Clamp(sampleRate, minFreq, hi);
                }

                const int ringSeconds = 10;
                AudioClip micClip = Microphone.Start(micDeviceName, true, ringSeconds, sampleRate);
                if (micClip == null)
                {
                    Debug.LogWarning("[WebRTCSender] Microphone.Start 返回 null，已跳过音频轨");
                    micDeviceName = null;
                    return;
                }

                audioSource.clip = micClip;
                audioSource.loop = true;
                micCaptureOwnedBySender = true;
                Debug.Log($"[WebRTCSender] 已绑定麦克风到 AudioSource: device={micDeviceName}, sampleRate={sampleRate}");
            }
            else
            {
                micCaptureOwnedBySender = false;
                micDeviceName = null;
            }

            if (!audioSource.isPlaying)
            {
                audioSource.Play();
            }

            AudioTrack = new AudioStreamTrack(audioSource);
        }

        private static string ResolveMicrophoneDeviceName(string preferred)
        {
            foreach (string d in Microphone.devices)
            {
                if (string.Equals(d, preferred, StringComparison.OrdinalIgnoreCase))
                {
                    return d;
                }
            }

            foreach (string d in Microphone.devices)
            {
                if (d.IndexOf(preferred, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return d;
                }
            }

            return null;
        }

        private void StopMicCaptureIfOwned()
        {
            if (!micCaptureOwnedBySender)
            {
                return;
            }

            if (BoundAudioSource != null)
            {
                BoundAudioSource.Stop();
                BoundAudioSource.clip = null;
            }

            if (!string.IsNullOrEmpty(micDeviceName) && Microphone.IsRecording(micDeviceName))
            {
                Microphone.End(micDeviceName);
            }

            micDeviceName = null;
            micCaptureOwnedBySender = false;
        }

        public async Task<bool> StartPublishing(string streamId, int port)
        {
            if (IsPublishing)
            {
                return true;
            }

            StreamId = streamId;
            AllocatedPort = port;

            try
            {
                Debug.Log($"[WebRTCSender] StartPublishing begin, streamId={streamId}, port={port}");
                EnsureWebRTCInitialized();
                Debug.Log("[WebRTCSender] WebRTC initialized");
                BuildPeerConnection();
                Debug.Log("[WebRTCSender] PeerConnection created");
                BuildMediaStreamAndTracks();
                Debug.Log("[WebRTCSender] Media tracks bound");

                Debug.Log("[WebRTCSender] CreateOffer...");
                var offerOp = PeerConnection.CreateOffer();
                await AwaitRtcYieldInstructionAsync(offerOp);
                if (offerOp.IsError)
                {
                    throw new InvalidOperationException($"CreateOffer failed: {FormatRtcError(offerOp.Error)}");
                }
                Debug.Log("[WebRTCSender] CreateOffer success");

                RTCSessionDescription offer = offerOp.Desc;
                Debug.Log($"[WebRTCSender] Offer SDP:\n{offer.sdp}");
                Debug.Log("[WebRTCSender] SetLocalDescription...");
                var setLocalOp = PeerConnection.SetLocalDescription(ref offer);
                await AwaitRtcYieldInstructionAsync(setLocalOp);
                if (setLocalOp.IsError)
                {
                    throw new InvalidOperationException($"SetLocalDescription failed: {FormatRtcError(setLocalOp.Error)}");
                }

                Debug.Log("[WebRTCSender] SetLocalDescription success");
                string localSdpAfterSetLocal = SafeGetLocalSdpOrFallback(ref offer);
                Debug.Log($"[WebRTCSender] LocalDescription SDP:\n{localSdpAfterSetLocal ?? "(null)"}");

                Debug.Log("[WebRTCSender] Waiting ICE gathering complete...");
                await WaitForIceGatheringComplete(TimeSpan.FromSeconds(5));
                Debug.Log("[WebRTCSender] ICE gathering completed or timeout");
                Debug.Log("[WebRTCSender] Exchange SDP via signaling...");
                string offerSdpForSignaling = SafeGetLocalSdpOrFallback(ref offer);
                if (string.IsNullOrWhiteSpace(offerSdpForSignaling))
                {
                    throw new InvalidOperationException("本地 SDP 为空，无法进行 ZLM 信令交换（LocalDescription 与 Offer 均无有效 sdp）。");
                }

                string answerSdp = await ExchangeSdpAsync(offerSdpForSignaling);
                if (string.IsNullOrWhiteSpace(answerSdp))
                {
                    throw new InvalidOperationException("对端返回的 Answer SDP 为空。");
                }

                var answer = new RTCSessionDescription { sdp = answerSdp, type = RTCSdpType.Answer };

                Debug.Log("[WebRTCSender] SetRemoteDescription...");
                var setRemoteOp = PeerConnection.SetRemoteDescription(ref answer);
                await AwaitRtcYieldInstructionAsync(setRemoteOp);
                if (setRemoteOp.IsError)
                {
                    throw new InvalidOperationException($"SetRemoteDescription failed: {FormatRtcError(setRemoteOp.Error)}");
                }

                Debug.Log("[WebRTCSender] SetRemoteDescription success");
                Debug.Log($"[WebRTCSender] RemoteDescription SDP:\n{answer.sdp}");

                IsPublishing = true;
                Debug.Log("[WebRTCSender] StartPublishing success");
                return true;
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex.Message);
                Debug.LogError($"[WebRTCSender] StartPublishing error: {ex.Message}");
                await StopPublishing();
                return false;
            }
        }

        public virtual async Task StopPublishing()
        {
            if (CameraTexture != null && CameraTexture.isPlaying)
            {
                CameraTexture.Stop();
            }

            VideoTrack?.Dispose();
            VideoTrack = null;

            AudioTrack?.Dispose();
            AudioTrack = null;
            StopMicCaptureIfOwned();

            MediaStream?.Dispose();
            MediaStream = null;

            if (PeerConnection != null)
            {
                PeerConnection.Close();
                PeerConnection.Dispose();
                PeerConnection = null;
            }

            IsPublishing = false;
            await Task.CompletedTask;
        }

        protected virtual void BuildPeerConnection()
        {
            RTCConfiguration config = new RTCConfiguration
            {
                iceServers = new[]
                {
                    new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } }
                }
            };

            PeerConnection = new RTCPeerConnection(ref config);
            PeerConnection.OnConnectionStateChange = HandlePeerConnectionStateChanged;
        }

        private void HandlePeerConnectionStateChanged(RTCPeerConnectionState state)
        {
            string name = state.ToString();
            Debug.Log($"[WebRTCSender] ConnectionState={name}");
            OnConnectionStateChanged?.Invoke(name);
            if (state == RTCPeerConnectionState.Disconnected
                || state == RTCPeerConnectionState.Failed
                || state == RTCPeerConnectionState.Closed)
            {
                ScheduleDisconnectCleanup(name);
            }
        }

        private void ScheduleDisconnectCleanup(string connectionStateName)
        {
            if (disconnectCleanupScheduled)
            {
                return;
            }

            disconnectCleanupScheduled = true;
            if (coroutineHost != null)
            {
                coroutineHost.StartCoroutine(CoDisconnectCleanup(connectionStateName));
            }
            else
            {
                _ = DisconnectCleanupAsync(connectionStateName);
            }
        }

        private IEnumerator CoDisconnectCleanup(string connectionStateName)
        {
            yield return null;
            Task t = DisconnectCleanupAsync(connectionStateName);
            while (!t.IsCompleted)
            {
                yield return null;
            }
        }

        private async Task DisconnectCleanupAsync(string connectionStateName)
        {
            try
            {
                await StopPublishing();
                StopWebRtcUpdateDriver();
                OnDisconnected?.Invoke(this, connectionStateName);
                Debug.Log($"[WebRTCSender] 连接已结束并完成清理: {connectionStateName}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[WebRTCSender] DisconnectCleanup 异常: {ex.Message}");
            }
            finally
            {
                disconnectCleanupScheduled = false;
            }
        }

        /// <summary>停止 WebRTC.Update 协程并重置初始化标记，便于下次推流重新 EnsureWebRTCInitialized。</summary>
        private void StopWebRtcUpdateDriver()
        {
            if (webRtcUpdateCoroutine != null && coroutineHost != null)
            {
                coroutineHost.StopCoroutine(webRtcUpdateCoroutine);
                webRtcUpdateCoroutine = null;
            }

            webRtcInitialized = false;
        }

        protected virtual void BuildMediaStreamAndTracks()
        {
            MediaStream = new MediaStream();
            bool hasAnyTrack = false;
            if (VideoTrack != null)
            {
                MediaStream.AddTrack(VideoTrack);
                PeerConnection.AddTrack(VideoTrack, MediaStream);
                hasAnyTrack = true;
            }

            if (AudioTrack != null)
            {
                MediaStream.AddTrack(AudioTrack);
                PeerConnection.AddTrack(AudioTrack, MediaStream);
                hasAnyTrack = true;
            }

            if (!hasAnyTrack)
            {
                throw new InvalidOperationException("未绑定任何音视频轨道，无法创建有效 WebRTC Offer（SDP 无 m 行）。");
            }
        }

        protected abstract Task<string> ExchangeSdpAsync(string offerSdp);

        protected static async Task<string> PostSdpAsync(string url, string sdp)
        {
            using var req = new UnityWebRequest(url, "POST");
            byte[] payload = Encoding.UTF8.GetBytes(sdp);
            req.uploadHandler = new UploadHandlerRaw(payload);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/sdp");

            var op = req.SendWebRequest();
            while (!op.isDone)
            {
                await Task.Yield();
            }

            if (req.result != UnityWebRequest.Result.Success)
            {
                throw new InvalidOperationException($"HTTP error: {req.error}, code={req.responseCode}, body={req.downloadHandler?.text}");
            }

            Debug.Log($"[WebRTCSender] Signaling HTTP response code={req.responseCode}");
            return req.downloadHandler.text;
        }

        /// <summary>
        /// com.unity.webrtc 在部分失败路径下会置 <c>IsError=true</c>，但返回 <c>default(RTCError)</c>（message 为空），
        /// 这里统一兜底，避免直接读空消息导致错误信息缺失。
        /// </summary>
        private static string FormatRtcError(RTCError error)
        {
            if (string.IsNullOrEmpty(error.message) && error.errorType == RTCErrorType.None)
            {
                return "(RTCError 为空，无详细原因)";
            }

            return string.IsNullOrEmpty(error.message) ? error.ToString() : error.message;
        }

        private void EnsureWebRTCInitialized()
        {
            if (webRtcInitialized)
            {
                return;
            }

            // com.unity.webrtc 3.x 起已移除 WebRTC.Initialize()，由包在首次使用时自行初始化；必须驱动 WebRTC.Update()。
            if (coroutineHost == null)
            {
                throw new InvalidOperationException(
                    "未调用 SetCoroutineHost(MonoBehaviour)：无法启动 WebRTC.Update() 协程，CreateOffer/SetDescription 会异常或卡死。");
            }

            webRtcInitialized = true;

            if (m_webRtcUpdate)
            {
                webRtcUpdateCoroutine = coroutineHost.StartCoroutine(global::Unity.WebRTC.WebRTC.Update());
            }
        }

        /// <summary>
        /// 部分环境下 SetLocalDescription 后一帧内 <see cref="RTCPeerConnection.LocalDescription"/> 仍可能为 null，避免对 .sdp 直接解引用。
        /// </summary>
        private string SafeGetLocalSdpOrFallback(ref RTCSessionDescription offer)
        {
            try
            {
                if (PeerConnection != null)
                {
                    RTCSessionDescription? local = PeerConnection.LocalDescription;
                    if (local.HasValue && !string.IsNullOrWhiteSpace(local.Value.sdp))
                    {
                        return local.Value.sdp;
                    }
                }
            }
            catch
            {
                // 个别版本访问 LocalDescription 可能抛异常，回退到 offer
            }

            return offer.sdp;
        }

        /// <summary>
        /// RTC 异步操作为 <see cref="CustomYieldInstruction"/>，必须在驱动了 <c>WebRTC.Update()</c> 的同一线程/帧循环里用协程 <c>yield return op</c> 等待。
        /// 仅用 <c>Task.Yield</c> 轮询 <c>keepWaiting</c> 可能与 <c>WebRTC.Update()</c> 不同步，表现为 <see cref="RTCPeerConnection.SetRemoteDescription"/> 等永远卡住或异常。
        /// </summary>
        private Task AwaitRtcYieldInstructionAsync(CustomYieldInstruction op)
        {
            if (op == null)
            {
                throw new ArgumentNullException(nameof(op));
            }

            if (coroutineHost == null)
            {
                throw new InvalidOperationException("coroutineHost 为空，无法协程等待 RTC 操作。");
            }

            var tcs = new TaskCompletionSource<bool>();
            coroutineHost.StartCoroutine(RunRtcYieldInstructionThenComplete(op, tcs));
            return tcs.Task;
        }

        private static IEnumerator RunRtcYieldInstructionThenComplete(CustomYieldInstruction op, TaskCompletionSource<bool> tcs)
        {
            yield return op;
            tcs.TrySetResult(true);
        }

        private async Task WaitForIceGatheringComplete(TimeSpan timeout)
        {
            RTCPeerConnection pc = PeerConnection;
            if (pc == null)
            {
                throw new InvalidOperationException("PeerConnection 为空，无法等待 ICE。");
            }

            var tcs = new TaskCompletionSource<bool>();
            void Handler(RTCIceGatheringState state)
            {
                if (state == RTCIceGatheringState.Complete)
                {
                    pc.OnIceGatheringStateChange = null;
                    tcs.TrySetResult(true);
                }
            }

            pc.OnIceGatheringStateChange = Handler;
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
            if (done != tcs.Task)
            {
                pc.OnIceGatheringStateChange = null;
            }
        }

        public virtual void Dispose()
        {
            try
            {
                StopPublishing().GetAwaiter().GetResult();
            }
            catch
            {
                // ignore dispose exceptions
            }

            if (webRtcInitialized)
            {
                StopWebRtcUpdateDriver();
            }
        }
    }
}
