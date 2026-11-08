using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PatchAdams.Core;

static class ComparisonFindingTests
{
    public static void FixtureEvidenceRoundTrips()
    {
        using var temporary = new TemporaryDirectory();
        var contents = CreateFixtureSession();
        SessionPackage.Write(contents, temporary.Path);
        var reopened = SessionPackage.Read(temporary.Path);
        Assert.Equal(3, reopened.Session.Version, "comparison sessions use version 3");
        Assert.Equal(JsonSerializer.Serialize(contents.Session.ComparisonFindings),
            JsonSerializer.Serialize(reopened.Session.ComparisonFindings), "all comparison data round-trips exactly");
        foreach (var (label, text) in contents.PatchTexts)
            Assert.Equal(text, reopened.PatchTexts[label], $"fixture {label} remains unchanged");
        Assert.Equal(contents.Session.Findings.Single().Id, reopened.Session.Findings.Single().Id,
            "ordinary findings remain separate and unchanged");
        Assert.Equal(contents.Session.ReviewerFeedback, reopened.Session.ReviewerFeedback, "review feedback unchanged");
        Assert.Equal(1, reopened.Session.Ranking.RankOf("A"), "comparison storage does not change ranking");
        var finding = reopened.Session.ComparisonFindings.Single();
        Assert.True(finding.Evidence.Select(item => item.Path).Distinct().Count() == 2,
            "comparison covers different files");
        Assert.True(finding.Evidence.Any(item => item.SelectedRanges.Count > 1), "disconnected evidence retained");
        Assert.True(finding.Evidence.Any(item => item.SelectedRanges.Any(range => range.IsBase)),
            "removed-base evidence retained");
        foreach (var evidence in finding.Evidence)
        {
            Assert.Equal(ReferenceGenerator.ForRanges(evidence.Ranges, evidence.Path.Split('/').Last()),
                evidence.Reference, "reference derived from that evidence's ranges");
            Assert.True(reopened.Session.DisplayLabel(evidence.Label) != evidence.Label,
                "stable internal labels remain compatible with blind display");
        }
        var json = JsonNode.Parse(Encoding.UTF8.GetString(MarkdownExport.RenderJson(reopened.Session)))!;
        Assert.Equal(finding.Id, json["session"]!["comparisonFindings"]![0]!["id"]!.GetValue<Guid>(),
            "JSON export includes comparisons without mixing them into ordinary findings");
        Assert.Equal(1, json["session"]!["findings"]!.AsArray().Count, "ordinary finding count unchanged");

        var changed = finding with { Description = "Updated comparison\r\nKeep whitespace.  \n" };
        var updated = reopened.Session with { ComparisonFindings = [changed] };
        SessionPackage.Write(new SessionContents(updated, reopened.PatchTexts), temporary.Path);
        var savedAgain = SessionPackage.Read(temporary.Path).Session.ComparisonFindings.Single();
        Assert.Equal(changed.Id, savedAgain.Id, "identity survives editing and resaving");
        Assert.Equal(changed.Description, savedAgain.Description, "edited shared assessment persists");
        Assert.Equal(finding.Evidence[1].Note, savedAgain.Evidence[1].Note, "per-patch note remains independent");
    }

