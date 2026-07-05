using System;
using System.IO;
using System.Text;
using Unity.Collections;
using UnityEngine;


public class ActionRecorder : MonoBehaviour
{

    public Transform playerHead;
    public bool recordToConsole = true;
    public bool recordToCsv = true;

    private string filePath;
    private StringBuilder buffer = new StringBuilder();

    private void Start()
    {
        string fileName = $"action_log_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv";
        filePath = Path.Combine(Application.persistentDataPath, fileName);

        WriteLine("utcTime,sessionTime,frame,playerPosition,action,hand,hold,details");

        Debug.Log($"[ActionRecorder] Logging to: {filePath}");

    }

    public void Record(string action,string hand="", GameObject hold = null, string details = "")
    {
        string utcTime = DateTime.UtcNow.ToString("o");
        string sessionTime = Time.time.ToString("F3");
        string frame = Time.frameCount.ToString();

        Vector3 position = playerHead != null ? playerHead.position : Vector3.zero;
        string playerPosition = FormatVector(position);

        string holdName = hold != null ? hold.name : "";

        string line=
            $"{Escape(utcTime)}," +
            $"{Escape(sessionTime)}," +
            $"{Escape(frame)}," +
            $"{Escape(playerPosition)}," +
            $"{Escape(action)}," +
            $"{Escape(hand)}," +
            $"{Escape(holdName)}," +
            $"{Escape(details)}";

        WriteLine(line);

        if (recordToConsole)
        {
            Debug.Log($"[ActionRecorder] {line}");
        }
    }


    private void WriteLine(string line)
    {
        buffer.AppendLine(line);

        if (recordToCsv)
        {
            File.AppendAllText(filePath, line + Environment.NewLine);
        }
    }

    private string FormatVector(Vector3 v)
    {
        return $"({v.x:F3},{v.y:F3},{v.z:F3})";
    }

    private string Escape(string value)
    {
        if (value == null)
        {
            return "";
        }

        value = value.Replace("\"", "\"\"");

        if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
        {
            return $"\"{value}\"";
        }

        return value;
    }

    public string GetLogFilePath()
    {
        return filePath;
    }





}