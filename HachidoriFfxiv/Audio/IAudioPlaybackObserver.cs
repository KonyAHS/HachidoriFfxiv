namespace HachidoriFfxiv.Audio;

/// <summary>Notified when pronunciation playback starts and finishes, so audio capture can ignore plugin output.</summary>
public interface IAudioPlaybackObserver
{
    long BeginPlayback();

    void EndPlayback(long handle);
}
