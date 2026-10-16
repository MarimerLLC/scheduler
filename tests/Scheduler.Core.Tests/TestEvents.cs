using NodaTime;
using Scheduler.Core.Model;
using Scheduler.Core.Security;

namespace Scheduler.Core.Tests;

/// <summary>Builds in-memory events for tests.</summary>
internal static class TestEvents
{
    public static SchedulingEvent WithSlots(int count)
    {
        var ev = new SchedulingEvent
        {
            ShortCode = Tokens.NewShortCode(),
            Title = "Planning",
            OrganizerEmail = "rocky@example.com",
            OrganizerTimeZone = "America/Chicago",
            Duration = Duration.FromMinutes(30),
        };

        for (var i = 0; i < count; i++)
        {
            ev.Slots.Add(new ProposedSlot
            {
                EventId = ev.Id,
                TimeZoneId = "America/Chicago",
                LocalStart = new LocalDateTime(2026, 11, 3 + i, 10, 0),
                Start = Instant.FromUtc(2026, 11, 3 + i, 16, 0),
            });
        }

        return ev;
    }

    public static Participant AddParticipant(this SchedulingEvent ev, string email, AttendeeRole role = AttendeeRole.Required)
    {
        var participant = new Participant
        {
            EventId = ev.Id,
            Email = email,
            Role = role,
            TokenNonce = MagicLinks.NewNonce(),
        };
        ev.Participants.Add(participant);
        return participant;
    }

    public static Participant Votes(this Participant participant, ProposedSlot slot, VoteValue value)
    {
        participant.Votes.Add(new Vote { ParticipantId = participant.Id, SlotId = slot.Id, Value = value, Channel = Channel.Mcp });
        return participant;
    }
}
