namespace HachidoriFfxiv.Media;

/// <summary>A screenshot ready for Hachidori's linked client-media envelope.</summary>
public sealed record ScreenshotMedia(string Token, string Filename, string Base64);
