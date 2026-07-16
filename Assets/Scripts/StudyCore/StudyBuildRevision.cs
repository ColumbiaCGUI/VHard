using UnityEngine;

public static class StudyBuildRevision
{
    public static string Current
    {
        get
        {
            TextAsset revision = Resources.Load<TextAsset>("StudyBuildRevision");
            string value = revision != null ? revision.text.Trim() : string.Empty;
            return !string.IsNullOrEmpty(value) ? value : Application.buildGUID;
        }
    }
}
