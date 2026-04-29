using UnityEngine;

public class SimpleBallShooter : MonoBehaviour
{
    [Header("发射核心设置")]
    public Transform shootPoint;    
    public GameObject ballPrefab;   
    public float destroyDelay = 5f;  

    [Header("目标球门")]
    public Transform targetBasket;

    [Header("1米1档 · 距离分段(5~26米)")]
    public float[] distanceSections = new float[]
    {
        5,6,7,8,9,
        10,11,12,13,14,
        15,16,17,18,19,
        20,21,22,23,24,
        25,26
    };

    [Header("对应力度（逐米递增)")]
    public Vector2[] forceByDistance = new Vector2[]
    {
        new(10,4), //5-6m
        new(10,4), //6-7m
        new(10,4), //7-8m
        new(12,4), //8-9m
        new(12.5f,4), //9-10m
        
        new(13,5), //10-11m
        new(13,5), //11-12m
        new(13,5), //12-13m
        new(13.5f,5.5f), //13-14m
        new(13.5f,6), //14-15m

        new(14,6), //15-16m
        new(14.5f,6), //16-17m
        new(15,6.5f), //17-18m
        new(15,7), //18-19m
        new(15,7.5f), //19-20m
        
        new(9.3f,14),   //20-21m
        new(9.6f,14),   //21-22m
        new(10,14),     //22-23m
        new(10.5f,14),   //23-24m
        new(10.8f,14),    //24-25m

        new(11,14),  //25-26m
        new(11.5f,14)  //25-26m

    };

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            Shoot();
        }
    }

    // 核心：无报错 · 按1米区间取固定力度
    private (Vector2 force, float distance) GetForceAndDistance()
    {
        if (targetBasket == null)
        {
            Debug.LogError("请设置目标球门！");
            return (new Vector2(8, 10), 0);
        }

        Vector3 toTarget = targetBasket.position - transform.position;
        toTarget.y = 0;
        float currentDistance = toTarget.magnitude;

        // 匹配区间
        int index = distanceSections.Length - 1;
        for (int i = 0; i < distanceSections.Length; i++)
        {
            if (currentDistance <= distanceSections[i])
            {
                index = i;
                break;
            }
        }

        index = Mathf.Clamp(index, 0, forceByDistance.Length - 1);
        return (forceByDistance[index], currentDistance);
    }

    void Shoot()
    {
        var (shootForce, currentDistance) = GetForceAndDistance();
        float upForce = shootForce.x;
        float forwardForce = shootForce.y;

        GameObject ball = Instantiate(ballPrefab, shootPoint.position, shootPoint.rotation);
        Rigidbody rb = ball.GetComponent<Rigidbody>();
        
        if (rb == null)
        {
            Debug.LogError("球缺少Rigidbody！");
            Destroy(ball);
            return;
        }

        rb.velocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        Vector3 shootDir = shootPoint.forward * forwardForce + shootPoint.up * upForce;
        rb.AddForce(shootDir, ForceMode.Impulse);
        
        Destroy(ball, destroyDelay);
        Debug.Log($"✅ 距离：{currentDistance:F1}m | 向上：{upForce:F1} | 向前：{forwardForce:F1}");
    }
}
