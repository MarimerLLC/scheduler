namespace Scheduler.Core;

/// <summary>
/// Hard limits on user-supplied fields. Titles, descriptions and notes are written by one person and
/// read by another person's agent, so they are kept short to limit what a prompt injection can carry.
/// See docs/design.md, "Prompt injection".
/// </summary>
public static class FieldLimits
{
    public const int TitleMax = 120;
    public const int DescriptionMax = 1000;
    public const int NoteMax = 280;
    public const int DisplayNameMax = 100;
    public const int EmailMax = 254;
    public const int TimeZoneIdMax = 64;

    public const int MaxSlotsPerEvent = 20;
    public const int MaxParticipantsPerEvent = 50;

    public const int MinDurationMinutes = 5;
    public const int MaxDurationMinutes = 8 * 60;
}
