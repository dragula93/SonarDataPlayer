namespace SonarDataPlayer.Core;

public sealed class PlaybackState
{
    public bool IsPlaying { get; private set; }

    public double CurrentTimeSeconds { get; private set; }

    public double PlaybackRate { get; private set; } = 1.0;

    public void Play() => IsPlaying = true;

    public void Pause() => IsPlaying = false;

    public void Toggle()
    {
        IsPlaying = !IsPlaying;
    }

    public void SetRate(double rate)
    {
        PlaybackRate = Math.Clamp(rate, 0.05, 16.0);
    }

    public void Seek(double timeSeconds, double durationSeconds)
    {
        CurrentTimeSeconds = Math.Clamp(timeSeconds, 0, Math.Max(0, durationSeconds));
    }

    public void Advance(TimeSpan elapsed, double durationSeconds, double? overrideRate = null)
    {
        if (!IsPlaying)
        {
            return;
        }

        var rate = overrideRate ?? PlaybackRate;
        CurrentTimeSeconds += elapsed.TotalSeconds * rate;
        if (CurrentTimeSeconds >= durationSeconds)
        {
            CurrentTimeSeconds = Math.Max(0, durationSeconds);
            IsPlaying = false;
        }
    }
}
