namespace GlyphLabs.ResonanceSync
{
    /// <summary>
    /// Determines which analysis strategy the processor uses to generate
    /// LipSyncData from an AudioClip.
    ///
    /// This enum is the single place where supported input modes are defined.
    /// Both the Editor window (for the mode dropdown) and the Processor
    /// (for strategy selection) reference this — never a raw int or string.
    /// </summary>
    public enum ProcessingMode
    {
        /// <summary>
        /// Amplitude + FFT frequency band analysis.
        /// Fast, no external dependencies, works on any audio.
        /// Good quality for casual dialogue and background characters.
        /// </summary>
        Signal = 0,

        /// <summary>
        /// Phoneme extraction via the Rhubarb Lip Sync CLI.
        /// More accurate mouth shapes, especially for principal characters.
        /// Requires Rhubarb binaries in ThirdParty/Rhubarb/.
        /// Editor-only — no runtime dependency.
        /// </summary>
        Rhubarb = 1
    }
}