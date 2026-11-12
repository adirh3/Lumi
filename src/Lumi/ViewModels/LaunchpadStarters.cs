using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Lumi.Localization;
using Lumi.Models;
using StrataTheme.Controls;

namespace Lumi.ViewModels;

/// <summary>One run of a starter's label: plain words, or a blank the user fills in before sending.</summary>
/// <param name="SpaceBefore">The template has a space before this part; parts are laid out side by side.</param>
public sealed record LaunchpadTemplatePart(string Text, bool IsBlank, bool SpaceBefore);

/// <summary>
/// Starter templates: a blank is a short bracketed label ("Plan a trip to [where?]"). The pill shows it as a dotted
/// blank, and the composer receives the template as it is and selects each blank in turn for the user to type over.
/// </summary>
internal static partial class LaunchpadTemplate
{
    [GeneratedRegex(@"\[[^\[\]\r\n]{1,40}\]")]
    private static partial Regex Blank();

    /// <summary>The template's blanks as they appear in it, brackets included, in order and without repeats.</summary>
    internal static IReadOnlyList<string> Blanks(string template)
        => Blank().Matches(template).Select(static match => match.Value).Distinct(StringComparer.Ordinal).ToList();

    internal static IReadOnlyList<LaunchpadTemplatePart> Parse(string template)
    {
        var parts = new List<LaunchpadTemplatePart>();
        var position = 0;
        foreach (Match match in Blank().Matches(template))
        {
            AddText(template[position..match.Index]);
            var spaceBefore = parts.Count > 0 && match.Index > 0 && char.IsWhiteSpace(template[match.Index - 1]);
            parts.Add(new LaunchpadTemplatePart(match.Value[1..^1].Trim(), IsBlank: true, spaceBefore));
            position = match.Index + match.Length;
        }

        AddText(template[position..]);
        return parts;

        void AddText(string text)
        {
            var trimmed = text.Trim();
            if (trimmed.Length > 0)
                parts.Add(new LaunchpadTemplatePart(trimmed, IsBlank: false, parts.Count > 0 && char.IsWhiteSpace(text[0])));
        }
    }
}

/// <summary>
/// What the launchpad offers to start, by kind: the tabs under the greeting, and the starters each one holds. Every
/// starter has a stable id, so the launchpad can learn which ones the user picks (<see cref="StarterUse"/>).
/// </summary>
internal static class LaunchpadStarterCatalog
{
    internal const string ForYou = "for-you";
    internal const string Plan = "plan";
    internal const string Shop = "shop";
    internal const string Learn = "learn";
    internal const string Write = "write";
    internal const string Documents = "documents";
    internal const string Money = "money";
    internal const string Computer = "computer";
    internal const string Code = "code";

    /// <summary>The tabs after For you, always in this order, so each one stays where the user found it.</summary>
    internal static readonly IReadOnlyList<string> Order = [Plan, Shop, Learn, Write, Documents, Money, Computer, Code];

    /// <summary>
    /// Well-tried starters that fill For you when the user's own ones are few: the work first in a coding project. Each
    /// list alone fills For you, so its grid never has a gap.
    /// </summary>
    private static readonly string[] GeneralFillers = ["plan.trip", "shop.choose", "write.email", "learn.explain", "docs.summarize", "computer.space"];
    private static readonly string[] CodingFillers = ["code.review", "code.explain", "code.fix", "code.changes", "code.tests", "code.feature"];

