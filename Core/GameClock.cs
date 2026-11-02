namespace BlockCircuit;

/// <summary>Accumulates wall time into bounded, deterministic simulation steps.</summary>
public sealed class GameClock
{
    public const double MaximumFrameSeconds = .25;
    readonly double stepSeconds;
    readonly int maxCatchUpSteps;
    double accumulator;

    public GameClock(int tickRate = 60, int maxCatchUpSteps = 8)
    {
        if (tickRate <= 0) throw new ArgumentOutOfRangeException(nameof(tickRate));
        if (maxCatchUpSteps <= 0) throw new ArgumentOutOfRangeException(nameof(maxCatchUpSteps));
        stepSeconds = 1d / tickRate;
        StepSeconds = (float)stepSeconds;
        this.maxCatchUpSteps = maxCatchUpSteps;
    }

    public float StepSeconds { get; }
    public long TotalTicks { get; private set; }
    public int StepsThisFrame { get; private set; }
    public double DroppedSeconds { get; private set; }
    public float Alpha => Math.Min(MathF.BitDecrement(1f), (float)Math.Max(0, accumulator / stepSeconds));

    public int Advance(double elapsedSeconds, Action<float> step)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        ArgumentNullException.ThrowIfNull(step);
        StepsThisFrame = 0;
        double accepted = Math.Min(elapsedSeconds, MaximumFrameSeconds);
        RecordDropped(elapsedSeconds - accepted);
        accumulator += accepted;
        // Frame partitions such as 1/144 can end a few ulps below a tick boundary.
        // A relative tolerance preserves the intended integer tick count.
        double tolerance = stepSeconds * 1e-9;
        while (StepsThisFrame < maxCatchUpSteps && accumulator + tolerance >= stepSeconds)
        {
            accumulator = Math.Max(0, accumulator - stepSeconds);
            step(StepSeconds);
            StepsThisFrame++;
            TotalTicks++;
        }
        if (accumulator + tolerance >= stepSeconds)
        {
            double discarded = Math.Floor((accumulator + tolerance) / stepSeconds) * stepSeconds;
            // Retain only the fractional tick for interpolation after a stalled frame.
            RecordDropped(Math.Min(accumulator, discarded));
            accumulator = Math.Max(0, accumulator - discarded);
        }
        if (accumulator < tolerance) accumulator = 0;
        return StepsThisFrame;
    }

    public void ResetAccumulator()
    {
        accumulator = 0;
        StepsThisFrame = 0;
    }

    void RecordDropped(double seconds) => DroppedSeconds =
        Math.Min(double.MaxValue, DroppedSeconds + seconds);
}
