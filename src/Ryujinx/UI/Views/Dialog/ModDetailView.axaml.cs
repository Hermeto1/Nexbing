using Avalonia.Interactivity;
using FluentAvalonia.UI.Controls;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.UI.Controls;
using Ryujinx.Ava.UI.Models;
using Ryujinx.Ava.UI.ViewModels;
using System.Threading.Tasks;

namespace Ryujinx.Ava.UI.Views.Dialog
{
    public partial class ModDetailView : RyujinxControl<ModDetailViewModel>
    {
        public ModDetailView()
        {
            InitializeComponent();
        }

        public static async Task Show(ModStoreItem item, ModStoreViewModel store)
        {
            ContentDialog dialog = new()
            {
                PrimaryButtonText = string.Empty,
                SecondaryButtonText = string.Empty,
                CloseButtonText = LocaleManager.Instance[LocaleKeys.Dialog_Nextendo_ModStoreClose],
                Content = new ModDetailView
                {
                    ViewModel = new ModDetailViewModel(item, store),
                },
                Title = item.Name,
            };

            dialog.Resources["ContentDialogMaxWidth"] = 720.0;
            dialog.Resources["ContentDialogMaxHeight"] = 780.0;

            await dialog.ShowAsync();
        }

        private async void OnInstall(object sender, RoutedEventArgs e)
        {
            await ViewModel.InstallOrUpdateAsync();
        }

        private async void OnDelete(object sender, RoutedEventArgs e)
        {
            await ViewModel.DeleteAsync();
        }

        private async void OnFavorite(object sender, RoutedEventArgs e)
        {
            await ViewModel.ToggleFavoriteAsync();
        }

        private void OnOpenGb(object sender, RoutedEventArgs e)
        {
            ViewModel.OpenGameBanana();
        }
    }
}
