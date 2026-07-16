using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;

public sealed class StudyCorePlayModeTests
{
    [UnityTest]
    public IEnumerator ScheduleParserRemainsAvailableInPlayerLoop()
    {
        const string csv =
            "participant,block,condition,route\n" +
            "P01,1,A,DEATH STAR\n" +
            "P01,2,B,SPEED\n" +
            "P01,3,C,THE CRUSH ALT\n";

        yield return null;

        Assert.That(StudySchedule.TryParse(csv, out var rows, out string error), Is.True, error);
        Assert.That(rows, Has.Count.EqualTo(3));
    }
}
