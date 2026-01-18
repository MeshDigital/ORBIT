namespace SLSKDONET.Data;

public enum IntegrityLevel
{
    None = 0,
    Verified = 1,   // Hash matches known good
    Suspicious = 2, // Bitrate mismatch or Spectral analysis failed
    Gold = 3        // Perfect Match (Duration + BPM + Key + Audio Hash)
}
