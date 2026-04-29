using System.Text;
using UnityEngine;
using Unity.MLAgents.Sensors;

public class RayDebug : MonoBehaviour
{
    RayPerceptionSensorComponent3D rayComponent;
    public int logEveryNFrames = 10;

    int frameCounter;

    void Awake()
    {
        rayComponent = GetComponent<RayPerceptionSensorComponent3D>();
    }

    void Update()
    {
        frameCounter++;
        if (frameCounter % logEveryNFrames != 0)
        {
            return;
        }

        if (rayComponent == null) return;

        var input = rayComponent.GetRayPerceptionInput();
        var output = RayPerceptionSensor.Perceive(input);
        var tags = input.DetectableTags;
        var rays = output.RayOutputs;
        if (rays == null || rays.Length == 0) return;

        var sb = new StringBuilder();
        sb.Append("RayDebug: ");

        for (int i = 0; i < rays.Length; i++)
        {
            var r = rays[i];
            if (!r.HasHit) continue;

            string tagName = "none";
            if (r.HitTaggedObject && r.HitTagIndex >= 0 && r.HitTagIndex < tags.Count)
            {
                tagName = tags[r.HitTagIndex];
            }

            string objName = r.HitGameObject != null ? r.HitGameObject.name : "null";
            sb.AppendFormat("[{0}] hit=True tag={1} obj={2} distNorm={3:F2}; ", i, tagName, objName, r.HitFraction);

            // 【新增】：如果打到了 Wall 或 Cube，画出它的包围盒，并打印它的坐标
            if (r.HitGameObject != null && (objName.Contains("Wall") || objName.Contains("Cube")))
            {
                Collider col = r.HitGameObject.GetComponent<Collider>();
                if (col != null)
                {
                    Debug.Log($"<color=red>警告！</color> 射线打到了 {objName}！它的中心坐标是 {col.bounds.center}，大小是 {col.bounds.size}。快去看看它是不是包住了小球！");
                    // 在 Scene 窗口画一个黄色的框，持续 2 秒
                    Debug.DrawLine(col.bounds.min, col.bounds.max, Color.yellow, 2f);
                }
            }
        }
        
        Debug.Log(sb.ToString());
    }
}