    public static void OlderSessionsDefaultToEmptyComparisons()
    {
        using var temporary = new TemporaryDirectory();
        var contents = CreateFixtureSession();
        SessionPackage.Write(contents, temporary.Path);
        var path = Path.Combine(temporary.Path, "session.json");
        var original = File.ReadAllText(path);
        foreach (var version in new[] { 1, 2 })
        {
            var manifest = JsonNode.Parse(original)!;
            manifest["version"] = version;
            manifest.AsObject().Remove("comparisonFindings");
            File.WriteAllText(path, manifest.ToJsonString());
            var reopened = SessionPackage.Read(temporary.Path);
            Assert.Equal(0, reopened.Session.ComparisonFindings.Count, $"version {version} defaults to no comparisons");
            Assert.Equal(1, reopened.Session.Findings.Count, "legacy ordinary finding preserved");
            SessionPackage.Write(reopened, temporary.Path);
            Assert.Equal(3, SessionPackage.Read(temporary.Path).Session.Version, "resaving upgrades schema");
        }
        Assert.Throws<InvalidDataException>(() => SessionPackage.Encode(contents.Session with { Version = 2 }),
            "raw encoding cannot disguise comparison data as an older schema");
        SessionPackage.Write(contents with { Session = contents.Session with { Version = 2 } }, temporary.Path);
        Assert.Equal(3, SessionPackage.Read(temporary.Path).Session.Version, "package writer promotes schema before saving");
        var future = JsonNode.Parse(original)!;
        future["version"] = 4;
        File.WriteAllText(path, future.ToJsonString());
        Assert.Throws<InvalidDataException>(() => SessionPackage.Read(temporary.Path), "future session versions rejected");
    }

    public static void InvalidEvidenceIsRejectedBeforeWriting()
    {
        using var temporary = new TemporaryDirectory();
        var contents = CreateFixtureSession();
        var comparison = contents.Session.ComparisonFindings.Single();
        var evidence = comparison.Evidence[0];
        var invalidFindings = new[]
        {
            comparison with { Evidence = [evidence] },
            comparison with { Evidence = [evidence, evidence] },
            comparison with { Evidence = [evidence with { Label = "unknown" }, comparison.Evidence[1]] },
            comparison with { Evidence = [evidence with { Ranges = [] }, comparison.Evidence[1]] },
            comparison with { Evidence = [evidence with { Ranges = [new(new LineSpan(0, 1))] }, comparison.Evidence[1]] },
            comparison with { Evidence = [evidence with { Ranges = [new(new LineSpan(5, 4))] }, comparison.Evidence[1]] },
            comparison with { Title = " " },
            comparison with { Description = "" },
            comparison with { Category = "" },
            comparison with { Id = Guid.Empty },
            comparison with { Severity = (FindingSeverity)100 }
        };
        foreach (var finding in invalidFindings)
        {
            var invalid = contents with { Session = contents.Session with { ComparisonFindings = [finding] } };
            var destination = Path.Combine(temporary.Path, "must-not-be-created");
            Assert.Throws<InvalidDataException>(() => SessionPackage.Write(invalid, destination),
                "invalid comparison rejected on write");
            Assert.True(!Directory.Exists(destination), "validation happens before filesystem writes");
        }
        foreach (var path in new[] { "/outside.cs", "C:\\outside.cs", "..\\outside.cs", "a/../b.cs", "\\\\server\\file.cs" })
        {
            var invalid = contents.Session with
            {
                ComparisonFindings = [comparison with
                {
                    Evidence = [evidence with { Path = path }, comparison.Evidence[1]]
                }]
            };
            Assert.Throws<InvalidDataException>(() => SessionPackage.Encode(invalid), $"unsafe path {path} rejected");
        }
        Assert.Throws<InvalidDataException>(() => SessionPackage.Encode(contents.Session with
        {
            ComparisonFindings = [comparison, comparison]
        }), "duplicate IDs rejected");
    }

