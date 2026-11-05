using System.Text;
using System.Text.Json.Nodes;
using PatchAdams.Core;

static class ReviewerFeedbackTests
{
    public static void FeedbackRoundTripsWithFixture()
    {
        using var temporary = new TemporaryDirectory();
        var (session, patches) = CreateFixtureSession();
        var feedback = "Reviewer — Slack feedback\r\n\r\n" +
            string.Concat(Enumerable.Repeat("• This finding is mistaken; the caller already checks it.\r\n", 2_000)) +
            "\tKeep this indentation and trailing whitespace.  \r\n";
        session = session with { ReviewerFeedback = feedback };
        SessionPackage.Write(new SessionContents(session, patches), temporary.Path);
        var reopened = SessionPackage.Read(temporary.Path);
        Assert.Equal(feedback, reopened.Session.ReviewerFeedback, "large multiline feedback is preserved exactly");
        Assert.Equal(patches["A"], reopened.PatchTexts["A"], "fixture patch remains unchanged");
        var json = JsonNode.Parse(Encoding.UTF8.GetString(MarkdownExport.RenderJson(reopened.Session)))!;
        Assert.Equal(feedback, json["session"]!["reviewerFeedback"]!.GetValue<string>(), "JSON includes feedback");
        Assert.True(!MarkdownExport.Render(reopened.Session).Contains("Slack feedback", StringComparison.Ordinal),
            "private reviewer feedback is not part of the Markdown report");
    }

    public static void OlderSessionsHaveEmptyFeedback()
    {
        using var temporary = new TemporaryDirectory();
        var (session, patches) = CreateFixtureSession();
        SessionPackage.Write(new SessionContents(session, patches), temporary.Path);
        var path = Path.Combine(temporary.Path, "session.json");
        var manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        manifest.Remove("reviewerFeedback");
        File.WriteAllText(path, manifest.ToJsonString());
        Assert.Equal("", SessionPackage.Read(temporary.Path).Session.ReviewerFeedback,
            "missing optional feedback defaults to empty text");
    }

    private static (ReviewSession Session, IReadOnlyDictionary<string, string> Patches) CreateFixtureSession()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "Fixtures", "patches")))
            root = root.Parent;
        if (root is null) throw new DirectoryNotFoundException("Could not find fixtures.");
        var text = File.ReadAllText(Path.Combine(root.FullName, "Fixtures", "patches", "kettle-1.patch"));
        _ = UnifiedDiffParser.Parse(text);
        var session = new ReviewSession(ReviewSession.CurrentVersion, "Feedback review", "",
            new SessionBase("synthetic"), [new SessionColumn("A", "kettle-1.patch", "")], null,
            ReviewSession.DefaultCategories, [], new SessionRanking(new Dictionary<string, int>(), ""),
            [], [], new BlindMode(false, 1), DateTimeOffset.UtcNow);
        return (session, new Dictionary<string, string> { ["A"] = text });
    }
}
