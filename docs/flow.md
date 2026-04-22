# ZLMediakitPlugin 整体流程（含用户登录）

## 流程概览

- 用户登录（WVP HTTP）用于获取业务调用权限（`accessToken`/`pushKey`）
- 设备注册（SIP REGISTER）用于让 GB28181 设备在线
- 点播触发后，平台反向发 `INVITE` 下发收流参数
- Unity 在拿到收流端口后发起 WebRTC 推流协商

## 时序图

```mermaid
sequenceDiagram
    autonumber
    participant Unity as Unity端(SIP+WebRTC)
    participant WVP as WVP平台
    participant ZLM as ZLMediaKit

    Note over Unity,WVP: 0) 用户登录（业务权限）
    Unity->>WVP: /api/user/login?username=...&password=md5(...)
    WVP-->>Unity: code=0 + accessToken(+pushKey)
    Unity->>Unity: 缓存 token / pushKey

    Note over Unity,WVP: 1) 设备SIP注册（设备在线）
    Unity->>WVP: REGISTER
    WVP-->>Unity: 401
    Unity->>WVP: REGISTER + Digest
    WVP-->>Unity: 200 OK
    loop 保活
      Unity->>WVP: MESSAGE Keepalive
      WVP-->>Unity: 200 OK
    end

    Note over Unity,WVP: 2) 点击Start触发点播
    Unity->>WVP: /api/play/start/{deviceId}/{channelId} (带token)
    WVP->>ZLM: openRtpServer
    ZLM-->>WVP: 收流端口(如42214)
    WVP->>Unity: SIP INVITE(含IP/端口/SSRC)
    Unity-->>WVP: 200 OK
    WVP->>Unity: ACK

    Note over Unity,ZLM: 3) WebRTC推流协商
    Unity->>Unity: 解析INVITE端口并组装streamId
    Unity->>ZLM: /index/api/webrtc?app=...&stream=...&type=push&...
    Note over Unity,ZLM: 携带鉴权参数(sign/callId/secret等)
    ZLM-->>Unity: Answer SDP
    Unity->>Unity: setRemoteDescription
    Note over Unity,ZLM: 推流建立

    opt 停止
      Unity->>WVP: /api/play/stop...
      Unity->>Unity: 关闭PC/释放资源
    end
```

## 关键说明

- 收流端口由平台下发，表示本次点播会话的媒体接收目标端口。
- 拿到收流端口后即可发起 WebRTC 协商；若协商接口鉴权失败，会表现为点播超时（`code=-2`）。
- SIP 在线与 WebRTC 鉴权是两条链路：SIP 成功不代表 WebRTC 推流接口一定有权限。
