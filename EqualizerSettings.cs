namespace Pickles_Playlist_Editor
{
    internal sealed class EqualizerSettings
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

        private static string BuildBand(int frequency, float gain)
        {
            return $"equalizer=f={frequency}:t=q:w=1.0:g={gain:0.0}";
        }
    }
}
