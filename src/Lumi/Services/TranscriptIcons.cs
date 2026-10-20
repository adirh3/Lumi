namespace Lumi.Services;

/// <summary>
/// Keys for the kinds of work an AI's transcript shows. View models carry a key; the view draws the
/// matching line icon (<see cref="Lumi.Converters.TranscriptIconConverter"/>).
/// </summary>
public static class TranscriptIcons
{
    public const string Read = "read";
    public const string Create = "create";
    public const string Edit = "edit";
    public const string Search = "search";
    public const string Web = "web";
    public const string Page = "page";
    public const string Terminal = "terminal";
    public const string Plan = "plan";
    public const string Screenshot = "screenshot";
    public const string Desktop = "desktop";
    public const string Memory = "memory";
    public const string Skill = "skill";
    public const string Project = "project";
    public const string Agent = "agent";
    public const string Connector = "connector";
    public const string Chat = "chat";
    public const string Chats = "chats";
    public const string Test = "test";
    public const string Data = "data";
    public const string Question = "question";
    public const string Mixed = "mixed";
    public const string Tool = "tool";
}
