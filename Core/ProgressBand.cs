using System;

namespace Mp4Merger.Core
{
    /// A slice of the progress bar that one step of a run owns.
    ///
    /// A run is made of steps that each report their own 0-100%: stabilizing a
    /// clip, re-encoding it, upscaling it. Mapping every step onto the whole bar
    /// sends it backwards when the next step starts over at 0, which is exactly
    /// what used to happen after stabilization in both merge and individual mode
    /// (see CLAUDE.md). Handing each step a band instead keeps the bar moving one
    /// way.
    ///
    /// How a run is cut into bands lives here too, not in the engine, so the
    /// shares are named rather than written into the call and the whole mapping
    /// can be checked without running ffmpeg.
    public readonly record struct ProgressBand(double Start, double Width)
    {
        /// Share of one file's band that stabilizing it takes: it is a full
        /// re-encode of that file, then the conversion re-encodes it again.
        public const double StabilizeShareOfFile = 0.5;

        /// Share of the merge bar that stabilizing takes: every input is
        /// re-encoded before the merge itself begins.
        public const double StabilizeShareOfMerge = 0.4;

        /// Share of a conversion that the filter pass before an AI upscale takes.
        public const double FilterShareOfAiUpscale = 0.3;

        /// Share of an AI merge that the plain merge before the upscale takes.
        public const double MergeShareOfAiMerge = 0.4;

        /// The whole bar.
        public static ProgressBand Whole => new(0, 100);

        /// The band covering file `index` of `count`, each getting an equal share.
        public static ProgressBand ForFile(int index, int count) =>
            count <= 0 ? Whole : new ProgressBand(index * (100.0 / count), 100.0 / count);

        /// The steps of one file in individual mode. Without stabilization the
        /// conversion gets the file's whole band, and the stabilize band is empty
        /// rather than absent, so the caller needs no second code path.
        public static (ProgressBand Stabilize, ProgressBand Convert) ForIndividualFile(
            int index, int count, bool stabilize)
        {
            var band = ForFile(index, count);
            return stabilize
                ? (band.Slice(0, StabilizeShareOfFile), band.Slice(StabilizeShareOfFile, 1))
                : (band.Slice(0, 0), band);
        }

        /// The two halves of a merge: stabilizing every input, then the merge.
        public static (ProgressBand Stabilize, ProgressBand Merge) ForMerge(bool stabilize) =>
            stabilize
                ? (Whole.Slice(0, StabilizeShareOfMerge), Whole.Slice(StabilizeShareOfMerge, 1))
                : (Whole.Slice(0, 0), Whole);

        /// An AI merge: merge at the original size, then hand the one file to the
        /// upscaler.
        public static (ProgressBand Merge, ProgressBand Upscale) ForAiMerge() =>
            (Whole.Slice(0, MergeShareOfAiMerge), Whole.Slice(MergeShareOfAiMerge, 1));

        /// An AI conversion inside this band: the filter pass, then the upscale.
        public (ProgressBand Filters, ProgressBand Upscale) SplitForAiUpscale() =>
            (Slice(0, FilterShareOfAiUpscale), Slice(FilterShareOfAiUpscale, 1));

        /// The part of this band that file `index` of `count` gets, for a step
        /// that runs once per file inside a larger phase.
        public ProgressBand ForFileWithin(int index, int count) =>
            count <= 0 ? this : Slice((double)index / count, (double)(index + 1) / count);

        /// Where to put the bar when this step reports `percent` of its own work.
        public double At(double percent) => Start + Math.Clamp(percent, 0, 100) / 100.0 * Width;

        /// The part of this band between two fractions of it.
        /// `Slice(0.3, 1)` is the last 70% of this band.
        public ProgressBand Slice(double from, double to) =>
            new(Start + Width * from, Width * (to - from));

        /// Where this band ends.
        public double End => Start + Width;
    }
}
