using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FluentAvalonia.UI.Controls;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.UI.Controls;
using Ryujinx.Ava.UI.Models;
using Ryujinx.Ava.UI.ViewModels;
using System.Threading.Tasks;
using Button = Avalonia.Controls.Button;

namespace Ryujinx.Ava.UI.Views.Dialog
{
    public partial class ModStoreView : RyujinxControl<ModStoreViewModel>
    {
        public ModStoreView()
        {
            InitializeComponent();
        }

        public static async Task Show(ulong titleId, string titleName)
        {
            ContentDialog contentDialog = new()
            {
                PrimaryButtonText = string.Empty,
                SecondaryButtonText = string.Empty,
                CloseButtonText = LocaleManager.Instance[LocaleKeys.Dialog_Nextendo_ModStoreClose],
                Content = new ModStoreView
                {
                    ViewModel = new ModStoreViewModel(titleId, titleName),
                },
                Title = LocaleManager.GetFormatted(LocaleKeys.Dialog_Nextendo_ModStoreTitleFormat, titleName),
            };

            // La largeur/hauteur max par défaut d'un ContentDialog (548 px de large) rognait cette
            // grille des DEUX côtés et poussait le bouton Fermer hors écran. On les élargit pour ce
            // dialog (un bouton Fermer NATIF est aussi ajouté ci-dessus, toujours accessible).
            contentDialog.Resources["ContentDialogMaxWidth"] = 980.0;
            contentDialog.Resources["ContentDialogMaxHeight"] = 940.0;

            await contentDialog.ShowAsync();
        }

        private async void OnSearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                await ViewModel.SearchAsync(ViewModel.SearchText);
            }
        }

        private async void OnSearchClick(object sender, RoutedEventArgs e)
        {
            await ViewModel.SearchAsync(ViewModel.SearchText);
        }

        private async void OnCategoryClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: ModCategory category })
            {
                await ViewModel.SelectCategoryAsync(category);
            }
        }

        private async void OnLoadMore(object sender, RoutedEventArgs e)
        {
            await ViewModel.LoadMoreAsync();
        }

        // Click a mod's preview/name -> open its GameBanana page in the browser for full details.
        private async void OnModTapped(object sender, TappedEventArgs e)
        {
            if (sender is Control { DataContext: ModStoreItem item })
            {
                await ModDetailView.Show(item, ViewModel);
            }
        }

        private async void OnFavoriteClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: ModStoreItem item })
            {
                await ViewModel.ToggleFavoriteAsync(item);
            }
        }

        // The "Enabled" checkbox already flipped item.Enabled via its two-way binding; persist it.
        private void OnToggleEnabled(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox { DataContext: ModStoreItem item })
            {
                ViewModel.PersistModEnabled(item);
            }
        }

        private async void Download(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: ModStoreItem item })
            {
                await ViewModel.DownloadAsync(item);
            }
        }

        private async void Delete(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: ModStoreItem item })
            {
                await ViewModel.DeleteAsync(item);
            }
        }

        // Le bouton "Fermer" natif du ContentDialog (CloseButtonText) gère la fermeture — toujours
        // accessible même si la mise en page interne débordait.
    }
}
