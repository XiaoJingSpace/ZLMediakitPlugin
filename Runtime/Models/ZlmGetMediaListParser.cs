using System;
using UnityEngine;

namespace ZLMediakitPlugin.Models
{
    /// <summary>
    /// ZLM <c>getMediaList</c> 应答 JSON 的精简反序列化（仅解析拼接 WebRTC 拉流所需字段）。
    /// </summary>
    [Serializable]
    public class ZlmGetMediaListRoot
    {
        public int code;
        public string cookie;
        public ZlmGetMediaListEntry[] data;
    }

    [Serializable]
    public class ZlmGetMediaListEntry
    {
        public string app;
        public string stream;
        public string vhost;
        public string schema;
        public string originTypeStr;
        public ZlmGetMediaListTrack[] tracks;
    }

    [Serializable]
    public class ZlmGetMediaListTrack
    {
        public int codec_type;
        public string codec_id_name;
    }

    public static class ZlmGetMediaListParser
    {
        /// <summary>
        /// 从 <c>getMediaList</c> 原始 JSON 中取一条 <c>rtc_push</c> 记录，拼接与 ZLM 控制台一致的 WebRTC 拉流地址（<c>type=play</c>）。
        /// </summary>
        public static bool TryBuildWebrtcPlayUrlsFromGetMediaListJson(
            string json,
            string host,
            int rtcHttpPort,
            int rtcHttpsPort,
            out string rtcHttpUrl,
            out string rtcHttpsUrl,
            out string summary)
        {
            rtcHttpUrl = string.Empty;
            rtcHttpsUrl = string.Empty;
            summary = string.Empty;

            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(host) || rtcHttpPort <= 0 || rtcHttpsPort <= 0)
            {
                return false;
            }

            ZlmGetMediaListRoot root;
            try
            {
                root = JsonUtility.FromJson<ZlmGetMediaListRoot>(json);
            }
            catch (Exception ex)
            {
                summary = "JSON 解析失败: " + ex.Message;
                return false;
            }

            if (root == null || root.code != 0 || root.data == null || root.data.Length == 0)
            {
                summary = $"code={root?.code ?? -1}, data 为空或不存在";
                return false;
            }

            ZlmGetMediaListEntry pick = null;
            for (int i = 0; i < root.data.Length; i++)
            {
                ZlmGetMediaListEntry e = root.data[i];
                if (e == null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(e.originTypeStr)
                    && e.originTypeStr.IndexOf("rtc_push", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    pick = e;
                    break;
                }
            }

            pick ??= root.data[0];
            if (pick == null || string.IsNullOrWhiteSpace(pick.app) || string.IsNullOrWhiteSpace(pick.stream))
            {
                summary = "未找到有效的 app/stream";
                return false;
            }

            InferAudioVideoCodecs(pick.tracks, out string audioCodec, out string videoCodec);
            string originTypeStr = string.IsNullOrWhiteSpace(pick.originTypeStr) ? "rtc_push" : pick.originTypeStr.Trim();

            string queryCore =
                "app=" + Uri.EscapeDataString(pick.app.Trim())
                + "&stream=" + Uri.EscapeDataString(pick.stream.Trim())
                + "&type=play"
                + "&originTypeStr=" + Uri.EscapeDataString(originTypeStr)
                + "&audioCodec=" + Uri.EscapeDataString(audioCodec)
                + "&videoCodec=" + Uri.EscapeDataString(videoCodec);

            if (!string.IsNullOrWhiteSpace(pick.vhost)
                && !string.Equals(pick.vhost.Trim(), "__defaultVhost__", StringComparison.Ordinal))
            {
                queryCore += "&vhost=" + Uri.EscapeDataString(pick.vhost.Trim());
            }

            rtcHttpUrl = $"http://{host}:{rtcHttpPort}/index/api/webrtc?{queryCore}";
            rtcHttpsUrl = $"https://{host}:{rtcHttpsPort}/index/api/webrtc?{queryCore}";
            summary =
                $"取自 data[{Array.IndexOf(root.data, pick)}]: app={pick.app}, stream={pick.stream}, vhost={pick.vhost}, originTypeStr={originTypeStr}, schema={pick.schema}, 推断 audioCodec={audioCodec}, videoCodec={videoCodec}";
            return true;
        }

        private static void InferAudioVideoCodecs(ZlmGetMediaListTrack[] tracks, out string audioCodec, out string videoCodec)
        {
            audioCodec = "AAC";
            videoCodec = "H264";
            if (tracks == null || tracks.Length == 0)
            {
                return;
            }

            bool videoSet = false;
            bool audioSet = false;
            for (int i = 0; i < tracks.Length; i++)
            {
                ZlmGetMediaListTrack t = tracks[i];
                if (t == null)
                {
                    continue;
                }

                string name = t.codec_id_name ?? string.Empty;
                if (t.codec_type == 0 && !videoSet)
                {
                    if (NameHintsH265(name))
                    {
                        videoCodec = "H265";
                    }
                    else if (NameHintsH264(name))
                    {
                        videoCodec = "H264";
                    }

                    videoSet = true;
                }
                else if (t.codec_type == 1 && !audioSet)
                {
                    if (NameHintsG711(name))
                    {
                        audioCodec = name.IndexOf("u", StringComparison.OrdinalIgnoreCase) >= 0 ? "PCMU" : "PCMA";
                    }
                    else if (NameHintsAac(name))
                    {
                        audioCodec = "AAC";
                    }

                    audioSet = true;
                }
            }
        }

        private static bool NameHintsH264(string name)
        {
            return name.IndexOf("264", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("avc", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool NameHintsH265(string name)
        {
            return name.IndexOf("265", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("hevc", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool NameHintsAac(string name)
        {
            return name.IndexOf("aac", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("mpeg4-generic", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool NameHintsG711(string name)
        {
            return name.IndexOf("711", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("g711", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("pcma", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("pcmu", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
