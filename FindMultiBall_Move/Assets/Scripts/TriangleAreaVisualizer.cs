using UnityEngine;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class TriangleAreaVisualizer : MonoBehaviour
{
    [Header("三角形顶点（XZ平面，Y=0）")]
    public Vector3 pointA = new Vector3(-18, 0, -18);
    public Vector3 pointB = new Vector3(-36, 0, -36);
    public Vector3 pointC = new Vector3(0, 0, -36);

    [Header("显示选项")]
    public bool drawGizmos = true;      // 是否在Scene视图中用Gizmos绘制线框
    public bool createMesh = true;      // 是否创建一个实际的Mesh物体（运行时可见）
    public Color gizmoColor = Color.green;
    public Color meshColor = Color.green; // 不透明绿

    void Start()
    {
        if (createMesh)
            CreateTriangleMesh();
    }

    // 创建一个Mesh物体，显示半透明三角形
    void CreateTriangleMesh()
    {
        MeshFilter mf = GetComponent<MeshFilter>();
        MeshRenderer mr = GetComponent<MeshRenderer>();

        // 创建Mesh数据
        Mesh mesh = new Mesh();
        mesh.name = "TriangleMesh";

        // 顶点（注意顺序需为逆时针以保证正面朝向摄像机）
        Vector3[] vertices = new Vector3[] { pointA, pointB, pointC };
        mesh.vertices = vertices;

        // 三角形索引（单个三角形）
        int[] triangles = new int[] { 0, 1, 2 };
        mesh.triangles = triangles;

        // 法线（使光照正确）
        Vector3 normal = Vector3.Cross(vertices[1] - vertices[0], vertices[2] - vertices[0]).normalized;
        Vector3[] normals = new Vector3[] { normal, normal, normal };
        mesh.normals = normals;

        // UV（可省略，但如果需要贴图可设置）
        Vector2[] uv = new Vector2[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1) };
        mesh.uv = uv;

        mf.mesh = mesh;

        // 创建半透明材质
        Material mat = new Material(Shader.Find("Standard"));
        mat.color = meshColor;
        mat.SetFloat("_Mode", 2);      // 设置为透明模式
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.DisableKeyword("_ALPHATEST_ON");
        mat.EnableKeyword("_ALPHABLEND_ON");
        mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        mat.renderQueue = 3000;
        mr.material = mat;
    }

    // 在Scene视图中绘制Gizmos（仅编辑器可见）
    void OnDrawGizmos()
    {
        if (!drawGizmos) return;

        Gizmos.color = gizmoColor;
        // 绘制三角形边框
        Gizmos.DrawLine(pointA, pointB);
        Gizmos.DrawLine(pointB, pointC);
        Gizmos.DrawLine(pointC, pointA);

        // 如果需要填充，可以用MeshGizmos（需额外处理），此处仅显示线框
        // 也可以绘制一个填充的Mesh Gizmos，但性能开销稍大，简单线框足够验证
    }
}