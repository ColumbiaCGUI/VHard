using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// The estimation battery is reachable from the manual experimenter console, which runs outside
/// any scheduled block. These cover the console surface it binds to and the strings it can render.
/// </summary>
public sealed class ManualEstimationConsoleTests
{
    [Test]
    public void AdvanceLabelNamesTheDestinationOfTheNextPress()
    {
        Assert.That(ManualEstimationPolicy.FormatAdvanceLabel(0, 12), Is.EqualTo("NEXT 2 / 12"));
        Assert.That(ManualEstimationPolicy.FormatAdvanceLabel(10, 12), Is.EqualTo("NEXT 12 / 12"));
        Assert.That(ManualEstimationPolicy.FormatAdvanceLabel(11, 12), Is.EqualTo("FINISH 12 / 12"));
    }

    [Test]
    public void AdvanceLabelFallsBackToTheStartLabelWithoutContent()
    {
        Assert.That(ManualEstimationPolicy.FormatAdvanceLabel(0, 0),
            Is.EqualTo(ManualEstimationPolicy.StartLabel));
        Assert.That(ManualEstimationPolicy.FormatAdvanceLabel(-1, 12), Is.EqualTo("NEXT 2 / 12"));
        Assert.That(ManualEstimationPolicy.FormatAdvanceLabel(99, 12), Is.EqualTo("FINISH 12 / 12"));
    }

    [Test]
    public void ProgressReadoutCarriesThePositionAndTheCodeAlone()
    {
        Assert.That(
            ManualEstimationPolicy.FormatProgressReadout("MB2016-386882", 2, 12),
            Is.EqualTo("ESTIMATE 3 / 12     " + StudyRouteIdentity.FormatCodeReference("MB2016-386882")));
        Assert.That(
            ManualEstimationPolicy.FormatProgressReadout("MB2016-386882", 0, 0),
            Is.EqualTo(ManualEstimationPolicy.EmptyReadout));
        Assert.That(
            ManualEstimationPolicy.FormatProgressReadout(" ", 0, 12),
            Is.EqualTo(ManualEstimationPolicy.EmptyReadout));
    }

    /// <summary>
    /// The estimation problems carry names, setters and community grades, and the participant is
    /// looking at the board while the experimenter holds the console. Every string the manual
    /// cycle can render must survive this for all shipped problems, not just the climbed routes.
    /// </summary>
    [Test]
    public void EveryConsoleStringForTheEstimationBatteryStaysBlind()
    {
        MoonBoardEstimationCatalog catalog = LoadEstimationCatalog();
        MethodInfo[] formatters = typeof(ManualEstimationPolicy)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.DeclaringType == typeof(ManualEstimationPolicy) &&
                             method.ReturnType == typeof(string))
            .ToArray();

