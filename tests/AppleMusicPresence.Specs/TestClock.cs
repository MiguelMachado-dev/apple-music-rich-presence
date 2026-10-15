namespace AppleMusicPresence.Specs;

internal sealed class TestClock : TimeProvider
{
    internal DateTimeOffset Now = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}
