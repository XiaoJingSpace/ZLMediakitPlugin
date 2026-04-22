# ZLMediakitPlugin

Unity 插件：用于业务点击“开始任务”后，向 WVP PRO 发起 SIP 端口协商，端口就绪后推送 WebRTC 到 ZLMediaKit。

## 功能概览

- `SIPClient`：负责与 WVP PRO 的 SIP INVITE/SDP 协商，解析媒体端口。
- `WebRTCSender`：WebRTC 发送基类，支持绑定 Unity 摄像头索引、RenderTexture、AudioSource。
- `ZLMediakitSender`：继承 `WebRTCSender`，实现与 ZLMediaKit 的 HTTP offer/answer 交换并建立推流。
- `ZLMediakitPluginManager`：业务统一入口，串联“端口申请 -> WebRTC 推流”。

## 目录

- `Runtime/Core`：管理器与配置模型
- `Runtime/SIP`：SIP 交互逻辑
- `Runtime/WebRTC`：推流抽象与实现
- `Runtime/Models`：通用模型
- `Samples~`：示例脚本

## 快速接入

1. 安装 Unity WebRTC 包（`com.unity.webrtc`）。
2. 将 `ZLMediakitPlugin` 作为 UPM 本地包或直接复制到 `Assets` 下。
3. 场景中添加 `ZLMediakitPluginManager` 组件并配置：
   - SIP Server Address / Port / Domain
   - SIP Server ID
   - ZLM WebRTC URL（默认 `/index/api/webrtc`）
4. 业务代码调用：
   - `StartStreaming(deviceId, cameraIndex, renderTexture, audioSource)`
   - 或监听 `OnPortAllocated` 后调用 `StartWebRTCStreaming(...)`

## 说明

- 当前 `SIPClient` 使用标准 UDP SIP 文本报文方式，适合对接 WVP PRO 的 INVITE 协商场景。
- 若你的 WVP 部署有鉴权、TCP/TLS、特殊头字段，可在 `SIPClient.BuildInviteRequest` 中扩展。
- `ZLMediakitSender` 默认向 `?app=live&stream={streamId}&type=push` 发起 WebRTC 协商，可按实际服务端参数调整。