    private static readonly (string Id, string Category, string Glyph, string Key)[] Ideas =
    [
        ("plan.trip", Plan, "✈️", nameof(Loc.Launchpad_IdeaPlanTrip)),
        ("plan.week", Plan, "📅", nameof(Loc.Launchpad_IdeaPlanWeek)),
        ("plan.event", Plan, "🎉", nameof(Loc.Launchpad_IdeaPlanEvent)),
        ("plan.meals", Plan, "🥗", nameof(Loc.Launchpad_IdeaPlanMeals)),
        ("plan.goal", Plan, "🎯", nameof(Loc.Launchpad_IdeaPlanGoal)),
        ("plan.prepare", Plan, "🎤", nameof(Loc.Launchpad_IdeaPlanPrepare)),
        ("shop.choose", Shop, "🛒", nameof(Loc.Launchpad_IdeaShopChoose)),
        ("shop.compare", Shop, "⚖️", nameof(Loc.Launchpad_IdeaShopCompare)),
        ("shop.deal", Shop, "🏷️", nameof(Loc.Launchpad_IdeaShopDeal)),
        ("shop.worth-it", Shop, "🤔", nameof(Loc.Launchpad_IdeaShopWorthIt)),
        ("shop.gift", Shop, "🎁", nameof(Loc.Launchpad_IdeaShopGift)),
        ("shop.reviews", Shop, "⭐", nameof(Loc.Launchpad_IdeaShopReviews)),
        ("learn.research", Learn, "🔍", nameof(Loc.Launchpad_IdeaLearnResearch)),
        ("learn.explain", Learn, "💡", nameof(Loc.Launchpad_IdeaLearnExplain)),
        ("learn.quiz", Learn, "🧠", nameof(Loc.Launchpad_IdeaLearnQuiz)),
        ("learn.news", Learn, "📰", nameof(Loc.Launchpad_IdeaLearnNews)),
        ("learn.fact-check", Learn, "✅", nameof(Loc.Launchpad_IdeaLearnFactCheck)),
        ("learn.skill", Learn, "🎓", nameof(Loc.Launchpad_IdeaLearnSkill)),
        ("write.email", Write, "✉️", nameof(Loc.Launchpad_IdeaWriteEmail)),
        ("write.reply", Write, "💬", nameof(Loc.Launchpad_IdeaWriteReply)),
        ("write.rewrite", Write, "🎨", nameof(Loc.Launchpad_IdeaWriteRewrite)),
        ("write.post", Write, "📣", nameof(Loc.Launchpad_IdeaWritePost)),
        ("write.translate", Write, "🌐", nameof(Loc.Launchpad_IdeaWriteTranslate)),
        ("write.proofread", Write, "✏️", nameof(Loc.Launchpad_IdeaWriteProofread)),
        ("docs.summarize", Documents, "📄", nameof(Loc.Launchpad_IdeaDocsSummarize)),
        ("docs.word", Documents, "📝", nameof(Loc.Launchpad_IdeaDocsWord)),
        ("docs.slides", Documents, "📊", nameof(Loc.Launchpad_IdeaDocsSlides)),
        ("docs.sheet", Documents, "📈", nameof(Loc.Launchpad_IdeaDocsSheet)),
        ("docs.organize", Documents, "🗂️", nameof(Loc.Launchpad_IdeaDocsOrganize)),
        ("docs.convert", Documents, "🔄", nameof(Loc.Launchpad_IdeaDocsConvert)),
        ("money.spending", Money, "💳", nameof(Loc.Launchpad_IdeaMoneySpending)),
        ("money.budget", Money, "🧾", nameof(Loc.Launchpad_IdeaMoneyBudget)),
        ("money.save", Money, "🐷", nameof(Loc.Launchpad_IdeaMoneySave)),
        ("money.loan", Money, "🏠", nameof(Loc.Launchpad_IdeaMoneyLoan)),
        ("money.split", Money, "➗", nameof(Loc.Launchpad_IdeaMoneySplit)),
        ("money.subscriptions", Money, "🔁", nameof(Loc.Launchpad_IdeaMoneySubscriptions)),
        ("computer.slow", Computer, "🐢", nameof(Loc.Launchpad_IdeaComputerSlow)),
        ("computer.space", Computer, "🧹", nameof(Loc.Launchpad_IdeaComputerSpace)),
        ("computer.fix", Computer, "🛠️", nameof(Loc.Launchpad_IdeaComputerFix)),
        ("computer.setup", Computer, "🔌", nameof(Loc.Launchpad_IdeaComputerSetup)),
        ("computer.security", Computer, "🔒", nameof(Loc.Launchpad_IdeaComputerSecurity)),
        ("computer.backup", Computer, "💾", nameof(Loc.Launchpad_IdeaComputerBackup)),
        ("code.explain", Code, "🗺️", nameof(Loc.Launchpad_IdeaCodeExplain)),
        ("code.fix", Code, "🐞", nameof(Loc.Launchpad_IdeaCodeFix)),
        ("code.tests", Code, "🧪", nameof(Loc.Launchpad_IdeaCodeTests)),
        ("code.review", Code, "👀", nameof(Loc.Launchpad_IdeaCodeReview)),
        ("code.changes", Code, "📜", nameof(Loc.Launchpad_IdeaCodeChanges)),
        ("code.feature", Code, "✨", nameof(Loc.Launchpad_IdeaCodeFeature)),
    ];

