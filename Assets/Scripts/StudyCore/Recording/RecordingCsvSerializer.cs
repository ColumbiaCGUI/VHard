using System;
using System.Globalization;
using System.Text;
using UnityEngine;

public static class RecordingCsvSerializer
{
    public const string EventHeader =
        "utcTime,sessionTime,frame,playerPosition,action,hand,hold,details";

    private static readonly char[] EscapedCharacters = { ',', '"', '\n', '\r' };

    public static string BuildEventRow(
        DateTime utcTime,
        float sessionTime,
        int frame,
        Vector3 playerPosition,
        string action,
        string hand,
        string hold,
        string details)
    {
        return string.Join(",",
            Escape(utcTime.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)),
            Escape(sessionTime.ToString("F3", CultureInfo.InvariantCulture)),
            Escape(frame.ToString(CultureInfo.InvariantCulture)),
            Escape(FormatEventVector(playerPosition)),
            Escape(action),
            Escape(hand),
            Escape(hold),
            Escape(details));
    }

    public static string BuildCaptureHeader()
    {
        StringBuilder header = new();
        header.Append("utc,sessionTime,frame,blockTime,mode,route,hold,");
        header.Append("headPosX,headPosY,headPosZ,headRotX,headRotY,headRotZ,headRotW,");
        AppendBoneHeader(header, 'L');
        header.Append("LConf,");
        AppendBoneHeader(header, 'R');
        header.Append("RConf,touchedHold,gripFlag,perFingerContactMask,gripScore");
        return header.ToString();
    }

    public static void AppendCaptureRow(StringBuilder output, CaptureFrame frame)
    {
        if (output == null)
        {
            throw new ArgumentNullException(nameof(output));
        }
        if (frame == null)
        {
            throw new ArgumentNullException(nameof(frame));
        }

        output.Append(new DateTime(frame.utcTicks, DateTimeKind.Utc)
            .ToString("o", CultureInfo.InvariantCulture)).Append(',');
        AppendFloat(output, frame.sessionTime);
        output.Append(frame.frame).Append(',');
        AppendFloat(output, frame.blockTime);
        AppendEscaped(output, frame.mode);
        AppendEscaped(output, frame.route);
        AppendEscaped(output, frame.hold);
        AppendVector(output, frame.headPosition);
        AppendQuaternion(output, frame.headRotation);
        for (int i = 0; i < CaptureFrame.BoneCount; i++)
        {
            AppendVector(output, frame.leftPositions[i]);
            AppendQuaternion(output, frame.leftRotations[i]);
        }
        output.Append(frame.leftConfidence).Append(',');
        for (int i = 0; i < CaptureFrame.BoneCount; i++)
        {
            AppendVector(output, frame.rightPositions[i]);
            AppendQuaternion(output, frame.rightRotations[i]);
        }
        output.Append(frame.rightConfidence).Append(',');
        AppendEscaped(output, frame.touchedHold);
        output.Append(frame.gripFlag).Append(',')
            .Append(frame.perFingerContactMask).Append(',');
        AppendFloat(output, frame.gripScore, false);
        output.AppendLine();
    }

    public static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string escaped = value.Replace("\"", "\"\"");
        return escaped.IndexOfAny(EscapedCharacters) >= 0
            ? "\"" + escaped + "\""
            : escaped;
    }

    private static void AppendBoneHeader(StringBuilder output, char hand)
    {
        for (int i = 0; i < CaptureFrame.BoneCount; i++)
        {
            output.Append(hand).Append(i).Append("PosX,")
                .Append(hand).Append(i).Append("PosY,")
                .Append(hand).Append(i).Append("PosZ,")
                .Append(hand).Append(i).Append("RotX,")
                .Append(hand).Append(i).Append("RotY,")
                .Append(hand).Append(i).Append("RotZ,")
                .Append(hand).Append(i).Append("RotW,");
        }
    }

    private static void AppendVector(StringBuilder output, Vector3 value)
    {
        AppendFloat(output, value.x);
        AppendFloat(output, value.y);
        AppendFloat(output, value.z);
    }

    private static void AppendQuaternion(StringBuilder output, Quaternion value)
    {
        AppendFloat(output, value.x);
        AppendFloat(output, value.y);
        AppendFloat(output, value.z);
        AppendFloat(output, value.w);
    }

    private static void AppendFloat(StringBuilder output, float value, bool comma = true)
    {
        output.Append(value.ToString("F5", CultureInfo.InvariantCulture));
        if (comma)
        {
            output.Append(',');
        }
    }

    private static void AppendEscaped(StringBuilder output, string value)
    {
        output.Append(Escape(value)).Append(',');
    }

    private static string FormatEventVector(Vector3 value)
    {
        return FormattableString.Invariant($"({value.x:F3},{value.y:F3},{value.z:F3})");
    }
}
