using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Ryujinx.Ava.Common.Locale;

namespace Ryujinx.Ava.UI.Models
{
    // [Nextendo] One mod from GameBanana shown in the Mod Store grid. The store lists
    // client-side / cosmetic mods pulled live from the GameBanana API (apiv11) for the
    // selected game, with a thumbnail, and installs the mod's archive into the game's
    // local mod folder under gb_<ModId>/.
    public partial class ModStoreItem : ObservableObject
    {
        public long ModId { get; init; }         // GameBanana _idRow
        public string Name { get; init; }
        public string Category { get; init; }     // root category name (e.g. "Skins")
        public string Author { get; init; }
        public int Likes { get; init; }
        public int Views { get; init; }
        public string Version { get; init; }
        public string ThumbnailUrl { get; init; } // GameBanana preview image (220px)
        public string ProfileUrl { get; init; }   // GameBanana mod page

        [ObservableProperty] private Bitmap _thumbnail; // loaded async from ThumbnailUrl
        [ObservableProperty] private bool _installed;
        [ObservableProperty] private bool _busy;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FavoriteGlyph))]
        private bool _favorited; // synced to the Nextendo account (heart button)

        public string FavoriteGlyph => Favorited ? "♥" : "♡";

        [ObservableProperty] private bool _updateAvailable; // installed version differs from GameBanana's latest
        [ObservableProperty] private bool _enabled = true;  // Ryujinx enabled/disabled state (mods.json)
        [ObservableProperty] private bool _hasConflict;     // shares romfs files with another enabled installed mod

        public string MetaText
        {
            get
            {
                string cat = string.IsNullOrWhiteSpace(Category) ? "" : Category;
                string by = string.IsNullOrWhiteSpace(Author) ? "" : LocaleManager.GetFormatted(LocaleKeys.Dialog_Nextendo_ModStoreByAuthorFormat, Author);
                string sep = cat.Length > 0 && by.Length > 0 ? "  ·  " : "";
                return cat + sep + by;
            }
        }

        public string StatsText => $"♥ {Likes}   ·   👁 {Views}" + (string.IsNullOrWhiteSpace(Version) ? "" : $"   ·   v{Version}");
    }

    // [Nextendo] A GameBanana mod category, used for the filter chips.
    public partial class ModCategory : ObservableObject
    {
        public long Id { get; init; }            // GameBanana category _idRow (0 = "All")
        public string Name { get; init; }
        public int Count { get; init; }

        [ObservableProperty] private bool _selected;

        public string Label => Count > 0 ? $"{Name} ({Count})" : Name;
    }
}
