using UnityEngine;
using UnityEngine.UI; // For UI elements like Text

public class HoldDetailUI : MonoBehaviour
{
    public Text holdNameText;
    public Text holdDifficultyText;
    public Text holdShapeText;

    // Update the UI with hold details
    public void UpdateHoldDetails(string holdName, string difficulty, string shape)
    {
        holdNameText.text = "Hold: " + holdName;
        holdDifficultyText.text = "Difficulty: " + difficulty;
        holdShapeText.text = "Shape: " + shape;
    }
}
