using System.Collections;
using System.Collections.Generic;
using System;
using System.Reflection;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

[RequireComponent(typeof(OmniMoveController))]
public class AddAprilTag : Agent
{
    private delegate void AprilTagProcessImageInvoker(ReadOnlySpan<Color32> image, float fov, float tagSize);

    // 任务状态枚举
    private enum TaskState
    {
        FindBall,
        MoveToTriangle,
        ShootBall,
    }

    [Header("Target Balls")]
    [Tooltip("9个小球的数组（在Inspector中直接拖入）")]
    public Transform[] balls;

    [Header("Goal Basket")] // 新增：球框设置
    public Transform goalBasket; // 👈 拖入你场景中的球框物体
    
    [Header("Shoot Settings")] // 新增：发射配置
    public Transform shootPoint; // 发射点（拖小车车头空物体）
    public float shootInterval = 0.5f; // 发射间隔0.5秒
    private bool isAimed = false;
    private float aimTimer = 0;
    // 发射力度配置（直接复用你的投篮逻辑）
    public float[] distanceSections = new float[]
    {
        5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26
    };
    public Vector2[] forceByDistance = new Vector2[]
    {
        new(10,4), new(10,4), new(10,4), new(12,4), new(12.5f,4),
        new(13,5), new(13,5), new(13,5), new(13.5f,5.5f), new(13.5f,6),
        new(14,6), new(14.5f,6), new(15,6.5f), new(15,7), new(15,7.5f),
        new(9.3f,14), new(9.6f,14), new(10,14), new(10.5f,14), new(10.8f,14), new(11,14), new(11.5f,14)
    };

    
    [Header("Spawn Settings")]
    [Tooltip("场地地板的引用，用于自动获取生成范围")]
    public Transform floor; 
    [Tooltip("生成高度")]
    public float spawnHeight = 0.5f; 

    [Header("Triangle Area")]
    [Tooltip("三角形区域的可视化脚本")]
    public TriangleAreaVisualizer triangleVisualizer;

    [Header("Visualization")]
    public TrailRenderer pathTrail; // 拖尾组件，用于显示轨迹
    
    // ==========================================
    // 原生 Camera 单目视觉系统
    // ==========================================
    [Header("Mono Vision Settings")]
    public Camera agentCamera;
    public int visionResolution = 84;
    public Color targetBallColor = Color.green;
    public float colorTolerance = 0.2f;
    public float realBallDiameter = 1.0f;

    // 内部视觉处理结果缓存
    private float vision_IsVisible; 
    private Vector2 vision_ScreenPos;
    private float vision_EstimatedDistance;

    // GPU 回读纹理
    private RenderTexture renderTexture;
    private Texture2D visionTexture;
    private float previousVisionDistance; 

    [Header("AprilTag Align Settings")]
    [Tooltip("用于对齐的目标Tag ID，-1表示使用检测到的第一个Tag")]
    public int targetAprilTagId = -1;
    [Tooltip("Tag边长(米)，需与真实打印尺寸一致")]
    public float aprilTagSize = 0.16f;
    [Tooltip("Tag检测分辨率，越高越稳定但更耗性能")]
    public int aprilTagResolution = 256;
    [Tooltip("Tag水平居中阈值(归一化屏幕坐标)")]
    public float aprilTagCenterThreshold = 0.08f;
    [Tooltip("Tag居中保持时长(秒)，达到后开始投球")]
    public float aprilTagCenterHoldTime = 0.6f;
    [Tooltip("Tag居中控制增益")]
    public float aprilTagTurnGain = 1.4f;
    [Tooltip("未识别Tag时原地搜索转向速度")]
    public float aprilTagSearchTurn = 1f;
    [Tooltip("是否输出AprilTag调试日志")]
    public bool aprilTagDebugLog = true;
    [Tooltip("AprilTag调试日志间隔(秒)")]
    public float aprilTagDebugInterval = 0.5f;

    private object aprilTagDetector;
    private MethodInfo aprilTagProcessImageMethod;
    private AprilTagProcessImageInvoker aprilTagProcessImage;
    private PropertyInfo aprilTagDetectedTagsProperty;
    private PropertyInfo aprilTagPoseIdProperty;
    private PropertyInfo aprilTagPosePositionProperty;
    private RenderTexture aprilTagRenderTexture;
    private Texture2D aprilTagTexture;
    private float aprilTagOffsetX;
    private bool aprilTagVisible;
    private float aprilTagDebugTimer;
    // ==========================================

    private OmniMoveController moveController;
    private Rigidbody rb;
    private float previousDistanceToBall; // 记录上一步到球的距离
    private int successCount = 0;  // 成功计数
    private int failCount = 0;     // 失败计数
    private int lastReportStep = 0;  // 上次报告的步数
    private TaskState currentTaskState = TaskState.FindBall; // 当前任务状态
    private float waitTimeInTriangle = 0f; // 在三角形内的等待时间
    
