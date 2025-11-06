using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VfxEditor.Formats.ScdFormat.Utils;

namespace VfxEditor.Select {
    public partial class SelectDataUtils {
        public static string BnpcPath => Path.Combine( Plugin.RootLocation, "Files", "bnpc.json" );
        public static string NpcFilesPath => Path.Combine( Plugin.RootLocation, "Files", "npc_files.json" );
        public static string CommonVfxPath => Path.Combine( Plugin.RootLocation, "Files", "common_vfx" );
        public static string CommonTmbPath => Path.Combine( Plugin.RootLocation, "Files", "common_tmb" );
        public static string CommonUldPath => Path.Combine( Plugin.RootLocation, "Files", "common_uld" );
        public static string CommonShpkPath => Path.Combine( Plugin.RootLocation, "Files", "common_shpk" );
        public static string CommonShcdPath => Path.Combine( Plugin.RootLocation, "Files", "common_shcd" );
        public static string CommonRacialPath => Path.Combine( Plugin.RootLocation, "Files", "common_racial" );

        [GeneratedRegex( "\\u0000([a-zA-Z0-9\\/_]*?)\\.avfx", RegexOptions.Compiled )]
        private static partial Regex AvfxRegexPattern();
        public static readonly Regex AvfxRegex = AvfxRegexPattern();

        [GeneratedRegex( "\\u0000([a-zA-Z0-9\\/_]*?)\\.scd", RegexOptions.Compiled )]
        private static partial Regex ScdRegexPattern();
        public static readonly Regex ScdRegex = ScdRegexPattern();

        // https://github.com/imchillin/CMTool/blob/master/ConceptMatrix/Views/SpecialControl.xaml.cs#L365

   

        public static readonly Dictionary<string, string> JobAnimationIds = new() {
            { "Warrior", "bt_2ax_emp" },
            { "Paladin", "bt_swd_sld" },
            { "Gunbreaker", "bt_2gb_emp" },
            { "Dark Knight", "bt_2sw_emp" },
            { "Astrologian", "bt_2gl_emp" },
            { "Sage", "bt_2ff_emp" },
            { "Scholar", "bt_2bk_emp" },
            { "White Mage", "bt_stf_sld" },
            { "Machinist", "bt_2gn_emp" },
            { "Dancer", "bt_chk_chk" },
            { "Bard", "bt_2bw_emp" },
            { "Samurai", "bt_2kt_emp" },
            { "Dragoon", "bt_2sp_emp" },
            { "Monk", "bt_clw_clw" },
            { "Ninja", "bt_dgr_dgr" },
            { "Reaper", "bt_2km_emp" },
            { "Red Mage", "bt_2rp_emp" },
            { "Black Mage", "bt_jst_sld" },
            { "Summoner", "bt_2bk_emp" },
            { "Blue Mage", "bt_rod_emp" },
            { "Pictomancer", "bt_brs_plt" },
            { "Viper", "bt_bld_bld" },
        };

        public static readonly Dictionary<string, string> JobMovementOverride = new() {
            { "Black Mage", "bt_stf_sld" },
            { "Ninja", "bt_nin_nin" },
        };

        public static readonly Dictionary<string, string> JobDrawOverride = new() {
            { "Black Mage", "bt_stf_sld" }
        };

        public static readonly Dictionary<string, string> JobAutoOverride = new() {
            { "Black Mage", "bt_stf_sld" }
        };

        public static readonly int MaxChangePoses = 6;

        public static Dictionary<string, string> FileExistsFilter( Dictionary<string, string> dict ) => dict.Where( x => File.Exists( x.Value ) ).ToDictionary( x => x.Key, x => x.Value );

        public static string GetSkeletonPath( string skeletonId, string path ) => $"chara/human/{skeletonId}/animation/a0001/{path}";

        
    }
}
