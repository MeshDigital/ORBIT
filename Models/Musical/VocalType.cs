namespace SLSKDONET.Models
{
    /// <summary>
    /// Classification of vocal content in a track.
    /// Helps DJs plan transitions and avoid vocal clashes.
    /// </summary>
    public enum VocalType
    {
        /// <summary> No vocals detected. </summary>
        Instrumental = 0,

        /// <summary> Occasional words, hype phrases, or shouts. </summary>
        SparseVocals = 1,

        /// <summary> Repeating hook or chorus, but lacks full song structure. </summary>
        HookOnly = 2,

        /// <summary> Standard song structure with verses and chorus. </summary>
        FullLyrics = 3
    }
}
