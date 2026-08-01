using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.UI.Models;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Helper;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS;
using SharpCompress.Archives;
using SharpCompress.Common;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Ava.UI.ViewModels
{
    // [Nextendo] Mod Store backed by the live GameBanana API (apiv11). It resolves the running
    // game to its GameBanana id (by name), lists client-side / cosmetic mods for it with
    // thumbnails, supports search + category filters, opens a mod's GameBanana page for details,
    // and installs a mod's archive into the game's local mod folder under gb_<modId>/.
    public partial class ModStoreViewModel : BaseModel
    {
        private const string ApiBase = "https://gamebanana.com/apiv11";
        private const int PerPage = 15;
        private const int TargetFill = 12;   // keep auto-loading pages until at least this many mods show
        private const int MaxAutoPages = 6;  // ...but never fetch more than this many pages at once

        private static readonly HttpClient Http = CreateHttp();

        private readonly ulong _titleId;
        private readonly string _titleName;

        private int _gameId = -1;      // GameBanana game id (-1 unresolved, 0 not found)
        private int _page = 1;
        private long _categoryId;      // 0 = all categories ; special: -1 installed, -2 favorites
        private string _query = "";
        private CancellationTokenSource _cts;

        // Special "category" chips that don't hit the GameBanana listing API.
        private const long CatInstalled = -1;
        private const long CatFavorites = -2;

        // Mod ids the signed-in Nextendo account has favorited (synced across PCs).
        private readonly HashSet<long> _favoriteIds = new();

        private static string NxBase() => Ryujinx.Ava.Common.NextendoApi.BaseUrl();

        public ObservableCollection<ModStoreItem> Mods { get; } = [];
        public ObservableCollection<ModCategory> Categories { get; } = [];

        [ObservableProperty] private bool _loading;
        [ObservableProperty] private bool _canLoadMore;
        [ObservableProperty] private string _status = "";
        [ObservableProperty] private string _searchText = "";
        [ObservableProperty] private bool _cosmeticOnly = true;

        private string ModsContentsDir => Path.Combine(ModLoader.GetModsBasePath(), "contents", _titleId.ToString("x16"));

        public ModStoreViewModel(ulong titleId, string titleName)
        {
            _titleId = titleId;
            _titleName = titleName ?? "";
            _ = InitAsync();
        }

        private static HttpClient CreateHttp()
        {
            HttpClient http = new() { Timeout = TimeSpan.FromSeconds(25) };
            http.DefaultRequestHeaders.Add("User-Agent", "Ryujinx-Nextendo-ModStore");
            http.DefaultRequestHeaders.Add("Accept", "application/json");
            return http;
        }

        private static string L(LocaleKeys key) => LocaleManager.Instance[key];
        private static string LF(LocaleKeys key, params object[] args) => LocaleManager.GetFormatted(key, args);

        // ---- lifecycle -----------------------------------------------------------------

        private async Task InitAsync()
        {
            Loading = true;
            Status = L(LocaleKeys.Dialog_Nextendo_ModStoreResolvingGame);
            try
            {
                _gameId = await ResolveGameIdAsync(_titleName);
                if (_gameId <= 0)
                {
                    Status = LF(LocaleKeys.Dialog_Nextendo_ModStoreGameNotFoundFormat, _titleName);
                    return;
                }

                await LoadFavoriteIdsAsync();
                await LoadCategoriesAsync();
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"[Nextendo] mod store init failed: {ex.Message}");
                Status = L(LocaleKeys.Dialog_Nextendo_ModStoreLoadError);
            }
            finally
            {
                Loading = false;
            }
        }

        private async Task<int> ResolveGameIdAsync(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return 0;
            }

            using JsonDocument doc = await GetJsonAsync($"{ApiBase}/Util/Game/NameMatch?_sName={Uri.EscapeDataString(name)}");
            if (doc == null || !doc.RootElement.TryGetProperty("_aRecords", out JsonElement recs) || recs.ValueKind != JsonValueKind.Array)
            {
                return 0;
            }

            int firstId = 0;
            foreach (JsonElement r in recs.EnumerateArray())
            {
                int id = (int)GetLong(r, "_idRow");
                if (id == 0)
                {
                    continue;
                }

                if (firstId == 0)
                {
                    firstId = id;
                }

                if (GetStr(r, "_sName").Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return id;
                }
            }

            return firstId;
        }

        // ---- categories (filter chips) -------------------------------------------------

        private async Task LoadCategoriesAsync()
        {
            Categories.Clear();
            Categories.Add(new ModCategory { Id = 0, Name = L(LocaleKeys.Dialog_Nextendo_ModStoreCategoryAll), Selected = _categoryId == 0 });
            Categories.Add(new ModCategory { Id = CatInstalled, Name = L(LocaleKeys.Dialog_Nextendo_ModStoreInstalled), Count = InstalledCount(), Selected = _categoryId == CatInstalled });
            if (!string.IsNullOrEmpty(NextendoAccount.NexToken))
            {
                Categories.Add(new ModCategory { Id = CatFavorites, Name = L(LocaleKeys.Dialog_Nextendo_ModStoreFavorites), Count = _favoriteIds.Count, Selected = _categoryId == CatFavorites });
            }

            using JsonDocument doc = await GetJsonAsync($"{ApiBase}/Mod/Categories?_idGameRow={_gameId}&_sSort=a_to_z&_nPage=1");
            if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (JsonElement c in doc.RootElement.EnumerateArray())
            {
                string cname = GetStr(c, "_sName");
                long cid = GetLong(c, "_idRow");
                int count = (int)GetLong(c, "_nItemCount");
                bool obsolete = c.TryGetProperty("_bIsObsolete", out JsonElement o) && o.ValueKind == JsonValueKind.True;

                if (obsolete || cid == 0 || count <= 0 || string.IsNullOrWhiteSpace(cname))
                {
                    continue;
                }

                if (cname.StartsWith("(Don't upload", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (CosmeticOnly && !IsCosmeticCategory(cname))
                {
                    continue;
                }

                Categories.Add(new ModCategory { Id = cid, Name = cname, Count = count, Selected = _categoryId == cid });
            }
        }

        // ---- listing (with auto-fill so the cosmetic filter never leaves a near-empty grid) ----

        public async Task ReloadAsync()
        {
            _cts?.Cancel();
            CancellationTokenSource cts = _cts = new CancellationTokenSource();
            _page = 1;
            Mods.Clear();
            Loading = true;
            Status = L(LocaleKeys.Dialog_Nextendo_ModStoreLoading);

            try
            {
                if (_categoryId == CatInstalled)
                {
                    LoadInstalled();
                    if (!cts.IsCancellationRequested)
                    {
                        UpdateStatus();
                    }

                    _ = CheckUpdatesForInstalledAsync(cts.Token);
                }
                else if (_categoryId == CatFavorites)
                {
                    await LoadFavoritesAsync(cts.Token); // owns its own status message
                }
                else
                {
                    int pages = 0;
                    do
                    {
                        await FetchOnePageAsync(cts.Token);
                        pages++;
                        _page++;
                    }
                    while (!cts.IsCancellationRequested && CanLoadMore && Mods.Count < TargetFill && pages < MaxAutoPages);

                    if (!cts.IsCancellationRequested)
                    {
                        UpdateStatus();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // superseded by a newer query
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"[Nextendo] mod store load failed: {ex.Message}");
                Status = L(LocaleKeys.Dialog_Nextendo_ModStoreLoadError);
            }
            finally
            {
                if (_cts == cts)
                {
                    Loading = false;
                }
            }
        }

        public async Task LoadMoreAsync()
        {
            CancellationTokenSource cts = _cts;
            if (cts == null || cts.IsCancellationRequested)
            {
                return;
            }

            Loading = true;
            try
            {
                await FetchOnePageAsync(cts.Token);
                _page++;
                UpdateStatus();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"[Nextendo] mod store load-more failed: {ex.Message}");
            }
            finally
            {
                if (_cts == cts)
                {
                    Loading = false;
                }
            }
        }

        private async Task FetchOnePageAsync(CancellationToken ct)
        {
            if (_gameId <= 0)
            {
                CanLoadMore = false;
                return;
            }

            string url;
            if (!string.IsNullOrWhiteSpace(_query))
            {
                url = $"{ApiBase}/Util/Search/Results?_sSearchString={Uri.EscapeDataString(_query)}&_idGameRow={_gameId}&_sModelName=Mod&_nPage={_page}";
            }
            else if (_categoryId != 0)
            {
                url = $"{ApiBase}/Mod/Index?_nPage={_page}&_nPerpage={PerPage}&_aFilters%5BGeneric_Game%5D={_gameId}&_aFilters%5BGeneric_Category%5D={_categoryId}";
            }
            else
            {
                url = $"{ApiBase}/Game/{_gameId}/Subfeed?_nPage={_page}&_sSort=default";
            }

            using JsonDocument doc = await GetJsonAsync(url, ct);
            if (doc == null || ct.IsCancellationRequested)
            {
                CanLoadMore = false;
                return;
            }

            JsonElement root = doc.RootElement;
            JsonElement records = root.TryGetProperty("_aRecords", out JsonElement rr) ? rr : root;
            int onPage = 0;

            if (records.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement r in records.EnumerateArray())
                {
                    onPage++;

                    string model = GetStr(r, "_sModelName");
                    if (model.Length > 0 && model != "Mod")
                    {
                        continue;
                    }

                    if (r.TryGetProperty("_bHasFiles", out JsonElement hf) && hf.ValueKind == JsonValueKind.False)
                    {
                        continue;
                    }

                    string cat = "";
                    if (r.TryGetProperty("_aRootCategory", out JsonElement rc) && rc.ValueKind == JsonValueKind.Object)
                    {
                        cat = GetStr(rc, "_sName");
                    }

                    // Client-side filter: only drop when we KNOW the category and it isn't cosmetic
                    // (so search results with no category still show through).
                    if (CosmeticOnly && _categoryId == 0 && cat.Length > 0 && !IsCosmeticCategory(cat))
                    {
                        continue;
                    }

                    ModStoreItem item = BuildItem(r, cat);
                    if (item == null)
                    {
                        continue;
                    }

                    item.Installed = InstalledDirFor(item.ModId) != null;
                    item.Favorited = _favoriteIds.Contains(item.ModId);
                    if (item.Installed && !string.IsNullOrEmpty(item.Version))
                    {
                        string iv = InstalledVersion(item.ModId);
                        item.UpdateAvailable = !string.IsNullOrEmpty(iv) && !string.Equals(iv, item.Version, StringComparison.OrdinalIgnoreCase);
                    }

                    Mods.Add(item);
                    _ = LoadThumbnailAsync(item, ct);
                }
            }

            bool complete = false;
            if (root.TryGetProperty("_aMetadata", out JsonElement md) && md.TryGetProperty("_bIsComplete", out JsonElement comp))
            {
                complete = comp.ValueKind == JsonValueKind.True;
            }

            CanLoadMore = onPage > 0 && !complete;
        }

        private void UpdateStatus()
        {
            if (Mods.Count > 0)
            {
                Status = LF(LocaleKeys.Dialog_Nextendo_ModStoreModsAvailableFormat, Mods.Count);
                return;
            }

            // Only the default "All" browse hides mods via the cosmetic filter; installed / a
            // specific category / favorites simply have nothing to show.
            Status = (_categoryId == 0 && CosmeticOnly)
                ? L(LocaleKeys.Dialog_Nextendo_ModStoreNoCosmetic)
                : L(LocaleKeys.Dialog_Nextendo_ModStoreNoMods);
        }

        private static ModStoreItem BuildItem(JsonElement r, string cat)
        {
            long id = GetLong(r, "_idRow");
            if (id == 0)
            {
                return null;
            }

            string author = "";
            if (r.TryGetProperty("_aSubmitter", out JsonElement sub) && sub.ValueKind == JsonValueKind.Object)
            {
                author = GetStr(sub, "_sName");
            }

            return new ModStoreItem
            {
                ModId = id,
                Name = GetStr(r, "_sName"),
                Category = cat,
                Author = author,
                Likes = (int)GetLong(r, "_nLikeCount"),
                Views = (int)GetLong(r, "_nViewCount"),
                Version = GetStr(r, "_sVersion"),
                ProfileUrl = GetStr(r, "_sProfileUrl"),
                ThumbnailUrl = FirstThumbnailUrl(r),
            };
        }

        private static string FirstThumbnailUrl(JsonElement r)
        {
            if (r.TryGetProperty("_aPreviewMedia", out JsonElement pm) && pm.ValueKind == JsonValueKind.Object
                && pm.TryGetProperty("_aImages", out JsonElement imgs) && imgs.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement im in imgs.EnumerateArray())
                {
                    string baseUrl = GetStr(im, "_sBaseUrl");
                    string file = GetStr(im, "_sFile220");
                    if (string.IsNullOrEmpty(file))
                    {
                        file = GetStr(im, "_sFile");
                    }

                    if (!string.IsNullOrEmpty(baseUrl) && !string.IsNullOrEmpty(file))
                    {
                        return $"{baseUrl}/{file}";
                    }
                }
            }

            return "";
        }

        private async Task LoadThumbnailAsync(ModStoreItem item, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(item.ThumbnailUrl))
            {
                return;
            }

            try
            {
                byte[] bytes = await Http.GetByteArrayAsync(item.ThumbnailUrl, ct);
                if (ct.IsCancellationRequested || bytes.Length == 0)
                {
                    return;
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    try { item.Thumbnail = new Bitmap(new MemoryStream(bytes)); }
                    catch { /* not an image we can decode */ }
                });
            }
            catch
            {
                // thumbnails are best-effort
            }
        }

        // ---- search / filters / details ------------------------------------------------

        public async Task SearchAsync(string text)
        {
            _query = (text ?? "").Trim();
            if (_query.Length > 0)
            {
                _categoryId = 0;
                foreach (ModCategory c in Categories)
                {
                    c.Selected = c.Id == 0;
                }
            }

            await ReloadAsync();
        }

        public async Task SelectCategoryAsync(ModCategory cat)
        {
            if (cat == null)
            {
                return;
            }

            foreach (ModCategory c in Categories)
            {
                c.Selected = ReferenceEquals(c, cat);
            }

            _categoryId = cat.Id;
            _query = "";
            SearchText = "";
            await ReloadAsync();
        }

        partial void OnCosmeticOnlyChanged(bool value)
        {
            _ = RefreshForCosmeticAsync();
        }

        private async Task RefreshForCosmeticAsync()
        {
            if (_gameId <= 0)
            {
                return;
            }

            if (CosmeticOnly && _categoryId != 0 && !Categories.Any(c => c.Id == _categoryId && IsCosmeticCategory(c.Name)))
            {
                _categoryId = 0;
            }

            await LoadCategoriesAsync();
            await ReloadAsync();
        }

        // Opens the mod's GameBanana page in the browser for full details (all screenshots,
        // description, comments, version history).
        public void OpenModPage(ModStoreItem item)
        {
            if (item != null && !string.IsNullOrEmpty(item.ProfileUrl))
            {
                OpenHelper.OpenUrl(item.ProfileUrl);
            }
        }

        // ---- installed mods -----------------------------------------------------------

        private int InstalledCount()
        {
            try
            {
                string dir = ModsContentsDir;
                if (!Directory.Exists(dir))
                {
                    return 0;
                }

                int n = 0;
                foreach (string d in Directory.GetDirectories(dir))
                {
                    if (TryParseModId(Path.GetFileName(d), out _))
                    {
                        n++;
                    }
                }

                return n;
            }
            catch
            {
                return 0;
            }
        }

        // On-disk folder for an installed GameBanana mod: "<mod name> [gb<modId>]" so the game's Mod
        // Manager shows the readable title, while the [gb<id>] suffix keeps install/remove/detection
        // unambiguous. (Older installs used "gb_<id>", still recognized.)
        private static string ModFolderName(ModStoreItem item)
        {
            string safe = SanitizeName(item.Name);
            return string.IsNullOrEmpty(safe) ? $"gb_{item.ModId}" : $"{safe} [gb{item.ModId}]";
        }

        private static string SanitizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "";
            }

            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder sb = new();
            foreach (char c in name.Trim())
            {
                sb.Append(System.Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }

            string s = sb.ToString().Trim(' ', '.');
            return s.Length > 80 ? s[..80].Trim() : s;
        }

        // Extracts the GameBanana mod id from a folder name in either scheme.
        private static bool TryParseModId(string folderName, out long modId)
        {
            modId = 0;
            if (string.IsNullOrEmpty(folderName))
            {
                return false;
            }

            int i = folderName.LastIndexOf("[gb", StringComparison.OrdinalIgnoreCase);
            if (i >= 0)
            {
                int j = folderName.IndexOf(']', i);
                if (j > i + 3 && long.TryParse(folderName.Substring(i + 3, j - i - 3), out modId))
                {
                    return true;
                }
            }

            return folderName.StartsWith("gb_", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(folderName.Substring(3), out modId);
        }

        // The installed folder for a mod id (any naming scheme), or null if not installed.
        private string InstalledDirFor(long modId)
        {
            try
            {
                string dir = ModsContentsDir;
                if (!Directory.Exists(dir))
                {
                    return null;
                }

                foreach (string d in Directory.GetDirectories(dir))
                {
                    if (TryParseModId(Path.GetFileName(d), out long id) && id == modId)
                    {
                        return d;
                    }
                }
            }
            catch
            {
                // ignore
            }

            return null;
        }

        // Lists the GameBanana mods currently installed for this game (gb_<modId> folders),
        // reading the little .nxmeta.json we drop at install time so we can show name + thumbnail.
        private void LoadInstalled()
        {
            CanLoadMore = false;
            CancellationTokenSource cts = _cts;
            string dir = ModsContentsDir;
            if (!Directory.Exists(dir))
            {
                return;
            }

            Dictionary<string, bool> enabledMap = ReadModEnabledMap();
            List<(ModStoreItem item, string dir)> loaded = new();

            foreach (string d in Directory.GetDirectories(dir))
            {
                if (!TryParseModId(Path.GetFileName(d), out long modId))
                {
                    continue;
                }

                ModStoreItem item = ReadInstalledMeta(d, modId);
                item.Installed = true;
                item.Favorited = _favoriteIds.Contains(modId);
                item.Enabled = !enabledMap.TryGetValue(d, out bool en) || en;
                Mods.Add(item);
                loaded.Add((item, d));
                if (!string.IsNullOrEmpty(item.ThumbnailUrl) && cts != null)
                {
                    _ = LoadThumbnailAsync(item, cts.Token);
                }
            }

            _ = ComputeConflictsAsync(loaded);
        }

        private static ModStoreItem ReadInstalledMeta(string dir, long modId)
        {
            try
            {
                string meta = Path.Combine(dir, ".nxmeta.json");
                if (File.Exists(meta))
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(meta));
                    JsonElement r = doc.RootElement;
                    return new ModStoreItem
                    {
                        ModId = modId,
                        Name = GetStr(r, "name"),
                        Category = GetStr(r, "category"),
                        Author = GetStr(r, "author"),
                        Likes = (int)GetLong(r, "likes"),
                        Version = GetStr(r, "version"),
                        ThumbnailUrl = GetStr(r, "thumbnail"),
                        ProfileUrl = GetStr(r, "profile"),
                    };
                }
            }
            catch
            {
                // fall through to a minimal card
            }

            return new ModStoreItem
            {
                ModId = modId,
                Name = $"Mod #{modId}",
                ProfileUrl = $"https://gamebanana.com/mods/{modId}",
            };
        }

        private static void WriteInstalledMeta(string target, ModStoreItem item)
        {
            try
            {
                Dictionary<string, object> meta = new()
                {
                    ["mod_id"] = item.ModId,
                    ["name"] = item.Name ?? "",
                    ["category"] = item.Category ?? "",
                    ["author"] = item.Author ?? "",
                    ["likes"] = item.Likes,
                    ["version"] = item.Version ?? "",
                    ["thumbnail"] = item.ThumbnailUrl ?? "",
                    ["profile"] = item.ProfileUrl ?? "",
                };
                File.WriteAllText(Path.Combine(target, ".nxmeta.json"), JsonSerializer.Serialize(meta));
            }
            catch
            {
                // metadata is a nicety; the install still succeeded without it
            }
        }

        // The version recorded at install time (from .nxmeta.json), or null.
        private string InstalledVersion(long modId)
        {
            try
            {
                string dir = InstalledDirFor(modId);
                if (dir == null)
                {
                    return null;
                }

                string meta = Path.Combine(dir, ".nxmeta.json");
                if (File.Exists(meta))
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(meta));
                    return GetStr(doc.RootElement, "version");
                }
            }
            catch
            {
                // ignore
            }

            return null;
        }

        // For the "Installed" view: fetch each mod's CURRENT GameBanana version and flag it if it
        // differs from the one we installed (a "MAJ dispo" badge + Update button).
        private async Task CheckUpdatesForInstalledAsync(CancellationToken ct)
        {
            foreach (ModStoreItem item in Mods.ToArray())
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                if (!item.Installed || string.IsNullOrEmpty(item.Version))
                {
                    continue;
                }

                try
                {
                    using JsonDocument doc = await GetJsonAsync($"{ApiBase}/Mod/{item.ModId}/ProfilePage", ct);
                    string cur = doc != null ? GetStr(doc.RootElement, "_sVersion") : "";
                    if (!string.IsNullOrEmpty(cur) && !string.Equals(cur, item.Version, StringComparison.OrdinalIgnoreCase))
                    {
                        item.UpdateAvailable = true;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    // best-effort
                }
            }
        }

        // ---- enable / disable + conflicts ----------------------------------------------

        private Dictionary<string, bool> ReadModEnabledMap()
        {
            Dictionary<string, bool> map = new(StringComparer.OrdinalIgnoreCase);
            try
            {
                string p = Path.Combine(AppDataManager.GamesDirPath, _titleId.ToString("x16"), "mods.json");
                if (File.Exists(p))
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(p));
                    if (doc.RootElement.TryGetProperty("mods", out JsonElement mods) && mods.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement m in mods.EnumerateArray())
                        {
                            string path = GetStr(m, "path");
                            bool enabled = !(m.TryGetProperty("enabled", out JsonElement e) && e.ValueKind == JsonValueKind.False);
                            if (!string.IsNullOrEmpty(path))
                            {
                                map[path] = enabled;
                            }
                        }
                    }
                }
            }
            catch
            {
                // ignore
            }

            return map;
        }

        // Writes the mod's enabled/disabled state into the game's mods.json (Ryujinx reads this at
        // launch). Keys are lowercase to match Ryujinx's on-disk format exactly.
        private void SetModEnabled(long modId, bool enabled)
        {
            try
            {
                string modDir = InstalledDirFor(modId);
                if (modDir == null)
                {
                    return;
                }

                string p = Path.Combine(AppDataManager.GamesDirPath, _titleId.ToString("x16"), "mods.json");
                List<Dictionary<string, object>> mods = new();
                if (File.Exists(p))
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(p));
                    if (doc.RootElement.TryGetProperty("mods", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement m in arr.EnumerateArray())
                        {
                            string path = GetStr(m, "path");
                            if (string.IsNullOrEmpty(path))
                            {
                                continue;
                            }

                            bool en = !(m.TryGetProperty("enabled", out JsonElement e) && e.ValueKind == JsonValueKind.False);
                            mods.Add(new() { ["name"] = GetStr(m, "name"), ["path"] = path, ["enabled"] = en });
                        }
                    }
                }

                Dictionary<string, object> entry = mods.FirstOrDefault(m => string.Equals(m["path"] as string, modDir, StringComparison.OrdinalIgnoreCase));
                if (entry != null)
                {
                    entry["enabled"] = enabled;
                }
                else
                {
                    mods.Add(new() { ["name"] = Path.GetFileName(modDir), ["path"] = modDir, ["enabled"] = enabled });
                }

                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p, JsonSerializer.Serialize(new Dictionary<string, object> { ["mods"] = mods }));
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"[Nextendo] mod enable/disable failed: {ex.Message}");
            }
        }

        // Called after the "enabled" checkbox toggles (its two-way binding already flipped item.Enabled).
        public void PersistModEnabled(ModStoreItem item)
        {
            if (item != null && item.Installed)
            {
                SetModEnabled(item.ModId, item.Enabled);
            }
        }

        // Flags installed mods that overwrite the same romfs files as another ENABLED mod. The file
        // walk runs off the UI thread; flags are marshalled back.
        private async Task ComputeConflictsAsync(List<(ModStoreItem item, string dir)> loaded)
        {
            HashSet<ModStoreItem> conflicting = await Task.Run(() => FindConflicts(loaded));
            if (conflicting.Count == 0)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (ModStoreItem it in conflicting)
                {
                    it.HasConflict = true;
                }
            });
        }

        private static HashSet<ModStoreItem> FindConflicts(List<(ModStoreItem item, string dir)> loaded)
        {
            HashSet<ModStoreItem> conflicting = new();
            try
            {
                Dictionary<ModStoreItem, HashSet<string>> files = new();
                foreach ((ModStoreItem item, string dir) in loaded)
                {
                    if (!item.Enabled)
                    {
                        continue;
                    }

                    string romfs = Path.Combine(dir, "romfs");
                    if (!Directory.Exists(romfs))
                    {
                        continue;
                    }

                    HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);
                    foreach (string f in Directory.GetFiles(romfs, "*", SearchOption.AllDirectories))
                    {
                        set.Add(Path.GetRelativePath(romfs, f).Replace('\\', '/'));
                    }

                    files[item] = set;
                }

                List<ModStoreItem> keys = files.Keys.ToList();
                for (int i = 0; i < keys.Count; i++)
                {
                    for (int j = i + 1; j < keys.Count; j++)
                    {
                        if (files[keys[i]].Overlaps(files[keys[j]]))
                        {
                            conflicting.Add(keys[i]);
                            conflicting.Add(keys[j]);
                        }
                    }
                }
            }
            catch
            {
                // best-effort
            }

            return conflicting;
        }

        // ---- favorites (synced to the Nextendo account) --------------------------------

        private void CollectFavoriteIds(JsonDocument doc)
        {
            if (doc != null && doc.RootElement.TryGetProperty("favorites", out JsonElement favs) && favs.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement f in favs.EnumerateArray())
                {
                    long modId = GetLong(f, "mod_id");
                    if (modId != 0)
                    {
                        _favoriteIds.Add(modId);
                    }
                }
            }
        }

        private async Task<JsonDocument> GetFavoritesJsonAsync(string token, CancellationToken ct)
        {
            using HttpRequestMessage req = new(HttpMethod.Get, $"{NxBase()}/api/mod-favorites?game_id={_gameId}");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            using HttpResponseMessage resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            string body = await resp.Content.ReadAsStringAsync(ct);
            return string.IsNullOrWhiteSpace(body) ? null : JsonDocument.Parse(body);
        }

        // Loads just the favorited mod ids so hearts light up across the grid.
        private async Task LoadFavoriteIdsAsync()
        {
            _favoriteIds.Clear();
            string token = NextendoAccount.NexToken;
            if (string.IsNullOrEmpty(token))
            {
                return;
            }

            try
            {
                using JsonDocument doc = await GetFavoritesJsonAsync(token, CancellationToken.None);
                CollectFavoriteIds(doc);
            }
            catch
            {
                // best-effort
            }
        }

        // The "Favorites" chip: the account's favorited mods for this game.
        private async Task LoadFavoritesAsync(CancellationToken ct)
        {
            CanLoadMore = false;
            string token = NextendoAccount.NexToken;
            if (string.IsNullOrEmpty(token))
            {
                Status = L(LocaleKeys.Dialog_Nextendo_ModStoreFavoritesLoginRequired);
                return;
            }

            try
            {
                using JsonDocument doc = await GetFavoritesJsonAsync(token, ct);
                if (doc != null && doc.RootElement.TryGetProperty("favorites", out JsonElement favs) && favs.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement f in favs.EnumerateArray())
                    {
                        long modId = GetLong(f, "mod_id");
                        if (modId == 0)
                        {
                            continue;
                        }

                        _favoriteIds.Add(modId);
                        ModStoreItem item = new()
                        {
                            ModId = modId,
                            Name = GetStr(f, "name"),
                            Category = GetStr(f, "category"),
                            Author = GetStr(f, "author"),
                            ThumbnailUrl = GetStr(f, "thumbnail_url"),
                            ProfileUrl = GetStr(f, "profile_url"),
                        };
                        item.Favorited = true;
                        item.Installed = InstalledDirFor(modId) != null;
                        Mods.Add(item);
                        if (!string.IsNullOrEmpty(item.ThumbnailUrl))
                        {
                            _ = LoadThumbnailAsync(item, ct);
                        }
                    }
                }

                Status = Mods.Count == 0
                    ? L(LocaleKeys.Dialog_Nextendo_ModStoreNoMods)
                    : LF(LocaleKeys.Dialog_Nextendo_ModStoreModsAvailableFormat, Mods.Count);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                Status = L(LocaleKeys.Dialog_Nextendo_ModStoreLoadError);
            }
        }

        // Heart button: add/remove this mod on the account (synced across PCs).
        public async Task ToggleFavoriteAsync(ModStoreItem item)
        {
            if (item == null)
            {
                return;
            }

            string token = NextendoAccount.NexToken;
            if (string.IsNullOrEmpty(token))
            {
                Status = L(LocaleKeys.Dialog_Nextendo_ModStoreFavoritesLoginRequired);
                return;
            }

            try
            {
                if (item.Favorited)
                {
                    string body = JsonSerializer.Serialize(new { mod_id = item.ModId });
                    if (await PostAuthedAsync($"{NxBase()}/api/mod-favorites/remove", body, token))
                    {
                        item.Favorited = false;
                        _favoriteIds.Remove(item.ModId);
                    }
                }
                else
                {
                    string body = JsonSerializer.Serialize(new
                    {
                        mod_id = item.ModId,
                        game_id = _gameId,
                        name = item.Name,
                        author = item.Author,
                        category = item.Category,
                        thumbnail_url = item.ThumbnailUrl,
                        profile_url = item.ProfileUrl,
                    });
                    if (await PostAuthedAsync($"{NxBase()}/api/mod-favorites", body, token))
                    {
                        item.Favorited = true;
                        _favoriteIds.Add(item.ModId);
                    }
                }
            }
            catch
            {
                // best-effort
            }
        }

        private static async Task<bool> PostAuthedAsync(string url, string jsonBody, string token)
        {
            using HttpRequestMessage req = new(HttpMethod.Post, url)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            using HttpResponseMessage resp = await Http.SendAsync(req);
            return resp.IsSuccessStatusCode;
        }

        // ---- install / remove ----------------------------------------------------------

        public async Task DownloadAsync(ModStoreItem item)
        {
            if (item == null || item.Busy)
            {
                return;
            }

            item.Busy = true;
            Status = LF(LocaleKeys.Dialog_Nextendo_ModStoreDownloadingFormat, item.Name);
            string tmpArchive = Path.Combine(Path.GetTempPath(), $"nxgb_{item.ModId}.zip");
            string tmpDir = Path.Combine(Path.GetTempPath(), $"nxgb_{item.ModId}_x");

            try
            {
                (string url, string fileName) = await ResolveDownloadAsync(item.ModId);
                if (url == null)
                {
                    Status = LF(LocaleKeys.Dialog_Nextendo_ModStoreNoSafeFileFormat, item.Name);
                    return;
                }

                byte[] data = await Http.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(tmpArchive, data);

                if (Directory.Exists(tmpDir))
                {
                    Directory.Delete(tmpDir, true);
                }

                Directory.CreateDirectory(tmpDir);

                try
                {
                    ExtractArchive(tmpArchive, tmpDir); // zip / 7z / rar / tar (auto-detected by content)
                }
                catch (Exception zex)
                {
                    Status = LF(LocaleKeys.Dialog_Nextendo_ModStoreUnsupportedFormat, item.Name, Path.GetExtension(fileName));
                    Logger.Warning?.Print(LogClass.Application, $"[Nextendo] mod extract failed: {zex.Message}");
                    return;
                }

                // Reinstall cleanly: drop any previous folder for this mod (either naming scheme).
                string existing = InstalledDirFor(item.ModId);
                if (existing != null && Directory.Exists(existing))
                {
                    Directory.Delete(existing, true);
                }

                string target = Path.Combine(ModsContentsDir, ModFolderName(item));
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, true);
                }

                if (!InstallNormalized(tmpDir, target))
                {
                    Status = LF(LocaleKeys.Dialog_Nextendo_ModStoreBadStructureFormat, item.Name);
                    return;
                }

                WriteInstalledMeta(target, item);
                item.Installed = true;
                item.UpdateAvailable = false;
                Status = LF(LocaleKeys.Dialog_Nextendo_ModStoreInstalledFormat, item.Name);
                Logger.Info?.Print(LogClass.Application, $"[Nextendo] GameBanana mod installed: {target}");
            }
            catch (Exception ex)
            {
                Status = LF(LocaleKeys.Dialog_Nextendo_ModStoreDownloadFailedFormat, item.Name);
                Logger.Warning?.Print(LogClass.Application, $"[Nextendo] mod download failed: {ex.Message}");
            }
            finally
            {
                try { File.Delete(tmpArchive); } catch { /* best effort */ }
                try { if (Directory.Exists(tmpDir)) { Directory.Delete(tmpDir, true); } } catch { /* best effort */ }
                item.Busy = false;
            }
        }

        public async Task DeleteAsync(ModStoreItem item)
        {
            if (item == null || item.Busy)
            {
                return;
            }

            item.Busy = true;
            Status = LF(LocaleKeys.Dialog_Nextendo_ModStoreDeletingFormat, item.Name);

            try
            {
                string dir = InstalledDirFor(item.ModId);
                await Task.Run(() =>
                {
                    if (dir != null && Directory.Exists(dir))
                    {
                        Directory.Delete(dir, true);
                    }
                });

                item.Installed = InstalledDirFor(item.ModId) != null;
                Status = item.Installed
                    ? LF(LocaleKeys.Dialog_Nextendo_ModStoreDeleteFailedFormat, item.Name)
                    : LF(LocaleKeys.Dialog_Nextendo_ModStoreDeletedSuccessFormat, item.Name);
                Logger.Info?.Print(LogClass.Application, $"[Nextendo] GameBanana mod removed: {item.Name} (gb{item.ModId})");
            }
            catch (Exception ex)
            {
                Status = LF(LocaleKeys.Dialog_Nextendo_ModStoreDeleteErrorFormat, item.Name, ex.Message);
            }
            finally
            {
                item.Busy = false;
            }
        }

        private async Task<(string url, string file)> ResolveDownloadAsync(long modId)
        {
            using JsonDocument doc = await GetJsonAsync($"{ApiBase}/Mod/{modId}/DownloadPage");
            if (doc == null || !doc.RootElement.TryGetProperty("_aFiles", out JsonElement files) || files.ValueKind != JsonValueKind.Array)
            {
                return (null, null);
            }

            string fallbackUrl = null, fallbackName = null;
            foreach (JsonElement f in files.EnumerateArray())
            {
                string url = GetStr(f, "_sDownloadUrl");
                string name = GetStr(f, "_sFile");
                if (string.IsNullOrEmpty(url) || !IsAllowedDownloadUrl(url))
                {
                    continue;
                }

                string av = GetStr(f, "_sAvResult");
                string an = GetStr(f, "_sAnalysisResult");
                bool clean = (av.Length == 0 || av.Equals("clean", StringComparison.OrdinalIgnoreCase))
                          && (an.Length == 0 || an.Equals("ok", StringComparison.OrdinalIgnoreCase));
                if (!clean)
                {
                    continue;
                }

                if (IsArchiveName(name))
                {
                    return (url, name);
                }

                fallbackUrl ??= url;
                fallbackName ??= name;
            }

            return (fallbackUrl, fallbackName);
        }

        private static readonly string[] ModRoots = { "romfs", "exefs", "cheats" };

        private static bool InstallNormalized(string extractedRoot, string target)
        {
            // 1) A directory that DIRECTLY contains romfs/exefs/cheats -> copy it as the mod root
            //    (handles "romfs/…", "ModName/romfs/…", "<TitleId>/romfs/…", packs, etc.).
            string direct = FindDir(extractedRoot,
                d => Directory.GetDirectories(d).Any(s => ModRoots.Contains(Path.GetFileName(s).ToLowerInvariant())));
            if (direct != null)
            {
                CopyDir(direct, target);
                return true;
            }

            // 2) A folder named like a Title ID (16 hex) whose CONTENTS are the romfs, i.e. the
            //    common "ModName/<TitleId>/Model/…" layout with no explicit "romfs" folder. Those
            //    contents belong under <target>/romfs (this is what most Splatoon/MK8 mods ship).
            string titleDir = FindDir(extractedRoot, d => IsTitleId(Path.GetFileName(d)));
            if (titleDir != null && Directory.EnumerateFileSystemEntries(titleDir).Any())
            {
                CopyDir(titleDir, Path.Combine(target, "romfs"));
                return true;
            }

            return false;
        }

        private static bool IsTitleId(string name) =>
            name != null && name.Length == 16 && name.All(char.IsAsciiHexDigit);

        // BFS for the shallowest directory (root included) that satisfies pred.
        private static string FindDir(string root, Func<string, bool> pred)
        {
            Queue<string> queue = new();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                string cur = queue.Dequeue();
                if (pred(cur))
                {
                    return cur;
                }

                foreach (string s in Directory.GetDirectories(cur))
                {
                    queue.Enqueue(s);
                }
            }

            return null;
        }

        private static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dir.Replace(from, to));
            }

            foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                File.Copy(file, file.Replace(from, to), true);
            }
        }

        // Only fetch mod files from GameBanana itself / its CDN over HTTPS. The URL comes from
        // GameBanana's own API, but validating it defends against a tampered or hijacked API
        // response steering the emulator at an attacker-controlled host.
        private static bool IsAllowedDownloadUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri u) || u.Scheme != Uri.UriSchemeHttps)
            {
                return false;
            }

            string host = u.Host;
            return host.Equals("gamebanana.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".gamebanana.com", StringComparison.OrdinalIgnoreCase);
        }

        private static readonly string[] ArchiveExts = { ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2" };

        private static bool IsArchiveName(string name) =>
            !string.IsNullOrEmpty(name) && ArchiveExts.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

        // Extract any supported archive (zip / 7z / rar / tar…) — SharpCompress auto-detects the
        // format from the content, so .7z and .rar mods install just like .zip ones.
        private static void ExtractArchive(string archivePath, string destDir)
        {
            Directory.CreateDirectory(destDir);

            // Zip Slip guard: a malicious mod archive could carry entries like "../../../..\\evil"
            // that, extracted with ExtractFullPath, would write OUTSIDE destDir and overwrite
            // arbitrary files on the user's machine. We refuse any entry whose resolved path
            // escapes destDir before writing it.
            string destFull = Path.GetFullPath(destDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            using IArchive archive = ArchiveFactory.OpenArchive(archivePath);
            ExtractionOptions options = new() { ExtractFullPath = true, Overwrite = true };
            foreach (IArchiveEntry entry in archive.Entries)
            {
                if (entry.IsDirectory)
                {
                    continue;
                }

                string key = entry.Key ?? string.Empty;
                string resolved = Path.GetFullPath(Path.Combine(destDir, key.Replace('\\', '/')));
                if (!resolved.StartsWith(destFull, StringComparison.Ordinal))
                {
                    Logger.Warning?.Print(LogClass.Application,
                        $"[Nextendo] mod archive: skipped path-traversal entry '{key}'");
                    continue;
                }

                entry.WriteToDirectory(destDir, options);
            }
        }

        // ---- client-side / cosmetic heuristic ------------------------------------------

        // Substring-matched against the (single-word) category name, so avoid fragments that
        // collide with cosmetic words — "ai" is inside "hair", "mode" is inside "model".
        private static readonly string[] GameplayWords =
        {
            "course", "track", "map", "stage", "level", "patch", "cheat", "trainer",
            "tool", "script", "hack", "gameplay", "physic", "balanc", "mechanic", "mission",
            "engine", "aimbot",
        };

        private static readonly string[] CosmeticWords =
        {
            "skin", "texture", "model", "mesh", "costume", "outfit", "cloth", "color", "colour",
            "recolor", "recolour", "retexture", "sprite", "shader", "reshade", "icon", "cursor",
            "hud", "menu", "interface", "theme", "font", "sound", "audio", "music",
            "voice", "sfx", "song", "bgm", "effect", "particle", "emote", "decal", "livery",
            "paint", "wallpaper", "face", "eye", "hair", "mii", "character", "kart", "vehicle",
            "body", "glider", "wheel", "appearance", "cosmetic", "visual", "overlay", "hat",
        };

        public static bool IsCosmeticCategory(string cat)
        {
            if (string.IsNullOrWhiteSpace(cat))
            {
                return false;
            }

            string c = cat.ToLowerInvariant();
            if (GameplayWords.Any(w => c.Contains(w)))
            {
                return false;
            }

            return CosmeticWords.Any(w => c.Contains(w));
        }

        // ---- json helpers --------------------------------------------------------------

        private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct = default)
        {
            try
            {
                using HttpResponseMessage resp = await Http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    return null;
                }

                string body = await resp.Content.ReadAsStringAsync(ct);
                return string.IsNullOrWhiteSpace(body) ? null : JsonDocument.Parse(body);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        private static string GetStr(JsonElement e, string prop) =>
            e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

        private static long GetLong(JsonElement e, string prop) =>
            e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) ? l : 0;
    }
}
