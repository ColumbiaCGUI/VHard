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
            "P01,1,A,MB2016-19215\n" +
            "P01,2,B,MB2016-21329\n" +
            "P01,3,C,MB2016-170190\n";

        yield return null;

        Assert.That(StudySchedule.TryParse(csv, out var rows, out string error), Is.True, error);
        Assert.That(rows, Has.Count.EqualTo(3));
    }
}
