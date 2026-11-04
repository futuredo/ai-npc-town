using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace Town.Editor
{
    /// <summary>命令行渲染场景截图并检查 NavMesh，用于不打开编辑器时验证场景。</summary>
    public static class SceneSnapshot
    {
        public static void CaptureFromCommandLine()
        {
            EditorSceneManager.OpenScene(GrayboxSceneBuilder.ScenePath);
            var tri = NavMesh.CalculateTriangulation();
            Debug.Log($"[Town] NavMesh 三角形数={tri.indices.Length / 3} 顶点数={tri.vertices.Length}");

            var cam = Camera.main;
            var rt = new RenderTexture(1280, 720, 24);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
            tex.Apply();
            cam.targetTexture = null;
            RenderTexture.active = null;

            string outPath = Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/graybox_snapshot.png"));
            File.WriteAllBytes(outPath, tex.EncodeToPNG());
            Debug.Log($"[Town] 截图已保存：{outPath}");
        }
    }
}
