using Scheduler.Core.Model;

namespace Scheduler.Core.Tests;

/// <summary>
/// The milestone names on an event's status (docs/design.md, "Push updates"): the vote milestones from the
/// organizer-only fields, then how the event ended, if it has.
/// </summary>
[TestClass]
public sealed class EventMilestoneTests
{
    private static readonly string[] Carol = ["carol@example.com"];

    private static EventMilestone[] Reached(EventState state, bool? everyoneResponded = false, IReadOnlyList<string>? rulesOut = null) =>
        EventMilestones.Reached(state, everyoneResponded, rulesOut ?? (everyoneResponded is null ? null : [])).ToArray();

    [TestMethod]
    public void AnOpenEventWithNothingReachedHasNoMilestones()
    {
        Assert.IsEmpty(Reached(EventState.Open));
    }

    [TestMethod]
    public void TheOrganizerSeesEveryoneRespondedAndRulesOutEveryTime()
    {
        CollectionAssert.AreEqual(new[] { EventMilestone.EveryoneResponded }, Reached(EventState.Open, everyoneResponded: true));
        CollectionAssert.AreEqual(new[] { EventMilestone.RulesOutEveryTime }, Reached(EventState.Open, rulesOut: Carol));
        CollectionAssert.AreEqual(new[] { EventMilestone.EveryoneResponded, EventMilestone.RulesOutEveryTime },
            Reached(EventState.Open, everyoneResponded: true, rulesOut: Carol));
    }

    [TestMethod]
    public void AParticipantSeesNoVoteMilestones()
    {
        Assert.IsEmpty(Reached(EventState.Open, everyoneResponded: null));
        CollectionAssert.AreEqual(new[] { EventMilestone.Finalized }, Reached(EventState.Finalized, everyoneResponded: null));
    }

    [TestMethod]
    public void HowAnEventEndedComesAfterTheVoteMilestonesItReached()
    {
        CollectionAssert.AreEqual(new[] { EventMilestone.EveryoneResponded, EventMilestone.Finalized },
            Reached(EventState.Finalized, everyoneResponded: true));
        CollectionAssert.AreEqual(new[] { EventMilestone.RulesOutEveryTime, EventMilestone.Cancelled },
            Reached(EventState.Cancelled, rulesOut: Carol));
        CollectionAssert.AreEqual(new[] { EventMilestone.Expired }, Reached(EventState.Expired));
    }
}
