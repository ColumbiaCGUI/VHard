using UnityEngine;

public class translation : MonoBehaviour
{
    public float moveDistance = 0.1f;
    public void MoveUp()
    {
        Debug.Log("curr position" + transform.position);

        transform.position += Vector3.up * moveDistance;

        Debug.Log("curr position" + transform.position);
    }

    public void MoveDown()
    {
        transform.position -= Vector3.up * moveDistance;
    }

    public void MoveLeft()
    {
        transform.position -= Vector3.right * moveDistance;
    }

    public void MoveRight()
    {
        transform.position += Vector3.right * moveDistance;
    }

    public void MoveForward()
    {
        transform.position += transform.forward * moveDistance;
    }

    public void MoveBackward()
    {
        transform.position -= transform.forward * moveDistance;
    }

}
