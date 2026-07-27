using System;
using UnityEngine;

public sealed class StudyPanelButton : MonoBehaviour
{
    public Action Pressed;

    public void Press()
    {
        Pressed?.Invoke();
    }
}
