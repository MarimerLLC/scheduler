namespace Scheduler.Web.Components.Shared;

/// <summary>Whether something the person just did worked.</summary>
public enum NoticeKind
{
    Success,
    Error,
}

/// <summary>
/// The outcome of something the person just did (saved, sent, refused), shown by <see cref="StatusNotice"/>. A page
/// makes a new one for every outcome, even with the same text: the notice is keyed on the instance, so saving twice
/// shows it arriving again. A class rather than a record on purpose, so two equal outcomes are still two notices.
/// </summary>
public sealed class Notice
{
    private Notice(NoticeKind kind, string text)
    {
        Kind = kind;
        Text = text;
    }

    public NoticeKind Kind { get; }

    public string Text { get; }

    public bool IsError => Kind == NoticeKind.Error;

    public static Notice Success(string text) => new(NoticeKind.Success, text);

    public static Notice Error(string text) => new(NoticeKind.Error, text);
}
