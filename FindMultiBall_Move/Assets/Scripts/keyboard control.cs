using UnityEngine;

public class OmniMoveController : MonoBehaviour
{
    public float moveSpeed = 5f;      // ƽ���ٶ� (m/s)

    public float turnSpeed = 2f;      // ��ת�ٶ� (rad/s)

    private Rigidbody rb;

    public bool enableKeyboardInput = false; // 默认关闭，交给 ML-Agents 控制

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        if (rb == null)
            Debug.LogError("OmniMoveController: δ�ҵ�Rigidbody�����������Rigidbody��");
    }

    void FixedUpdate()
    {
        if (!enableKeyboardInput) return; // 如果被 ML-Agents 控制，就不再读取键盘

        float moveZ = Input.GetAxis("Vertical");   // W/S ��ǰ/���
        float moveX = Input.GetAxis("Horizontal"); // A/D ����/����

        Vector3 moveDirection = transform.TransformDirection(new Vector3(moveX, 0, moveZ)).normalized;
        Vector3 targetVelocity = moveDirection * moveSpeed;

        targetVelocity.y = rb.velocity.y;

        rb.velocity = targetVelocity;

        float turn = 0;
        if (Input.GetKey(KeyCode.Q)) turn = -1;   // Q����ת
        else if (Input.GetKey(KeyCode.E)) turn = 1; // E����ת

        // Ӧ�ý��ٶȣ���Y�ᣩ
        rb.angularVelocity = new Vector3(0, turn * turnSpeed, 0);
    }

    // ========== ���ⲿ���õĽӿڣ�����ML-Agents��==========
    public void SetMove(float x, float z)
    {
        // �����ⲿ�������룬���Ǽ��̿��ƣ�ͨ����ǿ��ѧϰʱʹ�ã�
        // x: ���ҷ��� (-1~1), z: ǰ���� (-1~1)
        Vector3 moveDirection = transform.TransformDirection(new Vector3(x, 0, z)).normalized;
        Vector3 targetVelocity = moveDirection * moveSpeed;
        targetVelocity.y = rb.velocity.y;
        rb.velocity = targetVelocity;
    }

    public void SetTurn(float turnValue)
    {
        rb.angularVelocity = new Vector3(0, turnValue * turnSpeed, 0);
    }
}