using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Town.Editor
{
    public static class EditorCameraCapture
    {
        /// <summary>URP 使用显式单相机请求；释放临时对象并还原相机与活动 RT。</summary>
        public static void Save(Camera camera, string path, int width, int height)
        {
            if (!camera) throw new ArgumentNullException(nameof(camera));
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            var rt = new RenderTexture(width, height, 24);
            Texture2D image = null;
            try
            {
                rt.Create();
                camera.targetTexture = rt;
                if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset)
                {
                    var request = new UniversalRenderPipeline.SingleCameraRequest { destination = rt };
                    if (!RenderPipeline.SupportsRenderRequest(camera, request))
                        throw new InvalidOperationException("当前渲染管线不支持单相机截图请求。");
                    RenderPipeline.SubmitRenderRequest(camera, request);
                }
                else camera.Render();
                RenderTexture.active = rt;
                image = new Texture2D(width, height, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                image.Apply();
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                File.WriteAllBytes(path, image.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                if (image) UnityEngine.Object.DestroyImmediate(image);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }
    }
}