    /// <summary>The catalog's translation keys, for checks that every language keeps the same blanks.</summary>
    internal static IEnumerable<string> TemplateKeys => Ideas.Select(static idea => idea.Key);

    internal static string Title(string category) => category switch
    {
        ForYou => Loc.Launchpad_CategoryForYou,
        Plan => Loc.Launchpad_CategoryPlan,
        Shop => Loc.Launchpad_CategoryShop,
        Learn => Loc.Launchpad_CategoryLearn,
        Write => Loc.Launchpad_CategoryWrite,
        Documents => Loc.Launchpad_CategoryDocuments,
        Money => Loc.Launchpad_CategoryMoney,
        Computer => Loc.Launchpad_CategoryComputer,
        Code => Loc.Launchpad_CategoryCode,
        _ => category,
    };

    /// <summary>The starters a tab holds, in catalog order.</summary>
    internal static List<LaunchpadStarterSpec> In(string category)
        => Ideas.Where(idea => idea.Category == category).Select(ToSpec).ToList();

    /// <summary>The starters that fill For you after the user's own: the work first in a coding project.</summary>
    internal static List<LaunchpadStarterSpec> Fillers(bool isCodingProject)
        => (isCodingProject ? CodingFillers : GeneralFillers).Select(Find).OfType<LaunchpadStarterSpec>().ToList();

    /// <summary>
    /// A starter's line icon as a key, which the phone draws with the same Strata icon: its own for a catalog or
    /// time-of-day starter, a bookmark for a plan from the brief.
    /// </summary>
    internal static string IconKeyOf(string? id) => id switch
    {
        null => "bookmark",
        "plan.trip" => "globe",
        "plan.week" => "calendar",
        "plan.event" => "people",
        "plan.meals" => "checklist",
        "plan.goal" => "target",
        "plan.prepare" => "person",
        "shop.choose" => "tag",
        "shop.compare" => "overlap",
        "shop.deal" => "search",
        "shop.worth-it" => "question",
        "shop.gift" => "gift",
        "shop.reviews" => "chat",
        "learn.research" => "search",
        "learn.explain" => "lightbulb",
        "learn.quiz" => "check-circle",
        "learn.news" => "globe",
        "learn.fact-check" => "shield-check",
        "learn.skill" => "book",
        "write.email" => "mail",
        "write.reply" => "reply",
        "write.rewrite" => "wand",
        "write.post" => "share",
        "write.translate" => "globe",
        "write.proofread" => "pencil",
        "docs.summarize" => "document",
        "docs.word" => "document-plus",
        "docs.slides" => "layers",
        "docs.sheet" => "database",
        "docs.organize" => "folder",
        "docs.convert" => "files",
        "money.spending" => "wallet",
        "money.budget" => "checklist",
        "money.save" => "target",
        "money.loan" => "calendar",
        "money.split" => "people",
        "money.subscriptions" => "retry",
        "computer.slow" => "bolt",
        "computer.space" => "database",
        "computer.fix" => "sliders",
        "computer.setup" => "plug",
        "computer.security" => "shield",
        "computer.backup" => "copy",
        "code.explain" => "book",
        "code.fix" => "bug",
        "code.tests" => "beaker",
        "code.review" => "diff",
        "code.changes" => "branch",
        "code.feature" => "cube",
        "day.plan" => "checklist",
        "day.research" => "search",
        "day.document" => "document-plus",
        "day.tomorrow" => "calendar",
        "day.catch-up" => "chats",
        "day.focus" => "target",
        "day.recap" => "clock",
        _ => "lightbulb",
    };

    /// <summary>A starter's line icon (<see cref="IconKeyOf"/>).</summary>
    internal static Geometry IconOf(string? id) => IconOfKey(IconKeyOf(id));

