using System;
using UnityEngine;

namespace ZLMediakitPlugin.Models
{
    [Serializable]
    public class WebCameraIndex
    {
        [Tooltip("WebCamTexture.devices 的索引")]
        public int Index = 0;

        [Tooltip("可选：直接指定设备名，优先级高于 Index")]
        public string DeviceName = string.Empty;

        public string ResolveDeviceName()
        {
            if (!string.IsNullOrWhiteSpace(DeviceName))
            {
                return DeviceName.Trim();
            }

            var devices = WebCamTexture.devices;
            if (devices == null || devices.Length == 0)
            {
                return string.Empty;
            }

            if (Index < 0 || Index >= devices.Length)
            {
                return devices[0].name;
            }

            return devices[Index].name;
        }
    }
}
