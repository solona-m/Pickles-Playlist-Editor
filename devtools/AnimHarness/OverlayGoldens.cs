using Pickles_Playlist_Editor.Utils.Avfx;

namespace AnimHarness;

/// <summary>The full-atlas outputs pinned before the overlay code existed (Utils as of 742b023).</summary>
internal static partial class Program
{
    /// <summary>SHA-256 of today's Apply output: four Pickles effects and 12 frames, two Moon cats at 48.</summary>
    private static Dictionary<string, string> CurrentGoldens()
    {
        var result = new Dictionary<string, string>();
        using (var mod = Fixtures.Pickles())
        {
            if (!ApplyFrames(mod, 12, out string error)) throw new InvalidOperationException("Pickles apply: " + error);
            foreach (var rel in Fixtures.PicklesAvfx) result["pickles " + rel] = AvfxTableAnimation.Sha(mod.Read(rel));
            for (int k = 0; k < 12; k++)
            {
                string rel = $@"ppe-table-animation\t0000001_1_{k:D2}.atex";
                result["pickles " + rel] = AvfxTableAnimation.Sha(mod.Read(rel));
            }
        }
        using (var mod = Fixtures.Moon())
        {
            if (!ApplyFrames(mod, 48, out string error)) throw new InvalidOperationException("Moon apply: " + error);
            foreach (var rel in new[] { Fixtures.MoonCat, Fixtures.MoonCat2 }) result["moon " + rel] = AvfxTableAnimation.Sha(mod.Read(rel));
        }
        return result;
    }

    private static readonly Dictionary<string, string> Goldens = new()
    {
        [@"pickles common\59\laladjtable.avfx"] = "841DC82894227C12EA94D9A9589ECF5D8884CC955D6815605766DA6DA3E962CD",
        [@"pickles common\59\alinasexy.avfx"] = "2CCA0AB2F6B19726156AB1E89C23E789811337881A8A6CE4EE1578F0A740CACB",
        [@"pickles common\59\medium.avfx"] = "6ABE0E05D358123F9D7DC936DB8F0CE579CA024930A853D9C122B30A3F0FDD47",
        [@"pickles common\59\tall.avfx"] = "D756A947811AA46F8366C463381EEBD5A4B4F6215EC3B443FD4749081AE7C1AC",
        [@"pickles ppe-table-animation\t0000001_1_00.atex"] = "E11600E15EC79FE826F923200F7E56EC086385AD3144DF58471B600A267A908D",
        [@"pickles ppe-table-animation\t0000001_1_01.atex"] = "8764805CF4D021618B32260CA369F0179D92A01E24813BB0332B991FD4B10D14",
        [@"pickles ppe-table-animation\t0000001_1_02.atex"] = "722460D3CEDED72FE7D88FEDFF7ED13E43CD6481DB8D0D9AA5EA23965F6C9C5A",
        [@"pickles ppe-table-animation\t0000001_1_03.atex"] = "E12A18BF28E33395901C80282908F683E62E3BF027298581BD1CA21E32F13564",
        [@"pickles ppe-table-animation\t0000001_1_04.atex"] = "3A94C194B4FD81E337BC599B8788251BF7005E27AAB9D57775421AB77E99B49E",
        [@"pickles ppe-table-animation\t0000001_1_05.atex"] = "B20EF37AE857A6CB9609CF9C444B467D21A4EF07D3FA0910B3F8E4F0C538EF8A",
        [@"pickles ppe-table-animation\t0000001_1_06.atex"] = "32878565E41AFD4BAFAF4BE9D612E2091CE1E48E36F3A60EC14EC2CFCA5E81D7",
        [@"pickles ppe-table-animation\t0000001_1_07.atex"] = "B1D2386AD26DDD501E51029A8003CC87917C8F34B2598025585CFE537CEEEA35",
        [@"pickles ppe-table-animation\t0000001_1_08.atex"] = "9D4C43674F18FC4D0D1EAF508B90D5EDE7BE2137BF7B9810E5887D890A2E537A",
        [@"pickles ppe-table-animation\t0000001_1_09.atex"] = "C367DBEBC493CAFEF76EADE1969FAD06EDFC62E0BCD970C8AF9AC1ED599860D4",
        [@"pickles ppe-table-animation\t0000001_1_10.atex"] = "82DF28CE4C3EA021880D82B6A49AC2349B276EFAE259C9B51FE26B3C78EA77DE",
        [@"pickles ppe-table-animation\t0000001_1_11.atex"] = "5903C7301CC2ADA7E0FE520AD064C4E5592E5D76888F55DFD220804F77D02869",
        [@"moon common\59\alinasexy.avfx"] = "0148582C9639FF63D11E05F6A460D6EA5387F9EF3C2D408B0817158D5012D268",
        [@"moon common\67\alinasexy2.avfx"] = "0148582C9639FF63D11E05F6A460D6EA5387F9EF3C2D408B0817158D5012D268",
    };
}