    // 球收集相关
    private int ballsCollected = 0;  // 已收集的球数
    private bool[] ballCollected;    // 标记每个球是否已被收集
    private const int BALLS_TARGET = 3;  // 需要收集的球数
    
    // 初始位置记录
    private Vector3 carInitialPosition;
    private Quaternion carInitialRotation;
    private Vector3[] ballInitialPositions;
    private Quaternion[] ballInitialRotations;
    private List<GameObject> collectedBallList = new List<GameObject>(); // 👈 新增
    private int shootedBallCount = 0; // 👈 新增
    private bool isShooting = false; // 👈 新增
    private bool hasEnteredTriangle = false; // 👈 新增：标记是否已经进入过三角形区域


    public override void Initialize()
    {
        moveController = GetComponent<OmniMoveController>();
        rb = GetComponent<Rigidbody>();

        // 【关键修复：彻底解决画面抖动】
        // 使用 Unity 物理引擎自带的约束来锁定 X 和 Z 轴旋转。
        // 这取代了之前在 FixedUpdate 里强行修改欧拉角的方式，彻底消除了物理引擎和代码“打架”引发的疯狂抖动！
        rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;

        // 初始化球数组和收集状态
        if (balls != null && balls.Length > 0)
        {
            ballCollected = new bool[balls.Length];
            ballInitialPositions = new Vector3[balls.Length];
            ballInitialRotations = new Quaternion[balls.Length];
            
            for (int i = 0; i < balls.Length; i++)
            {
                if (balls[i] != null)
                {
                    // 记录初始位置和旋转
                    ballInitialPositions[i] = balls[i].position;
                    ballInitialRotations[i] = balls[i].rotation;
                    balls[i].gameObject.SetActive(true);
                }
            }
        }
        else
        {
            Debug.LogError("球数组未配置或为空！请在Inspector中设置9个小球！");
        }
        
        // 记录小车的初始位置和旋转
        carInitialPosition = transform.position;
        carInitialRotation = transform.rotation;

        // 取消代码里强制写的 500 步限制，交由 Inspector 面板来控制。
        // 如果填 0，就不会因为步数到期而强行结束一轮！
        // MaxStep = 500; 

        if (agentCamera != null)
        {
            renderTexture = new RenderTexture(visionResolution, visionResolution, 16);
            visionTexture = new Texture2D(visionResolution, visionResolution, TextureFormat.RGB24, false);

            aprilTagRenderTexture = new RenderTexture(aprilTagResolution, aprilTagResolution, 16);
            aprilTagTexture = new Texture2D(aprilTagResolution, aprilTagResolution, TextureFormat.RGB24, false);

            // 使用反射动态接入AprilTag包，避免编辑器/程序集索引异常导致编译期命名空间报错。
            Type detectorType = Type.GetType("AprilTag.TagDetector, AprilTag.Runtime");
            Type tagPoseType = Type.GetType("AprilTag.TagPose, AprilTag.Runtime");
            if (detectorType != null && tagPoseType != null)
            {
                aprilTagDetector = Activator.CreateInstance(detectorType, aprilTagResolution, aprilTagResolution, 2);
                aprilTagProcessImageMethod = detectorType.GetMethod("ProcessImage");
                if (aprilTagProcessImageMethod != null)
                {
                    aprilTagProcessImage = aprilTagProcessImageMethod.CreateDelegate(
                        typeof(AprilTagProcessImageInvoker),
                        aprilTagDetector
                    ) as AprilTagProcessImageInvoker;
                }
                aprilTagDetectedTagsProperty = detectorType.GetProperty("DetectedTags");
                aprilTagPoseIdProperty = tagPoseType.GetProperty("ID");
                aprilTagPosePositionProperty = tagPoseType.GetProperty("Position");
            }
            else
            {
                Debug.LogWarning("AprilTag.Runtime 程序集未解析成功，将跳过Tag居中流程。");
            }
        }
    }

