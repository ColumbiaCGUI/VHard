using UnityEngine;

public class Translation : MonoBehaviour
{
    // Set fixed distance to move per press (in units)
    private float moveDistance = 0.01f;

    // Booleans to track the button presses (hold states)
    private bool isMoveUp = false;
    private bool isMoveDown = false;
    private bool isMoveLeft = false;
    private bool isMoveRight = false;
    private bool isMoveForward = false;
    private bool isMoveBackward = false;

    void Update()
    {
        // Continuously move the object if the corresponding boolean is true
        if (isMoveUp)
        {
            MoveUp();
        }
        if (isMoveDown)
        {
            MoveDown();
        }
        if (isMoveLeft)
        {
            MoveLeft();
        }
        if (isMoveRight)
        {
            MoveRight();
        }
        if (isMoveForward)
        {
            MoveForward();
        }
        if (isMoveBackward)
        {
            MoveBackward();
        }
    }

    // Move the object by a fixed distance in each direction
    public void MoveUp()
    {
        transform.position += Vector3.up * moveDistance;
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

    // These methods are triggered by button events for pressing down
    public void StartMoveUp() { isMoveUp = true; }
    public void StartMoveDown() { isMoveDown = true; }
    public void StartMoveLeft() { isMoveLeft = true; }
    public void StartMoveRight() { isMoveRight = true; }
    public void StartMoveForward() { isMoveForward = true; }
    public void StartMoveBackward() { isMoveBackward = true; }

    // These methods are triggered by button events for releasing the button
    public void StopMoveUp() { isMoveUp = false; }
    public void StopMoveDown() { isMoveDown = false; }
    public void StopMoveLeft() { isMoveLeft = false; }
    public void StopMoveRight() { isMoveRight = false; }
    public void StopMoveForward() { isMoveForward = false; }
    public void StopMoveBackward() { isMoveBackward = false; }
}