using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using ZLMediakitPlugin.Core;
using ZLMediakitPlugin.Models;

namespace ZLMediakitPlugin.Samples
{
    public sealed class ExampleUsage : MonoBehaviour
    {
        [Header("Device")]
        [SerializeField] private string deviceId = "34020000001320000001";

        [Header("Video Source")]
        [SerializeField] private WebCameraIndex cameraIndex;
        [SerializeField] private RenderTexture renderTexture;

        [Header("Audio Source")]
        [SerializeField] private AudioSource audioSource;

        [Header("UI")]
        [SerializeField] private Button startButton;
        [SerializeField] private Button stopButton;
        [SerializeField] private TextMeshProUGUI statusText;
        [SerializeField] private RawImage Preview;

        private void Start()
        {
            if (startButton != null) startButton.onClick.AddListener(OnStartClicked);
            if (stopButton != null) stopButton.onClick.AddListener(OnStopClicked);

            if (ZLMediakitPluginManager.Instance == null)
            {
                statusText.text = "请先在场景中放置 ZLMediakitPluginManager";
                return;
            }
            ZLMediakitPluginManager.Instance.cameraIndex = cameraIndex;
            ZLMediakitPluginManager.Instance.renderTexture = renderTexture;
            ZLMediakitPluginManager.Instance.audioSource = audioSource;

            ZLMediakitPluginManager.Instance.OnPortAllocated += OnPortAllocated;
            ZLMediakitPluginManager.Instance.OnStreamStarted += OnStreamStarted;
            ZLMediakitPluginManager.Instance.OnStreamFailed += OnStreamFailed;
            ZLMediakitPluginManager.Instance.OnStreamStopped += OnStreamStopped;
        }

        private async void OnStartClicked()
        {
            await StartTaskAsync();
        }

        private async Task StartTaskAsync()
        {
            if (ZLMediakitPluginManager.Instance == null)
            {
                return;
            }

            statusText.text = "申请端口中...";
            startButton.interactable = false;

            bool ok = await ZLMediakitPluginManager.Instance.StartStreaming(
                deviceId,
                cameraIndex,
                renderTexture,
                audioSource);

            if (!ok)
            {
                startButton.interactable = true;
                statusText.text = "启动失败";
            }

           
        }

        private async void OnStopClicked()
        {
            if (ZLMediakitPluginManager.Instance == null)
            {
                return;
            }

            statusText.text = "停止中...";
            await ZLMediakitPluginManager.Instance.StopStreaming();
            startButton.interactable = true;
        }

        private void OnPortAllocated(int port)
        {
            statusText.text = $"端口已分配: {port}";
        }

        private void OnStreamStarted()
        {
            statusText.text = "推流中";
            Preview.texture = ZLMediakitPluginManager.Instance.currentSender.CameraTexture;
        }

        private void OnStreamFailed(string reason)
        {
            statusText.text = $"失败: {reason}";
            startButton.interactable = true;
        }

        private void OnStreamStopped()
        {
            statusText.text = "已停止";
        }

        private void OnDestroy()
        {
            if (ZLMediakitPluginManager.Instance == null)
            {
                return;
            }

            ZLMediakitPluginManager.Instance.OnPortAllocated -= OnPortAllocated;
            ZLMediakitPluginManager.Instance.OnStreamStarted -= OnStreamStarted;
            ZLMediakitPluginManager.Instance.OnStreamFailed -= OnStreamFailed;
            ZLMediakitPluginManager.Instance.OnStreamStopped -= OnStreamStopped;
        }
    }
}
