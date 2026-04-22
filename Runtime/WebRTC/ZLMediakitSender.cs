using System;
using System.Threading.Tasks;

namespace ZLMediakitPlugin.WebRTC
{
    /// <summary>
    /// 基于 ZLMediaKit 的 WebRTC Sender，实现 offer/answer HTTP 交换。
    /// </summary>
    public sealed class ZLMediakitSender : WebRTCSender
    {
        private readonly string zlmWebRtcApi;
        private readonly string app;
        private readonly string vhost;
        private readonly string sign;
        private readonly string callId;
        private readonly string secret;

        public ZLMediakitSender(string zlmWebRtcApi, string app = "live", string vhost = "__defaultVhost__", string sign = "", string callId = "", string secret = "")
        {
            this.zlmWebRtcApi = zlmWebRtcApi?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(zlmWebRtcApi));
            this.app = app;
            this.vhost = vhost;
            this.sign = sign;
            this.callId = callId;
            this.secret = secret;
        }

        protected override async Task<string> ExchangeSdpAsync(string offerSdp)
        {
            if (string.IsNullOrWhiteSpace(StreamId))
            {
                throw new InvalidOperationException("StreamId 为空，无法拼接 ZLM WebRTC 推流 URL。");
            }

            string url = $"{zlmWebRtcApi}?app={Uri.EscapeDataString(app)}&stream={Uri.EscapeDataString(StreamId)}&type=push&vhost={Uri.EscapeDataString(vhost)}";
            if (!string.IsNullOrWhiteSpace(sign))
            {
                url += $"&sign={Uri.EscapeDataString(sign)}";
            }
            if (!string.IsNullOrWhiteSpace(callId))
            {
                // WVP 鉴权侧通常按原始 Call-ID 做关联，避免将 '@' 编码成 '%40' 导致匹配失败。
                url += $"&callId={callId}";
            }
            if (!string.IsNullOrWhiteSpace(secret))
            {
                url += $"&secret={Uri.EscapeDataString(secret)}";
            }
            UnityEngine.Debug.Log($"[ZLMediakitSender] WebRTC 协商请求 URL: {url}");
            string answerRaw = await PostSdpAsync(url, offerSdp);
            UnityEngine.Debug.Log($"[ZLMediakitSender] WebRTC 协商原始应答: {answerRaw}");
            return ParseZlmAnswerSdp(answerRaw);
        }

        private static string ParseZlmAnswerSdp(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new InvalidOperationException("ZLM 返回空应答。");
            }

            string trimmed = raw.Trim();
            if (!trimmed.StartsWith("{"))
            {
                return trimmed;
            }

            var wrapper = UnityEngine.JsonUtility.FromJson<ZlmWebRtcResponse>(trimmed);
            if (wrapper == null)
            {
                throw new InvalidOperationException($"ZLM webrtc api JSON 解析失败: {trimmed}");
            }

            if (wrapper.code != 0 || string.IsNullOrWhiteSpace(wrapper.sdp))
            {
                throw new InvalidOperationException($"ZLM webrtc api 返回异常: {trimmed}");
            }

            UnityEngine.Debug.Log($"[ZLMediakitSender] WebRTC Answer SDP 解析成功:\n{wrapper.sdp}");

            return wrapper.sdp;
        }

        [Serializable]
        private class ZlmWebRtcResponse
        {
            public int code;
            public string msg;
            public string sdp;
        }
    }
}
