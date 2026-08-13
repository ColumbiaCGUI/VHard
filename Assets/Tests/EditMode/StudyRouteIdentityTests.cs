using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public sealed class StudyRouteIdentityTests
{
    private static readonly string[] ApprovedRouteIds =
    {
        "MB2016-19215", "MB2016-21329", "MB2016-170190",
    };

    [Test]
    public void RouteCodeIsStableAndDistinctForTheApprovedRoutes()
    {
        string[] codes = ApprovedRouteIds.Select(StudyRouteIdentity.GetRouteCode).ToArray();

        Assert.That(codes.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(ApprovedRouteIds.Length));
        foreach (string code in codes)
        {
            Assert.That(code.Length, Is.EqualTo(4));
            Assert.That(code, Does.Match("^[2-9A-HJ-NP-Z]{4}$"));
        }
        Assert.That(
            StudyRouteIdentity.GetRouteCode(ApprovedRouteIds[0]),
            Is.EqualTo(StudyRouteIdentity.GetRouteCode(" mb2016-19215 ")));
    }

    [Test]
    public void RouteCodeMatchesTheShippedCatalogRouteIds()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "moonboard_2016_40.json");
        Assert.That(
            MoonBoardStudyCatalog.TryParse(File.ReadAllText(path), out MoonBoardStudyCatalog catalog, out string error),
            Is.True,
            error);

        Assert.That(catalog.routes.Select(route => route.id), Is.EquivalentTo(ApprovedRouteIds));
    }

    [Test]
    public void BlindLabelCarriesNoRouteIdentity()
    {
        string label = StudyRouteIdentity.FormatBlindLabel(ApprovedRouteIds[0], 0, ApprovedRouteIds.Length);

        Assert.That(label, Does.Contain("SLOT 1 / 3"));
        Assert.That(label, Does.Contain(StudyRouteIdentity.GetRouteCode(ApprovedRouteIds[0])));
        Assert.That(label, Does.Not.Contain(ApprovedRouteIds[0]));
        Assert.That(label, Does.Not.Contain("19215"));
        Assert.That(label, Does.Not.Contain("FAR FROM THE MADDING CROWD"));
        Assert.That(label, Does.Not.Contain("6B+"));
    }

    [Test]
    public void BlindLabelReportsMissingRoutes()
    {
        Assert.That(StudyRouteIdentity.FormatBlindLabel(string.Empty, 0, 0), Is.EqualTo("NO ROUTES"));
        Assert.That(StudyRouteIdentity.FormatSlot(0, 0), Is.EqualTo("NO ROUTES"));
        Assert.That(
            StudyRouteIdentity.FormatStepLabel(Array.Empty<string>(), 0, 1),
            Is.EqualTo("NO ROUTES"));
    }

    [Test]
    public void StepLabelNamesTheWrappedDestinationSlot()
    {
        List<string> routes = ApprovedRouteIds.ToList();

        Assert.That(
            StudyRouteIdentity.FormatStepLabel(routes, 0, -1),
            Is.EqualTo("< 3  " + StudyRouteIdentity.GetRouteCode(ApprovedRouteIds[2])));
        Assert.That(
            StudyRouteIdentity.FormatStepLabel(routes, 2, 1),
            Is.EqualTo("1  " + StudyRouteIdentity.GetRouteCode(ApprovedRouteIds[0]) + " >"));
        Assert.That(
            StudyRouteIdentity.FormatStepLabel(routes, 1, 1),
            Is.EqualTo("3  " + StudyRouteIdentity.GetRouteCode(ApprovedRouteIds[2]) + " >"));
    }

    [Test]
    public void CodeReferenceNamesARouteByItsCodeAlone()
    {
        Assert.That(
            StudyRouteIdentity.FormatCodeReference(ApprovedRouteIds[0]),
            Is.EqualTo("CODE " + StudyRouteIdentity.GetRouteCode(ApprovedRouteIds[0])));
        Assert.That(StudyRouteIdentity.FormatCodeReference(" "), Is.EqualTo("NO ROUTE"));
    }

    [Test]
    public void RouteFailureStatusReplacesTheDiagnosticWithTheCode()
    {
        Assert.That(
            StudyRouteIdentity.FormatRouteFailureStatus(ApprovedRouteIds[1]),
            Is.EqualTo("CODE " + StudyRouteIdentity.GetRouteCode(ApprovedRouteIds[1]) +
                       " is unavailable; see the log."));
        Assert.That(
            StudyRouteIdentity.FormatRouteFailureStatus(string.Empty),
            Is.EqualTo("The selected route is unavailable; see the log."));
    }

    /// <summary>
    /// The console names routes only through this class, so every formatter it can reach must
    /// stay blind for every shipped route. A formatter added later that takes a catalog record
    /// instead of a route id fails here rather than reaching the panel.
    /// </summary>
    [Test]
    public void EveryPublicFormatterStaysBlindForTheShippedRoutes()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "moonboard_2016_40.json");
        Assert.That(
            MoonBoardStudyCatalog.TryParse(File.ReadAllText(path), out MoonBoardStudyCatalog catalog, out string error),
            Is.True,
            error);
        MethodInfo[] formatters = typeof(StudyRouteIdentity)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.DeclaringType == typeof(StudyRouteIdentity) &&
                             method.ReturnType == typeof(string))
            .ToArray();

        Assert.That(formatters, Is.Not.Empty);
        foreach (MoonBoardRouteDefinition route in catalog.routes)
        {
            foreach (MethodInfo formatter in formatters)
            {
                string rendered = (string)formatter.Invoke(null, BuildBlindArguments(formatter, route.id));

                Assert.That(rendered, Does.Not.Contain(route.id), formatter.Name);
                Assert.That(rendered, Does.Not.Contain("MB2016"), formatter.Name);
                Assert.That(rendered, Does.Not.Contain(route.sourceProblemId), formatter.Name);
                Assert.That(
                    rendered.ToUpperInvariant(),
                    Does.Not.Contain(route.name.ToUpperInvariant()),
                    formatter.Name);
                Assert.That(rendered, Does.Not.Contain(route.grade), formatter.Name);
            }
        }
    }

    [Test]
    public void RouteCodeRejectsAnEmptyRouteId()
    {
        Assert.Throws<ArgumentException>(() => StudyRouteIdentity.GetRouteCode(" "));
    }

    private static object[] BuildBlindArguments(MethodInfo formatter, string routeId)
    {
        ParameterInfo[] parameters = formatter.GetParameters();
        object[] arguments = new object[parameters.Length];
        for (int index = 0; index < parameters.Length; index++)
        {
            Type parameterType = parameters[index].ParameterType;
            if (parameterType == typeof(string))
            {
                arguments[index] = routeId;
            }
            else if (parameterType == typeof(int))
            {
                arguments[index] = parameters[index].Name.EndsWith("Count", StringComparison.Ordinal)
                    ? ApprovedRouteIds.Length
                    : Array.IndexOf(ApprovedRouteIds, routeId);
            }
            else if (parameterType == typeof(IReadOnlyList<string>))
            {
                arguments[index] = ApprovedRouteIds;
            }
            else
            {
                Assert.Fail(
                    formatter.Name + " takes a " + parameterType.Name +
                    "; a console formatter must take route ids only, never a catalog record.");
            }
        }
        return arguments;
    }
}