    private void OnDestroy()
    {
        if (renderTexture != null) renderTexture.Release();
        if (visionTexture != null) Destroy(visionTexture);
        if (aprilTagRenderTexture != null) aprilTagRenderTexture.Release();
        if (aprilTagTexture != null) Destroy(aprilTagTexture);
        if (aprilTagDetector is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    // 收集指定的球
    private void CollectBall(int ballIndex)
    {
        if (ballIndex < 0 || ballIndex >= balls.Length) return;
        if (ballCollected[ballIndex]) return;

        ballCollected[ballIndex] = true;
        ballsCollected++;
        collectedBallList.Add(balls[ballIndex].gameObject);
        SetReward(0.3f);

        Transform targetBall = balls[ballIndex];
        Rigidbody ballRb = targetBall.GetComponent<Rigidbody>();
        Collider ballCol = targetBall.GetComponent<Collider>();

        // 冻结物理 + 关闭碰撞 防卡死
        if(ballRb != null)
        {
            ballRb.velocity = Vector3.zero;
            ballRb.angularVelocity = Vector3.zero;
            ballRb.isKinematic = true; 
        }
        if(ballCol != null) 
            ballCol.enabled = false;  // 关键：关闭碰撞体！

        // 垂直堆叠不遮挡
        targetBall.SetParent(shootPoint);
        targetBall.localPosition = new Vector3(0, ballsCollected * 0.2f, 0);
        targetBall.localRotation = Quaternion.identity;
        targetBall.gameObject.SetActive(true);

        Debug.Log($"收集第{ballsCollected}球");

        // 只有满3球才切换状态
        if (ballsCollected >= BALLS_TARGET)
        {
            StartMoveToTriangle();
        }
    }

    // 开始移动到三角形区域
    private void StartMoveToTriangle()
    {
        Debug.Log($"<color=green>成功收集 {BALLS_TARGET} 个球，开始导航到三角形区域</color>");
        SetReward(0.5f);
        currentTaskState = TaskState.MoveToTriangle;
    }

    // 成功结束辅助函数（便于调试）：抵达三角形区域
    private void SignalSuccess(string reason)
    {
        Debug.Log($"<color=green>最终成功: {reason}</color>");
        SetReward(1.0f);
        
        // 显示所有球，准备下一个回合
        if (balls != null)
        {
            for (int i = 0; i < balls.Length; i++)
            {
                if (balls[i] != null)
                {
                    balls[i].gameObject.SetActive(true);
                }
            }
        }
        
        EndEpisode();
    }

    // 失败结束辅助函数（便于调试）
    private void SignalFail(string reason, float penalty)
    {
        failCount++;
        Debug.Log($"<color=red>失败 #{failCount}: {reason}</color>");
        AddReward(penalty);
        EndEpisode();
    }

    // 每次回合开始时调用
    public override void OnEpisodeBegin()
    {
        // 关键修复3：强行清理上一局还在运行的投篮协程，防止它在清空 list 的同时继续发射导致 Collection was modified 报错！
        StopAllCoroutines();

        currentTaskState = TaskState.FindBall; // 重置为找球状态
        waitTimeInTriangle = 0f; // 重置等待计时器
        ballsCollected = 0; // 重置已收集球数
        collectedBallList.Clear(); // 👈 新增
        shootedBallCount = 0; // 👈 新增
        isShooting = false; // 👈 新增
        hasEnteredTriangle = false; // 👈 新增
        isAimed = false;
        aimTimer = 0f;
        aprilTagOffsetX = 0f;
        aprilTagVisible = false;
        
        // 初始化球的收集状态
        if (ballCollected == null)
        {
            ballCollected = new bool[balls.Length];
        }
        for (int i = 0; i < ballCollected.Length; i++)
        {
            ballCollected[i] = false;
        }
        
        // 1. 停止车辆当前速度
        rb.velocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        // 2. 恢复小车到初始位置
        transform.position = carInitialPosition;
        transform.rotation = carInitialRotation;
        Debug.Log($"<color=yellow>小车已恢复到初始位置：{carInitialPosition}</color>");

        // 3. 恢复所有球到初始位置
        if (balls != null && balls.Length > 0)
        {
            if (ballInitialPositions != null && ballInitialPositions.Length == balls.Length)
            {
                for (int i = 0; i < balls.Length; i++)
                {
                    if (balls[i] != null)
                    {
                        balls[i].position = ballInitialPositions[i];
                        balls[i].rotation = ballInitialRotations[i];
                        balls[i].gameObject.SetActive(true);
                        balls[i].transform.SetParent(null);
                        Debug.Log($"<color=yellow>球{i+1}已恢复到初始位置：{ballInitialPositions[i]}</color>");
                    }
                }
            }
            else
            {
                Debug.LogError($"球的初始位置数组未初始化正确!");
            }
        }

        // 4. 清除上一局的轨迹，重新开始画线
        if (pathTrail != null)
        {
            pathTrail.Clear();
        }

        // 5. 记录初始距离到最近的未收集的球
        if (balls != null && balls.Length > 0)
        {
            float minDist = float.MaxValue;
            foreach (var ball in balls)
            {
                if (ball != null && ball.gameObject.activeSelf)
                {
                    float dist = Vector3.Distance(transform.position, ball.position);
                    if (dist < minDist)
                        minDist = dist;
                }
            }
            previousDistanceToBall = minDist;
        }

        ProcessVisionFeature();
        previousVisionDistance = vision_IsVisible > 0.5f ? vision_EstimatedDistance : 20f;
    }

    private void ProcessVisionFeature()
    {
        if (agentCamera == null) return;
        agentCamera.targetTexture = renderTexture;
        agentCamera.Render();
        RenderTexture.active = renderTexture;
        visionTexture.ReadPixels(new Rect(0, 0, visionResolution, visionResolution), 0, 0);
        visionTexture.Apply();
        RenderTexture.active = null;
        agentCamera.targetTexture = null;

        Color32[] pixels = visionTexture.GetPixels32();
        int minX = visionResolution, maxX = -1;
        int minY = visionResolution, maxY = -1;
        bool found = false;

        for (int y = 0; y < visionResolution; y++)
        {
            for (int x = 0; x < visionResolution; x++)
            {
                Color32 c = pixels[y * visionResolution + x];
                if (Mathf.Abs(c.r / 255f - targetBallColor.r) < colorTolerance && 
                    Mathf.Abs(c.g / 255f - targetBallColor.g) < colorTolerance && 
                    Mathf.Abs(c.b / 255f - targetBallColor.b) < colorTolerance)
                {
                    found = true;
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
            }
        }

        if (found)
        {
            vision_IsVisible = 1.0f;
            float centerX = (minX + maxX) / 2.0f;
            float centerY = (minY + maxY) / 2.0f;
            vision_ScreenPos.x = (centerX / visionResolution) * 2.0f - 1.0f;
            vision_ScreenPos.y = (centerY / visionResolution) * 2.0f - 1.0f;

            float pixelWidth = (maxX - minX) + 1;
            float focalLength = (visionResolution / 2.0f) / Mathf.Tan(agentCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            vision_EstimatedDistance = (realBallDiameter * focalLength) / pixelWidth;
        }
        else
        {
            vision_IsVisible = 0.0f;
            vision_ScreenPos = Vector2.zero;
            vision_EstimatedDistance = 20f; 
        }
    }

    // 收集非视觉观察
    public override void CollectObservations(VectorSensor sensor)
    {
        // 加入单目视觉感知数据
        ProcessVisionFeature();
        sensor.AddObservation(vision_IsVisible);                 // 1个观测值
        sensor.AddObservation(vision_ScreenPos);                 // 2个观测值
        float normalizedDist = Mathf.Clamp(vision_EstimatedDistance, 0f, 20f) / 20f;
        sensor.AddObservation(normalizedDist);                   // 1个观测值
        
        // 总共刚好 4 个值，与 Inspector 中的 Space Size = 4 吻合！
    }

    // 接收强化学习大脑或启发式函数给出的动作
    public override void OnActionReceived(ActionBuffers actionBuffers)
    {
        switch (currentTaskState)
        {
            case TaskState.FindBall:
                OnActionReceived_FindBall(actionBuffers);
                break;
            case TaskState.MoveToTriangle:
                OnActionReceived_MoveToTriangle(actionBuffers);
                break;
            case TaskState.ShootBall: // 👈 新增
                OnActionReceived_ShootBall(actionBuffers); // 👈 新增
                break;
        }
    }

    // 第一阶段：寻找球
    private void OnActionReceived_FindBall(ActionBuffers actionBuffers)
    {
        // 获取连续动作值（需要在Behavior Parameters中设置Continuous Actions = 3）
        float moveX = actionBuffers.ContinuousActions[0]; // 左右平移
        float moveZ = actionBuffers.ContinuousActions[1]; // 前后移动
        float rawTurn = actionBuffers.ContinuousActions[2]; 
        
        float turnValue = Mathf.Clamp(rawTurn, 0f, 1f); 

        // 调用OmniMoveController的方法控制车辆
        moveController.SetMove(moveX, moveZ);
        moveController.SetTurn(turnValue);

        // 1. 基础探索时间惩罚
        AddReward(-0.002f);
        
        // 2. 对侧向移动和转向施加额外惩罚，迫使Agent更倾向于使用 moveZ (前进)
        float sidewaysPenalty = Mathf.Abs(moveX) * 0.002f;
        float turnPenalty = Mathf.Abs(rawTurn) * 0.002f;
        AddReward(-sidewaysPenalty - turnPenalty);

        if (moveZ < 0)
        {
            AddReward(moveZ * 0.005f); // moveZ为负，直接变成惩罚
        }

        int currentStep = Academy.Instance.StepCount;

        // ==============================================================
        // 纯视觉参数的奖励函数 (脱离世界坐标)
        // ==============================================================
        if (vision_IsVisible > 0.5f)
        {
            float distanceDelta = previousVisionDistance - vision_EstimatedDistance;  
            float distanceReward = Mathf.Clamp(distanceDelta * 0.5f, -0.05f, 0.1f);  
            float centerFactor = 1.0f - Mathf.Abs(vision_ScreenPos.x); 

            if (distanceDelta > 0)
                AddReward(distanceReward * centerFactor);
            else
                AddReward(distanceReward);

            // 【新增！防转圈规矩】：如果球已经处在屏幕相对中央的位置 (偏离不到0.3)
            if (Mathf.Abs(vision_ScreenPos.x) < 0.3f) 
            {
                // 如果此时 AI 还在大幅度打方向盘，狠狠扣分！
                if (Mathf.Abs(rawTurn) > 0.1f)
                {
                    AddReward(-0.01f);
                }
                else
                {
                    // 方向盘稳如泰山，立刻给一笔稳行奖励
                    AddReward(0.01f);
                }
            }

            previousVisionDistance = vision_EstimatedDistance;

            // [辅助调试] 每 200 步报告一次视野情况
            if (currentStep % 200 == 0)
            {
                Debug.Log($"<color=cyan>[视觉OK] 发现球！估算距离: {vision_EstimatedDistance:F2}  屏幕中心偏移: {vision_ScreenPos.x:F2}</color>");
            }
        }
        else
        {
            // 视野里没球时，加大惩罚，逼迫它左顾右盼
            AddReward(-0.005f);

            // [诊断] 一旦出现转圈问题，很可能是根本没看见球！
            if (currentStep % 200 == 0)
            {
                Debug.LogWarning($"[视觉丢失] 找不到目标球！(如果一直在打印这句，说明光照亮度、Color Tolerance或相机捕捉失效，屏幕里根本提取不到 {targetBallColor} )");
            }
        }

        // 8. 越界检测
        if (floor != null)
        {
            Collider floorCollider = floor.GetComponent<Collider>();
            if (floorCollider != null)
            {
                Bounds bounds = floorCollider.bounds;
                if (transform.position.x < bounds.min.x - 2f || transform.position.x > bounds.max.x + 2f ||
                    transform.position.z < bounds.min.z - 2f || transform.position.z > bounds.max.z + 2f ||
                    transform.position.y < bounds.min.y - 2f)
                {
                    SignalFail("小车越界", -1.0f);
                    return;
                }
            }
        }

        // 8. 每1000步报告一次统计
        if (currentStep - lastReportStep >= 1000)
        {
            Debug.Log($"[STATUS] Step {currentStep}: 已收集 {ballsCollected}/{BALLS_TARGET} 个球, 成功 {successCount}, 失败 {failCount}");
            lastReportStep = currentStep;
        }
    }

    // 第二阶段：移动到三角形区域（使用启发式导航）
    private void OnActionReceived_MoveToTriangle(ActionBuffers actionBuffers)
    {
        // 只要进入过三角形区域就不再出来，防止在边界反复横跳产生的回拉力
        if (!hasEnteredTriangle && IsPointInTriangle(transform.position))
        {
            hasEnteredTriangle = true;
        }

        // 检查是否已经在三角形区域内
        if (hasEnteredTriangle)
        {
            // 停车：只杀掉平移速度，不能杀掉角速度和重置Turn！否则会导致小车使用 SetTurn 时被强行刹车，产生巨大的“扯平抵抗力”。
            moveController.SetMove(0, 0);
            rb.velocity = Vector3.zero;
            // 严禁在此处调用 rb.angularVelocity = Vector3.zero; 或 moveController.SetTurn(0);

            // 进入三角区后不再自动看向球框，改为识别AprilTag并将其居中。
            if (TryUpdateAprilTagAlignment())
            {
                // 吸附死区：只要曾经对准过(isAimed)，就锁死在这个状态，无视摄像头微晃
                bool centered = Mathf.Abs(aprilTagOffsetX) <= aprilTagCenterThreshold || isAimed;
                if (centered)
                {
                    // 彻底停死轮子，安静等0.6秒投篮
                    moveController.SetTurn(0);
                    aimTimer += Time.deltaTime;
                    isAimed = true;
                    if (aimTimer >= aprilTagCenterHoldTime && !isShooting)
                    {
                        currentTaskState = TaskState.ShootBall;
                        StartCoroutine(ShootBallsSequence());
                    }
                }
                else
                {
                    // 带最低起步防卡死的旋转力度计算：正数=向右转，负数=向左转
                    // 如果发现方向反转抖动，这里可能是真正的底层输入方向：
                    // 当偏差在右边 (offsetX > 0) 时，我们需要向右转。
                    float rawTurn = aprilTagOffsetX * aprilTagTurnGain;
                    float minPower = 0.15f; 
                    float tagTurnValue = Mathf.Clamp(Mathf.Abs(rawTurn), minPower, 0.5f) * Mathf.Sign(rawTurn);
                    
                    moveController.SetTurn(tagTurnValue);
                    aimTimer = 0f;
                    isAimed = false;
                }
            }
            else
            {
                // 未识别到Tag时，低速原地逆时针向左搜索。
                // 如果发现向左搜索反而向右，说明 moveController 对于 SetTurn 的正负号和 Unity 默认的 Transform 旋转恰好相反。
                // 那么为了它真正向左搜索，我们用负数试试：
                moveController.SetTurn(-aprilTagSearchTurn);
                aimTimer = 0f;
                isAimed = false;
            }

            waitTimeInTriangle += Time.deltaTime;
            return;
        }
        

        
        // 如果离开三角形，重置计时器
        waitTimeInTriangle = 0f;

        // 计算三角形中心点
        Vector3 triangleCenter = GetTriangleCenter();
        
        // 计算朝向三角形中心的方向
        Vector3 directionToTriangle = (triangleCenter - transform.position).normalized;
        
        // 计算需要旋转的角度
        float angleToTarget = Vector3.SignedAngle(transform.forward, directionToTriangle, Vector3.up);
        
        // 决定旋转方向：角度为正表示需要向左转(正旋转)，为负表示向右转
        float turnValue = Mathf.Clamp01(Mathf.Abs(angleToTarget) / 45f); // 角度越大转速越快
        if (angleToTarget < 0)
            turnValue = -turnValue;
        
        // 如果已经大致对准目标，就往前走；否则继续转向
        float moveZ = 0;
        if (Mathf.Abs(angleToTarget) < 30f)
        {
            moveZ = 1.0f; // 直接前进
        }
        
        moveController.SetMove(0, moveZ);
        moveController.SetTurn(Mathf.Clamp(turnValue, -1f, 1f));

        // 越界检测
        if (floor != null)
        {
            Collider floorCollider = floor.GetComponent<Collider>();
            if (floorCollider != null)
            {
                Bounds bounds = floorCollider.bounds;
                if (transform.position.x < bounds.min.x - 2f || transform.position.x > bounds.max.x + 2f ||
                    transform.position.z < bounds.min.z - 2f || transform.position.z > bounds.max.z + 2f ||
                    transform.position.y < bounds.min.y - 2f)
                {
                    SignalFail("移动到三角形时越界", -1.0f);
                    return;
                }
            }
        }
    }
    private IEnumerator ShootBallsSequence()
    {
        isShooting = true;
        foreach (var ball in collectedBallList)
        {
            ShootSingleBall(ball);
            yield return new WaitForSeconds(shootInterval);
        }
        
        Debug.Log("<color=green>3个球全部分离小车并射出，请欣赏完美弧线（等待5秒观看投篮落地）...</color>");
        
        // 发射完成后，强行等待 5 秒，让你充分看清球在空中的飞行轨迹和落入篮筐的画面！
        yield return new WaitForSeconds(5f); 
        
        isShooting = false;
        SignalSuccess("3球发射并完全落地，任务成功结束");
    }
    private void ShootSingleBall(GameObject ball)
    {
        if (ball == null || shootPoint == null) return;

        // 解绑父物体，脱离小车
        ball.transform.SetParent(null);

        Rigidbody rbBall = ball.GetComponent<Rigidbody>();
        Collider ballCol = ball.GetComponent<Collider>(); // 新增
        if (rbBall == null) { Debug.LogError("球缺少Rigidbody"); return; }

        // 恢复物理模拟
        rbBall.isKinematic = false;
        rbBall.velocity = Vector3.zero;
        rbBall.angularVelocity = Vector3.zero;
        if(ballCol != null) ballCol.enabled = true; // 新增：恢复碰撞

        if (goalBasket != null)
        {
            // 给目标位置预留一点高度补偿，确保能越过篮筐边缘极准空心入网
            Vector3 targetPos = goalBasket.position;
            targetPos.y += 0.2f; 

            // 计算初速度（固定 60 度抛射角效果最完美）
            Vector3 initVelocity = CalculateProjectileVelocity(shootPoint.position, targetPos, 60f);

            // 直接赋予计算出的完美速度向量，取代加力，完全无视小球质量造成的影响
            rbBall.velocity = initVelocity;
            Debug.Log($"<color=orange>动态抛射计算 → 目标距离: {Vector3.Distance(shootPoint.position, targetPos):F2} | 初速度: {initVelocity.magnitude:F2}</color>");
        }
        else
        {
            // 无目标兜底
            rbBall.AddForce(shootPoint.forward * 4 + shootPoint.up * 10, ForceMode.Impulse);
        }

        shootedBallCount++;
        Debug.Log($"<color=magenta>发射第{shootedBallCount}球</color>");
    }

    private Vector3 CalculateProjectileVelocity(Vector3 origin, Vector3 target, float angleDeg)
    {
        Vector3 targetDir = target - origin;
        float yOffset = targetDir.y;
        targetDir.y = 0f;
        float distance = targetDir.magnitude;
        
        float radAngle = angleDeg * Mathf.Deg2Rad;
        float g = Mathf.Abs(Physics.gravity.y);
        
        float denominator = distance * Mathf.Tan(radAngle) - yOffset;
        if (denominator <= 0) return (targetDir.normalized + Vector3.up).normalized * 5f; // 防止高度太过极端算不出来的兜底
        
        float v2 = (g * distance * distance) / (2f * denominator * Mathf.Cos(radAngle) * Mathf.Cos(radAngle));
        float v = Mathf.Sqrt(v2);
        
        Vector3 velocity = targetDir.normalized * (v * Mathf.Cos(radAngle));
        velocity.y = v * Mathf.Sin(radAngle);
        
        return velocity;
    }
    
    private void OnActionReceived_ShootBall(ActionBuffers actionBuffers)
    {
        // 投篮阶段不需要再乱跑乱转了，锁定速度
        moveController.SetMove(0, 0);
        moveController.SetTurn(0);
        rb.velocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        // 发射与结束交由协程处理；此阶段不再自动看向球框。
    }

    private bool TryUpdateAprilTagAlignment()
    {
        aprilTagVisible = false;
        aprilTagOffsetX = 0f;

        if (agentCamera == null || aprilTagDetector == null || aprilTagRenderTexture == null || aprilTagTexture == null)
            return false;

        agentCamera.targetTexture = aprilTagRenderTexture;
        agentCamera.Render();
        RenderTexture.active = aprilTagRenderTexture;
        aprilTagTexture.ReadPixels(new Rect(0, 0, aprilTagResolution, aprilTagResolution), 0, 0);
        aprilTagTexture.Apply();
        RenderTexture.active = null;
        agentCamera.targetTexture = null;

        if (aprilTagProcessImage == null || aprilTagDetectedTagsProperty == null || aprilTagPoseIdProperty == null || aprilTagPosePositionProperty == null)
            return false;

        Color32[] pixels = aprilTagTexture.GetPixels32();
        aprilTagProcessImage(pixels, agentCamera.fieldOfView * Mathf.Deg2Rad, aprilTagSize);

        bool found = false;
        float bestDepth = float.MaxValue;
        float selectedOffsetX = 0f;
        int detectedCount = 0;
        int validCount = 0;

        IEnumerable detectedTags = aprilTagDetectedTagsProperty.GetValue(aprilTagDetector) as IEnumerable;
        if (detectedTags == null) return false;

        foreach (var tagPose in detectedTags)
        {
            detectedCount++;
            int tagId = (int)aprilTagPoseIdProperty.GetValue(tagPose);
            Vector3 tagPosition = (Vector3)aprilTagPosePositionProperty.GetValue(tagPose);

            if (targetAprilTagId >= 0 && tagId != targetAprilTagId)
                continue;

            if (tagPosition.z <= 0.01f)
                continue;

            validCount++;

            float x = tagPosition.x;
            float z = tagPosition.z;
            float horizontalNdc = x / (z * Mathf.Tan(agentCamera.fieldOfView * 0.5f * Mathf.Deg2Rad) * agentCamera.aspect);
            horizontalNdc = Mathf.Clamp(horizontalNdc, -1f, 1f);

            if (z < bestDepth)
            {
                bestDepth = z;
                selectedOffsetX = horizontalNdc;
                found = true;
            }
        }

        if (!found)
        {
            if (aprilTagDebugLog)
            {
                aprilTagDebugTimer += Time.deltaTime;
                if (aprilTagDebugTimer >= Mathf.Max(0.05f, aprilTagDebugInterval))
                {
                    aprilTagDebugTimer = 0f;
                    // Debug.Log($"[AprilTag] 未锁定目标: detected={detectedCount}, valid={validCount}, targetId={targetAprilTagId}");
                }
            }
            return false;
        }

        aprilTagVisible = true;
        aprilTagOffsetX = selectedOffsetX;

        if (aprilTagDebugLog)
        {
            aprilTagDebugTimer += Time.deltaTime;
            if (aprilTagDebugTimer >= Mathf.Max(0.05f, aprilTagDebugInterval))
            {
                aprilTagDebugTimer = 0f;
                // Debug.Log($"[AprilTag] 锁定成功: offsetX={aprilTagOffsetX:F3}, detected={detectedCount}, valid={validCount}");
            }
        }

        return true;
    }

    // 用于自己手动测试（Heuristic），对应Behavior Parameters里的Heuristic Only模式
    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var continuousActionsOut = actionsOut.ContinuousActions;
        continuousActionsOut[0] = Input.GetAxis("Horizontal"); // A/D
        continuousActionsOut[1] = Input.GetAxis("Vertical");   // W/S
        
        float turn = 0;
        if (Input.GetKey(KeyCode.Q)) turn = -1;
        else if (Input.GetKey(KeyCode.E)) turn = 1;
        continuousActionsOut[2] = turn;
    }

    private bool IsFrontContact(Vector3 point, float minDot = 0.6f)
    {
        Vector3 dir = (point - transform.position).normalized;
        float dot = Vector3.Dot(transform.forward, dir);
        return dot > minDot;
    }

    // 触发检测：碰撞到目标球或障碍物
    private void OnTriggerEnter(Collider other)
    {
        if (currentTaskState == TaskState.FindBall)
        {
            if (other.CompareTag("Ball"))
            {
                // 通过视觉辅助判断是否属于前方正面撞击
                if (vision_IsVisible > 0.5f && Mathf.Abs(vision_ScreenPos.x) < 0.6f)
                {
                    // 找到对应的球索引
                    for (int i = 0; i < balls.Length; i++)
                    {
                        if (balls[i] != null && balls[i].gameObject == other.gameObject)
                        {
                            CollectBall(i);
                            break;
                        }
                    }
                }
                else
                {
                    // 修改：不要直接用 SignalFail 结束回合！只要没掉下地图就给它机会重试，我们只扣分惩罚
                    AddReward(-0.2f);
                    // Debug.Log("<color=orange>侧碰或没看清就碰到了球，扣除 0.2 分但不结束回合！</color>");
                }
                return;
            }

            if (other.CompareTag("Wall") || other.CompareTag("Obstacle"))
            {
                // 已删除：失败 #3: 触发器碰到墙/障碍
            }
        }
    }
    
    // 物理碰撞检测：碰撞到目标球或障碍物
    private void OnCollisionEnter(Collision collision)
    {
        if (currentTaskState == TaskState.FindBall)
        {
            if (collision.gameObject.CompareTag("Ball"))
            {
                // 通过视觉辅助判断是否属于前方正面撞击
                if (vision_IsVisible > 0.5f && Mathf.Abs(vision_ScreenPos.x) < 0.6f)
                {
                    // 找到对应的球索引
                    for (int i = 0; i < balls.Length; i++)
                    {
                        if (balls[i] != null && balls[i].gameObject == collision.gameObject)
                        {
                            CollectBall(i);
                            break;
                        }
                    }
                }
                else
                {
                    // 已删除：失败 #11: 非前方视觉捕捉到物理碰撞到球
                }
                return;
            }

            if (collision.gameObject.CompareTag("Wall") || collision.gameObject.CompareTag("Obstacle"))
            {
                // 已删除：失败 #13: 物理碰撞到墙/障碍
            }
        }
    }

    // ========== 三角形区域相关的辅助方法 ==========

    /// <summary>
    /// 计算三角形的中心点（质心）
    /// </summary>
    private Vector3 GetTriangleCenter()
    {
        if (triangleVisualizer == null)
        {
            Debug.LogError("TriangleAreaVisualizer not assigned!");
            return Vector3.zero;
        }

        // 三角形质心 = (A + B + C) / 3
        Vector3 center = (triangleVisualizer.pointA + triangleVisualizer.pointB + triangleVisualizer.pointC) / 3f;
        return center;
    }

    /// <summary>
    /// 判断点是否在三角形内部（XZ平面）
    /// 使用重心坐标法
    /// </summary>
    private bool IsPointInTriangle(Vector3 point)
    {
        if (triangleVisualizer == null)
        {
            Debug.LogError("TriangleAreaVisualizer not assigned!");
            return false;
        }

        Vector3 p = point;
        Vector3 a = triangleVisualizer.pointA;
        Vector3 b = triangleVisualizer.pointB;
        Vector3 c = triangleVisualizer.pointC;

        // 将3D点投影到XZ平面
        Vector2 p2d = new Vector2(p.x, p.z);
        Vector2 a2d = new Vector2(a.x, a.z);
        Vector2 b2d = new Vector2(b.x, b.z);
        Vector2 c2d = new Vector2(c.x, c.z);

        // 使用重心坐标法判断点是否在三角形内
        return IsPointInTriangle2D(p2d, a2d, b2d, c2d);
    }

    /// <summary>
    /// 二维平面上判断点是否在三角形内部
    /// </summary>
    private bool IsPointInTriangle2D(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float sign(Vector2 p1, Vector2 p2, Vector2 p3)
        {
            return (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);
        }

        float d1 = sign(p, a, b);
        float d2 = sign(p, b, c);
        float d3 = sign(p, c, a);

        bool hasNeg = (d1 < 0) || (d2 < 0) || (d3 < 0);
        bool hasPos = (d1 > 0) || (d2 > 0) || (d3 > 0);

        return !(hasNeg && hasPos);
    }

    /// <summary>
    /// 计算点到三角形的最近距离（XZ平面）
    /// </summary>
    private float DistancePointToTriangle(Vector3 point)
    {
        if (triangleVisualizer == null)
        {
            Debug.LogError("TriangleAreaVisualizer not assigned!");
            return float.MaxValue;
        }

        Vector3 p = point;
        Vector3 a = triangleVisualizer.pointA;
        Vector3 b = triangleVisualizer.pointB;
        Vector3 c = triangleVisualizer.pointC;

        // 如果已经在三角形内，距离为0
        if (IsPointInTriangle(p))
            return 0f;

        // 计算到三个边的距离
        float distAB = DistancePointToSegment(p, a, b);
        float distBC = DistancePointToSegment(p, b, c);
        float distCA = DistancePointToSegment(p, c, a);

        // 返回最近的距离
        return Mathf.Min(distAB, distBC, distCA);
    }

    /// <summary>
    /// 计算点到线段的距离（XZ平面）
    /// </summary>
    private float DistancePointToSegment(Vector3 point, Vector3 segStart, Vector3 segEnd)
    {
        Vector2 p = new Vector2(point.x, point.z);
        Vector2 a = new Vector2(segStart.x, segStart.z);
        Vector2 b = new Vector2(segEnd.x, segEnd.z);

        Vector2 ab = b - a;
        Vector2 ap = p - a;

        float ab2 = Vector2.Dot(ab, ab);
        if (ab2 == 0f)
            return Vector2.Distance(p, a);

        float t = Mathf.Clamp01(Vector2.Dot(ap, ab) / ab2);
        Vector2 closest = a + t * ab;

        return Vector2.Distance(p, closest);
    }
}

