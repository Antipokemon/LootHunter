namespace LootHunter.Automation;

public enum FarmState
{
    Idle,
    Validating,
    Planning,
    Teleporting,
    WaitingForZone,
    Mounting,
    Navigating,
    SearchingForMob,
    EngagingMob,
    WaitingForLoot,
    Replanning,
    Paused,
    Completed,
    Error,
}