        Assert.That(formatters, Is.Not.Empty);
        List<MoonBoardEstimationProblemDefinition> problems = catalog.problems.ToList();
        problems.Add(catalog.practiceProblem);
        for (int index = 0; index < problems.Count; index++)
        {
            MoonBoardEstimationProblemDefinition problem = problems[index];
            foreach (MethodInfo formatter in formatters)
            {
                string rendered = (string)formatter.Invoke(
                    null,
                    BuildBlindArguments(formatter, problem.id, index, problems.Count));

                Assert.That(rendered, Does.Not.Contain(problem.id), formatter.Name);
                Assert.That(rendered, Does.Not.Contain("MB2016"), formatter.Name);
                Assert.That(rendered, Does.Not.Contain(problem.apiId.ToString()), formatter.Name);
                Assert.That(rendered, Does.Not.Contain(problem.grade), formatter.Name);
                Assert.That(rendered, Does.Not.Contain(problem.vGrade), formatter.Name);
                Assert.That(
                    rendered.ToUpperInvariant(),
                    Does.Not.Contain(problem.name.ToUpperInvariant()),
                    formatter.Name);
                Assert.That(
                    rendered.ToUpperInvariant(),
                    Does.Not.Contain(problem.setter.ToUpperInvariant()),
                    formatter.Name);
            }
        }
    }

    /// <summary>
    /// The manual cycle walks <see cref="MoonBoardEstimationCatalog.problems"/> in catalog order,
    /// so that array is what has to hold estimation-only content and leave practice out.
    /// </summary>
    [Test]
    public void ManualCycleSourceHoldsTheTwelveEstimationProblemsWithoutPractice()
    {
        MoonBoardEstimationCatalog catalog = LoadEstimationCatalog();

        Assert.That(catalog.problems, Has.Length.EqualTo(12));
        Assert.That(
            catalog.problems.Select(problem => problem.purpose),
            Is.All.EqualTo("estimation-only"));
        Assert.That(catalog.practiceProblem.purpose, Is.EqualTo("practice-only"));
        Assert.That(
            catalog.problems.Any(problem => problem.apiId == catalog.practiceProblem.apiId),
            Is.False);
        Assert.That(
            catalog.problems.Select(problem => StudyRouteIdentity.GetRouteCode(problem.id)).Distinct(),
            Has.Exactly(12).Items,
            "The console names every problem by its code, so the codes cannot collide.");
    }

    [Test]
    public void EstimationControllerExposesTheSurfaceTheConsoleBindsTo()
    {
        Type controller = FindLoadedType("EstimationController");

        MethodInfo start = controller.GetMethod(
            "StartManualEstimation",
            BindingFlags.Public | BindingFlags.Instance);
        Assert.That(start, Is.Not.Null);
        Assert.That(start.GetParameters(), Is.Empty);
        Assert.That(start.ReturnType, Is.EqualTo(typeof(bool)));
        Assert.That(
            controller.GetMethod("NextEstimation", BindingFlags.Public | BindingFlags.Instance),
            Is.Not.Null);
        Assert.That(
            controller.GetMethod("EndEstimation", BindingFlags.Public | BindingFlags.Instance),
            Is.Not.Null,
            "Start and reset close an open estimation cycle through the console.");
        Assert.That(
            controller.GetMethod("GetAdvanceLabel", BindingFlags.Public | BindingFlags.Instance)
                ?.ReturnType,
            Is.EqualTo(typeof(string)));
        Assert.That(
            controller.GetMethod("GetProgressReadout", BindingFlags.Public | BindingFlags.Instance)
                ?.ReturnType,
            Is.EqualTo(typeof(string)));
    }

    [Test]
    public void ConsoleKeepsOneEstimationControlInsteadOfTheUnwiredPair()
    {
        Type panel = FindLoadedType("StudyControlPanel");
        const BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        Assert.That(panel.GetField("estimationButton", NonPublicInstance), Is.Not.Null);
        Assert.That(
            panel.GetField("estimationStartButton", NonPublicInstance),
            Is.Null,
            "The unwired scheduled-estimation buttons were the reason the battery never appeared.");
        Assert.That(panel.GetField("estimationNextButton", NonPublicInstance), Is.Null);
    }

    private static MoonBoardEstimationCatalog LoadEstimationCatalog()
    {
        string mainPath = Path.Combine(Application.streamingAssetsPath, "moonboard_2016_40.json");
        Assert.That(
            MoonBoardStudyCatalog.TryParse(
                File.ReadAllText(mainPath),
                out MoonBoardStudyCatalog main,
                out string error),
            Is.True,
            error);
        string estimationPath = Path.Combine(
            Application.streamingAssetsPath,
            "moonboard_2016_40_estimation.json");
        Assert.That(
            MoonBoardEstimationCatalog.TryParseApproved(
                File.ReadAllText(estimationPath),
                main,
                out MoonBoardEstimationCatalog catalog,
                out error),
            Is.True,
            error);
        return catalog;
    }

    private static object[] BuildBlindArguments(
        MethodInfo formatter,
        string problemId,
        int ordinal,
        int count)
    {
        ParameterInfo[] parameters = formatter.GetParameters();
        object[] arguments = new object[parameters.Length];
        for (int index = 0; index < parameters.Length; index++)
        {
            Type parameterType = parameters[index].ParameterType;
            if (parameterType == typeof(string))
            {
                arguments[index] = problemId;
            }
            else if (parameterType == typeof(int))
            {
                arguments[index] = parameters[index].Name == "count" ? count : ordinal;
            }
            else
            {
                Assert.Fail(
                    formatter.Name + " takes a " + parameterType.Name +
                    "; a console formatter must take problem ids only, never a catalog record.");
            }
        }
        return arguments;
    }

    private static Type FindLoadedType(string name)
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(name))
            .Single(type => type != null);
    }
}