    public static void MalformedManifestsAreRejected()
    {
        using var temporary = new TemporaryDirectory();
        SessionPackage.Write(CreateFixtureSession(), temporary.Path);
        var path = Path.Combine(temporary.Path, "session.json");
        var original = File.ReadAllText(path);
        var mutations = new Action<JsonNode>[]
        {
            root => root["comparisonFindings"] = null,
            root => root["comparisonFindings"]![0] = null,
            root => root["comparisonFindings"]![0]!["evidence"] = null,
            root => root["comparisonFindings"]![0]!["evidence"]![0] = null,
            root => root["comparisonFindings"]![0]!["evidence"]![0]!["ranges"] = null,
            root => root["comparisonFindings"]![0]!["evidence"]![0]!["ranges"]![0] = null,
            root => root["comparisonFindings"]![0]!["evidence"]![0]!["ranges"]![0]!["lines"] = null,
            root => root["comparisonFindings"]![0]!["evidence"]![0]!["ranges"]![0]!["lines"]!["lower"] = -1,
            root => root["comparisonFindings"]![0]!["evidence"]![0]!["path"] = "../outside.cs",
            root => root["comparisonFindings"]![0]!["evidence"]![0]!["label"] = "missing",
            root => root["comparisonFindings"]![0]!["evidence"]![0]!["note"] = null,
            root => root["version"] = 2
        };
        foreach (var mutate in mutations)
        {
            var manifest = JsonNode.Parse(original)!;
            mutate(manifest);
            File.WriteAllText(path, manifest.ToJsonString());
            Assert.Throws<InvalidDataException>(() => SessionPackage.Read(temporary.Path),
                "malformed comparison manifest rejected");
        }
    }

    private static SessionContents CreateFixtureSession()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "Fixtures", "patches")))
            root = root.Parent;
        if (root is null) throw new DirectoryNotFoundException("Fixture root not found.");
        var texts = new[] { "kettle-1.patch", "kettle-2.patch" }
            .Select(name => File.ReadAllText(Path.Combine(root.FullName, "Fixtures", "patches", name))).ToArray();
        var patches = texts.Select(text => UnifiedDiffParser.Parse(text)).ToArray();
        var provider = new InMemoryFileProvider(SyntheticBase.Merge(patches));
        var applied = patches.Select(patch => ApplyEngine.Apply(patch, provider)).ToArray();
        var first = applied[0].Files.First(file => file.PostText is not null &&
            DisplayLines.Create(file).Any(line => line.Kind == DisplayLineKind.Removed));
        var second = applied[1].Files.First(file => file.Path != first.Path && file.PostText is not null);
        ComparisonEvidence Evidence(string label, AppliedFile file)
        {
            var rows = DisplayLines.Create(file).Where(line =>
                line.Kind is not DisplayLineKind.SyntheticFiller and not DisplayLineKind.CollapsedContext).ToArray();
            var selected = new[] { rows.First(), rows.Last() }
                .Concat(rows.Where(row => row.Kind == DisplayLineKind.Removed).Take(1));
            return new ComparisonEvidence(label, file.Path, SelectionRanges.FromDisplayLines(selected),
                $"Observation for {label}\r\nPreserve formatting.  \n");
        }
        var a = Evidence("A", first);
        var b = Evidence("B", second);
        var comparison = new ComparisonFinding(Guid.NewGuid(), "Compare implementations",
            "A and B make different choices.\r\n\tKeep exact whitespace.  \n", "other",
            FindingSeverity.Material, RequestRelevance.Unclear, [a, b], DateTimeOffset.UtcNow);
        var ordinary = new SessionFinding(Guid.NewGuid(), "A", a.Path, a.Ranges[0].Lines, a.Reference,
            "Independent ordinary finding", "Unchanged", "other", FindingSeverity.Minor,
            RequestRelevance.No, false, "", [], DateTimeOffset.UtcNow, a.Ranges);
        var session = new ReviewSession(ReviewSession.CurrentVersion, "Comparison persistence", "",
            new SessionBase("synthetic"),
            [new SessionColumn("A", "kettle-1.patch", ""), new SessionColumn("B", "kettle-2.patch", "")],
            null, ReviewSession.DefaultCategories, [ordinary],
            new SessionRanking(new Dictionary<string, int> { ["A"] = 1 }, ""), [], [],
            new BlindMode(true, 42), DateTimeOffset.UtcNow, ReviewerFeedback: "Unchanged feedback")
        {
            ComparisonFindings = [comparison]
        };
        return new SessionContents(session, new Dictionary<string, string> { ["A"] = texts[0], ["B"] = texts[1] });
    }
}
