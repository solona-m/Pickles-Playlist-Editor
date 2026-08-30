namespace Pickles_Playlist_Editor
{
    public sealed class EqualizerSettings
    {
        public float BassGain { get; set; }
        public float LowMidGain { get; set; }
        public float MidGain { get; set; }
        public float HighMidGain { get; set; }
        public float TrebleGain { get; set; }

        public string ToFilterChain()
        {
            return string.Join(",",
                BuildBand(64, BassGain),
                BuildBand(250, LowMidGain),
                BuildBand(1000, MidGain),
                BuildBand(4000, HighMidGain),
                BuildBand(12000, TrebleGain));
        }

        /// <summary>
        /// One <c>equalizer</c> filter, formatted for ffmpeg rather than for a human.
        ///
        /// The gain MUST be written with an invariant decimal point. Interpolating it with the
        /// current culture emits "g=3,0" wherever the regional format uses a comma — German, French,
        /// Spanish, Dutch, Swedish, most of Europe and Latin America — and a comma is what separates
        /// filters in ffmpeg's -af syntax. ffmpeg then reads the band as ending at "g=3" and goes
        /// looking for a filter named "0":
        ///
        ///     [AVFilterGraph] No such filter: '0'
        ///     Error opening output files: Filter not found
        ///
        /// Every band and every value is affected, 0.0 included, so the equalizer failed outright for
        /// those users rather than sounding wrong. <see cref="Utils.FFMpeg.NormalizeVolume"/> pins the
        /// invariant culture for exactly this reason; this is the one place that did not.
        /// </summary>
        private static string BuildBand(int frequency, float gain)
        {
            return FormattableString.Invariant($"equalizer=f={frequency}:t=q:w=1.0:g={gain:0.0}");
        }
    }
}
