using UnityEngine;

public class BallShooter : MonoBehaviour
{
    public Transform shootPoint;

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Ball"))
        {
            TeleportBallToShootPoint(other.gameObject);
        }
    }

    private void TeleportBallToShootPoint(GameObject ball)
    {
        Rigidbody rb = ball.GetComponent<Rigidbody>();
        if (rb != null)
        {
            // 先清速度，再设为运动学（顺序不能反！）
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.isKinematic = true; // 再设为运动学
        }

        Collider col = ball.GetComponent<Collider>();
        if (col != null)
            col.enabled = false;

        // 仅传送位置和旋转
        ball.transform.position = shootPoint.position;
        ball.transform.rotation = shootPoint.rotation;
    }
}