    /// <summary>The line icon an icon key names (<see cref="Lumi.Remote.Protocol.RemoteProtocol.LaunchpadIcons"/>).</summary>
    internal static Geometry IconOfKey(string? key) => key switch
    {
        "bookmark" => StrataIcons.Bookmark,
        "globe" => StrataIcons.Globe,
        "calendar" => StrataIcons.Calendar,
        "people" => StrataIcons.People,
        "checklist" => StrataIcons.Checklist,
        "target" => StrataIcons.Target,
        "person" => StrataIcons.Person,
        "tag" => StrataIcons.Tag,
        "overlap" => StrataIcons.Overlap,
        "search" => StrataIcons.Search,
        "question" => StrataIcons.Question,
        "gift" => StrataIcons.Gift,
        "chat" => StrataIcons.Chat,
        "check-circle" => StrataIcons.CheckCircle,
        "shield-check" => StrataIcons.ShieldCheck,
        "book" => StrataIcons.Book,
        "mail" => StrataIcons.Mail,
        "reply" => StrataIcons.Reply,
        "wand" => StrataIcons.Wand,
        "share" => StrataIcons.Share,
        "pencil" => StrataIcons.Pencil,
        "document" => StrataIcons.Document,
        "document-plus" => StrataIcons.DocumentPlus,
        "layers" => StrataIcons.Layers,
        "database" => StrataIcons.Database,
        "folder" => StrataIcons.Folder,
        "files" => StrataIcons.Files,
        "wallet" => StrataIcons.Wallet,
        "retry" => StrataIcons.Retry,
        "bolt" => StrataIcons.Bolt,
        "sliders" => StrataIcons.Sliders,
        "plug" => StrataIcons.Plug,
        "shield" => StrataIcons.Shield,
        "copy" => StrataIcons.Copy,
        "bug" => StrataIcons.Bug,
        "beaker" => StrataIcons.Beaker,
        "diff" => StrataIcons.Diff,
        "branch" => StrataIcons.Branch,
        "cube" => StrataIcons.Cube,
        "chats" => StrataIcons.Chats,
        "clock" => StrataIcons.Clock,
        _ => StrataIcons.Lightbulb,
    };

    /// <summary>A catalog or time-of-day starter by id, in the current language; null for an id no longer offered.</summary>
    internal static LaunchpadStarterSpec? Find(string id)
    {
        foreach (var idea in Ideas)
        {
            if (idea.Id == id)
                return ToSpec(idea);
        }

        return LaunchpadPlanner.TimeOfDayStarter(id);
    }

    private static LaunchpadStarterSpec ToSpec((string Id, string Category, string Glyph, string Key) idea)
    {
        var template = Loc.Get(idea.Key);
        return new LaunchpadStarterSpec(idea.Glyph, template, template) { Id = idea.Id, CategoryId = idea.Category };
    }
}

/// <summary>
/// What the launchpad learns from the starters the user picks: a recency-weighted count per starter and place
/// (a project, or none), which puts the user's habits in For you.
/// </summary>
internal static class LaunchpadStarterUse
{
    /// <summary>A pick's weight halves in two weeks, so a habit the user dropped fades.</summary>
    private const double HalfLifeDays = 14;

    /// <summary>Picked at least this often, and still this weighty, a starter is a habit.</summary>
    internal const int HabitMinCount = 2;
    internal const double HabitMinScore = 1.2;

    /// <summary>The profile keeps this many records at most; the faintest go first.</summary>
    internal const int MaxRecords = 200;

    internal static double ScoreAt(StarterUse use, DateTimeOffset now)
    {
        var days = Math.Max(0, (now - use.LastUsedAt).TotalDays);
        return use.Score * Math.Pow(0.5, days / HalfLifeDays);
    }

    internal static void Record(List<StarterUse> uses, string id, Guid? projectId, DateTimeOffset now)
    {
        var use = uses.FirstOrDefault(item => item.Id == id && item.ProjectId == projectId);
        if (use is null)
        {
            use = new StarterUse { Id = id, ProjectId = projectId };
            uses.Add(use);
        }

        use.Score = ScoreAt(use, now) + 1;
        use.Count++;
        use.LastUsedAt = now;

        if (uses.Count > MaxRecords)
        {
            var faintest = uses.OrderBy(item => ScoreAt(item, now)).First(item => !ReferenceEquals(item, use));
            uses.Remove(faintest);
        }
    }

    /// <summary>The starters the user keeps picking in this place, most used first.</summary>
    internal static List<string> Habits(IEnumerable<StarterUse> uses, Guid? projectId, DateTimeOffset now)
        => uses
            .Where(use => use.ProjectId == projectId && use.Count >= HabitMinCount)
            .Select(use => (use.Id, Score: ScoreAt(use, now)))
            .Where(use => use.Score >= HabitMinScore)
            .OrderByDescending(use => use.Score)
            .Select(use => use.Id)
            .ToList();
}
