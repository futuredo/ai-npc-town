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
            string outPath = Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/graybox_snapshot.png"));
            EditorCameraCapture.Save(cam, outPath, 1280, 720);
            Debug.Log($"[Town] 截图已保存：{outPath}");
        }
    }
}
