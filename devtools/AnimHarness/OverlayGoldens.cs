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
            string error = string.Empty;
            if (!WithoutOverlay(() => ApplyFrames(mod, 12, out error))) throw new InvalidOperationException("Pickles apply: " + error);
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

    /// <summary>ShellPins as of 0bb7b97: the window part and every probe output that passed in game.</summary>
    private static readonly Dictionary<string, string> ShellGoldens = new()
    {
        ["small plan model"] = "AE80CE82D14D02D8D1FA3EB8942FEE1DB2462ECFCD4A67B8AB802E362ABF6990",
        ["pickles single-triangle inside models"] = "8DFD2DAD939F22C854D0713253434AD52624A3B05B601D4ABF97904924CCD590",
        ["synthetic outside"] = "9F1A84A8839E312590E28D61673173C2C5668FEABCFA4094071FC8B61102CFE5",
        ["synthetic inside"] = "6EA1716B787D768F2BA0A49E97CD5F424CF801AD9C84A4A809C37DD017CF62DC",
        ["synthetic crossX1"] = "E779C7D25E671DCFAF023C29B4BD4B82E9E4E7645F086E097336553978473FED",
        ["synthetic enclose"] = "90AED44AF0C4317D0F68A1601722D04B68CBEDC90BF4CDE525AA8289A4E9D8E8",
        ["synthetic sliverCentre"] = "9F1A84A8839E312590E28D61673173C2C5668FEABCFA4094071FC8B61102CFE5",
        ["synthetic cornerVertex"] = "9F1A84A8839E312590E28D61673173C2C5668FEABCFA4094071FC8B61102CFE5",
        ["synthetic onX0"] = "B3F3E968DBFB8C6CCD39C894208D56B200582C4BE741C12103CE88D6ACD99552",
        ["synthetic cornerCover"] = "AD8ADE4296B6471F1E7DF9DD26C093715C0260C4D7BA4FD69D7DE536D66701C2",
        // Added after the split; its inside goes through the unchanged centre clip.
        ["synthetic edgeOnX0"] = "753D2A4AAF1EFEACD11C974698DA33C8E4D236074DA000FDB0AD6BBA1BB89361",
        ["probe 0 avfx"] = "3DD78858252CA92F7CDE504C04CD2FC9FBC3DE11BE6E7B9966F115E92ABA0096",
        ["probe 1 avfx"] = "E6F64C49724B2D026243FD9EF99D81CC0E5592DF5DD28486A841B3442F1B7594",
        ["probe 3 avfx"] = "FEFBEBD50CD2F7C2CC640D7CB55A60D9C1C17AA841479F61835CB11CD6FC8AC7",
        ["probe frame vfx/atex/ppeprobe1_00.atex"] = "942492E12FEC3084BCF15EE9A07C0D0F84B41F6BD901EAD9041C8F00C3605B77",
        ["probe frame vfx/atex/ppeprobe1_01.atex"] = "31184F9FBCDEC5E206FFEFA2F5B84427EF08A74492DFDF24AAD9CE383B879D2D",
        ["probe frame vfx/atex/ppeprobe1_02.atex"] = "41E0B73BF43D52ABDC2D0E2D2B7C4C506B11797906AC0EE1504001A7E4EDF03F",
        ["probe frame vfx/atex/ppeprobe1_03.atex"] = "6DE44A3B45CFF08884A089478BADCB1E86021BA4F59DAE455841E37F9706B66B",
        ["probe frame vfx/atex/ppeprobe1_04.atex"] = "597EB6D58FF0B52B6E7526F175C538B73759D3106E6DDD918CA4993553C91A43",
        ["probe frame vfx/atex/ppeprobe1_05.atex"] = "4A5582FFB3220C9DC21BD8DAA93FDF39E288A43697E3619B2DF5AE3FF63EBDA5",
        ["probe frame vfx/atex/ppeprobe1_06.atex"] = "0D5FB0201DEE71B58A18941A6D75F48DA1453452B3119032B9ED15957530D921",
        ["probe frame vfx/atex/ppeprobe1_07.atex"] = "78378563600F812A3C791ECBE1CCA6162247E6E07801AD67138C718D4FD015E9",
        ["probe frame vfx/atex/ppeprobe1_08.atex"] = "F17415100D0532C326425972C1FB6A1A3AB53EB6098E57228CEC80CB6E1877BE",
        ["probe frame vfx/atex/ppeprobe1_09.atex"] = "FA3B5E24FF718EA9A9478BE65EC34E5572C441635261671BE9353201EDA66574",
        ["probe frame vfx/atex/ppeprobe2_00.atex"] = "C061E90BF8F4B79545AB7F6AB02773064AB3C51D382A6F2757FF843B6C0C1A97",
        ["probe frame vfx/atex/ppeprobe2_01.atex"] = "2F687A7091DD53DA5CE5DE1E7487846761515E53DF35E9D97A921B2AFFAAAF46",
        ["probe frame vfx/atex/ppeprobe2_02.atex"] = "7319724DBF55260E138016116AFCAD98A942297D735D2BC75F398A782B578E82",
        ["probe frame vfx/atex/ppeprobe2_03.atex"] = "47B17FBE6F80DBFB73EF3FE35656D43ED3DD0F0042958884B60E7033A8A601E1",
        ["probe frame vfx/atex/ppeprobe2_04.atex"] = "030BD4FC158F463110EA1D3A9130CC647A45886370B21C577843B251F300B013",
        ["probe frame vfx/atex/ppeprobe2_05.atex"] = "856DB8309C307CC08E468DB1C1796109F436F41D8FA93500089741067F1FE675",
        ["probe frame vfx/atex/ppeprobe2_06.atex"] = "DBAD966190E4580BFA0DF9038EA418CF37FDAD4D67954B7699DF66FFF9EA192F",
        ["probe frame vfx/atex/ppeprobe2_07.atex"] = "F2019F34B92476E85FDC885C2BCA1F241F6082661DA07BAD899B0FE1783F3FBA",
        ["probe frame vfx/atex/ppeprobe2_08.atex"] = "66D213652E889796AAF0C9B759E254EBE8299FC81F5322E45DFC23976969B3D6",
        ["probe frame vfx/atex/ppeprobe2_09.atex"] = "E39CB9A07111A230447D8503B98EFB4060D2771CC040A96B9951FA85AE1C7CB0",
        ["probe frame vfx/atex/ppeprobe3_00.atex"] = "99F91A842DA3F6877B4C52086D1F70FBF282675B60BE9C412BB7F1A85E104477",
        ["probe frame vfx/atex/ppeprobe4_00.atex"] = "721928FE146FBB04D86B716FA0B351A72C505B435A1B7C477C6D6C3A19D0ECF2",
    };
}
