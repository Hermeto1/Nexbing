using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Ryujinx.Ava.UI.Models;
using Ryujinx.Common.Helper;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Ryujinx.Ava.UI.ViewModels
{
    // [Nextendo] In-app detail panel for a GameBanana mod: screenshots + description, fetched from
    // the mod's ProfilePage. Install / favorite delegate back to the owning ModStoreViewModel so the
    // grid card stays in sync (same ModStoreItem instance).
    public partial class ModDetailViewModel : BaseModel
    {
        private static readonly HttpClient Http = CreateHttp();

        private readonly ModStoreViewModel _store;

        public ModStoreItem Item { get; }
        public ObservableCollection<Bitmap> Images { get; } = [];

        [ObservableProperty] private string _description = "";
        [ObservableProperty] private bool _loading = true;

        public string Name => Item?.Name ?? "";
        public string MetaText => Item?.MetaText ?? "";
        public string StatsText => Item?.StatsText ?? "";

        public ModDetailViewModel(ModStoreItem item, ModStoreViewModel store)
        {
            Item = item;
            _store = store;
            _ = LoadAsync();
        }

        private static HttpClient CreateHttp()
        {
            HttpClient http = new() { Timeout = TimeSpan.FromSeconds(25) };
            http.DefaultRequestHeaders.Add("User-Agent", "Ryujinx-Nextendo-ModStore");
            http.DefaultRequestHeaders.Add("Accept", "application/json");
            return http;
        }

        private async Task LoadAsync()
        {
            // Affichage INSTANTANÉ : la carte de la grille a déjà décodé une vignette en mémoire.
            // On la montre tout de suite pour que le panneau ne soit jamais vide, puis on charge les
            // captures pleine résolution derrière (la première remplace la vignette).
            bool seededHero = Item?.Thumbnail != null;
            if (seededHero)
            {
                Images.Add(Item.Thumbnail);
            }

            try
            {
                string body = await Http.GetStringAsync($"https://gamebanana.com/apiv11/Mod/{Item.ModId}/ProfilePage");
                using JsonDocument doc = JsonDocument.Parse(body);
                JsonElement r = doc.RootElement;

                Description = StripHtml(GetStr(r, "_sText"));

                List<string> urls = [];
                if (r.TryGetProperty("_aPreviewMedia", out JsonElement pm) && pm.ValueKind == JsonValueKind.Object
                    && pm.TryGetProperty("_aImages", out JsonElement imgs) && imgs.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement im in imgs.EnumerateArray())
                    {
                        string baseUrl = GetStr(im, "_sBaseUrl");
                        string file = GetStr(im, "_sFile530");
                        if (string.IsNullOrEmpty(file))
                        {
                            file = GetStr(im, "_sFile");
                        }

                        if (!string.IsNullOrEmpty(baseUrl) && !string.IsNullOrEmpty(file))
                        {
                            urls.Add($"{baseUrl}/{file}");
                        }
                    }
                }

                // Séquentiel = ordre d'affichage garanti. La vignette (seededHero) sert d'aperçu
                // immédiat ; la 1re image pleine résolution la remplace en place, les suivantes s'ajoutent.
                for (int i = 0; i < urls.Count; i++)
                {
                    Bitmap bmp = await LoadBitmapAsync(urls[i]);
                    if (bmp == null)
                    {
                        continue;
                    }

                    bool replaceHero = seededHero && i == 0;
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (replaceHero && Images.Count > 0)
                        {
                            Images[0] = bmp;
                        }
                        else
                        {
                            Images.Add(bmp);
                        }
                    });
                }
            }
            catch
            {
                // best-effort; the panel still shows name/meta + buttons
            }
            finally
            {
                Loading = false;
            }
        }

        private static async Task<Bitmap> LoadBitmapAsync(string url)
        {
            try
            {
                byte[] bytes = await Http.GetByteArrayAsync(url);
                if (bytes.Length == 0)
                {
                    return null;
                }

                return new Bitmap(new MemoryStream(bytes));
            }
            catch
            {
                return null;
            }
        }

        public Task InstallOrUpdateAsync() => _store.DownloadAsync(Item);

        public Task DeleteAsync() => _store.DeleteAsync(Item);

        public Task ToggleFavoriteAsync() => _store.ToggleFavoriteAsync(Item);

        public void OpenGameBanana()
        {
            if (!string.IsNullOrEmpty(Item?.ProfileUrl))
            {
                OpenHelper.OpenUrl(Item.ProfileUrl);
            }
        }

        // GameBanana descriptions are small HTML fragments; render them as readable plain text.
        private static string StripHtml(string html)
        {
            if (string.IsNullOrEmpty(html))
            {
                return "";
            }

            string s = Regex.Replace(html, "(?i)<br\\s*/?>", "\n");
            s = Regex.Replace(s, "(?i)</p>", "\n\n");
            s = Regex.Replace(s, "<[^>]+>", "");
            s = s.Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
                 .Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&rsquo;", "'").Replace("&lsquo;", "'");
            s = Regex.Replace(s, "\n{3,}", "\n\n");
            return s.Trim();
        }

        private static string GetStr(JsonElement e, string prop) =>
            e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
    }
}
