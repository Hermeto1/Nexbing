using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Navigation;
using Ryujinx.Ava.Common;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.UI.Controls;
using Ryujinx.Ava.UI.Helpers;
using Ryujinx.Ava.UI.Models;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using UserProfile = Ryujinx.Ava.UI.Models.UserProfile;

namespace Ryujinx.Ava.UI.Views.User
{
    public partial class UserEditorView : RyujinxControl<TempProfile>
    {
        private NavigationDialogHost _parent;
        private UserProfile _profile;
        private bool _isNewUser;

        public static uint MaxProfileNameLength => 0x20;
        public bool IsDeletable => _profile.UserId != AccountManager.DefaultUserId;

        public UserEditorView()
        {
            InitializeComponent();
            AddHandler(Frame.NavigatedToEvent, (s, e) =>
            {
                NavigatedTo(e);
            }, RoutingStrategies.Direct);
        }

        private void NavigatedTo(NavigationEventArgs arg)
        {
            if (Program.PreviewerDetached)
            {
                switch (arg.NavigationMode)
                {
                    case NavigationMode.New:
                        (NavigationDialogHost parent, UserProfile profile, bool isNewUser) = ((NavigationDialogHost parent, UserProfile profile, bool isNewUser))arg.Parameter;
                        _isNewUser = isNewUser;
                        _profile = profile;
                        ViewModel = new TempProfile(_profile);

                        _parent = parent;
                        break;
                }

                ((ContentDialog)_parent.Parent).Title = $"{LocaleManager.Instance[LocaleKeys.UserProfileWindowTitle]} - " +
                                                        $"{(_isNewUser ? LocaleManager.Instance[LocaleKeys.UserEditorTitleCreate] : LocaleManager.Instance[LocaleKeys.UserEditorTitle])}";

                AddPictureButton.IsVisible = _isNewUser;
                ChangePictureButton.IsVisible = !_isNewUser;
                IdLabel.IsVisible = _profile != null;
                IdText.IsVisible = _profile != null;
                if (!_isNewUser && IsDeletable)
                {
                    DeleteButton.IsVisible = true;
                }
                else
                {
                    DeleteButton.IsVisible = false;
                }
            }
        }

        private async void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isNewUser)
            {
                if (ViewModel.Name != string.Empty || ViewModel.Image != null)
                {
                    if (await ContentDialogHelper.CreateChoiceDialog(
                            LocaleManager.Instance[LocaleKeys.DialogUserProfileUnsavedChangesTitle],
                            LocaleManager.Instance[LocaleKeys.DialogUserProfileUnsavedChangesMessage],
                            LocaleManager.Instance[LocaleKeys.DialogUserProfileUnsavedChangesSubMessage]))
                    {
                        _parent?.GoBack();
                    }
                }
                else
                {
                    _parent?.GoBack();
                }
            }
            else
            {
                if (_profile.Name != ViewModel.Name || _profile.Image != ViewModel.Image)
                {
                    if (await ContentDialogHelper.CreateChoiceDialog(
                            LocaleManager.Instance[LocaleKeys.DialogUserProfileUnsavedChangesTitle],
                            LocaleManager.Instance[LocaleKeys.DialogUserProfileUnsavedChangesMessage],
                            LocaleManager.Instance[LocaleKeys.DialogUserProfileUnsavedChangesSubMessage]))
                    {
                        _parent?.GoBack();
                    }
                }
                else
                {
                    _parent?.GoBack();
                }
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            _parent.DeleteUser(_profile);
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            DataValidationErrors.ClearErrors(NameBox);

            if (string.IsNullOrWhiteSpace(ViewModel.Name))
            {
                DataValidationErrors.SetError(NameBox, new DataValidationException(LocaleManager.Instance[LocaleKeys.UserProfileEmptyNameError]));

                return;
            }

            if (ViewModel.Image == null)
            {
                _parent.Navigate(typeof(UserProfileImageSelectorView), (_parent, ViewModel));

                return;
            }

            if (_profile != null && !_isNewUser)
            {
                // [Nextendo] The account is the SOURCE OF TRUTH for a linked profile's name + picture.
                // Editing it must reach the server, else the local change would silently diverge from
                // the site (and the in-game identity) with no way to reconcile. So for a linked profile
                // we PUSH FIRST (the name push doubles as the online probe) and only apply locally if
                // it succeeds — offline / server down => the edit is rejected.  (#2 sync-up + #3 offline gate.)
                if (_profile.IsNextendoLinked)
                {
                    (bool ok, string err) = await NextendoApi.SetUsernameAsync(ViewModel.Name);
                    if (!ok)
                    {
                        bool fr = LocaleManager.Instance.CurrentLanguageCode?.StartsWith("fr", System.StringComparison.OrdinalIgnoreCase) ?? false;
                        string msg = !string.IsNullOrEmpty(err)
                            ? err
                            : (fr
                                ? "Tu dois être en ligne pour modifier ton profil Nextendo : le changement doit être synchronisé avec ton compte."
                                : "You must be online to change your Nextendo profile: the change has to sync with your account.");
                        await ContentDialogHelper.CreateErrorDialog(msg);
                        return; // rejected — nothing applied locally, so no divergence with the account
                    }

                    // Online (the name push went through): push the new picture too (best-effort).
                    if (ViewModel.Image is { Length: > 0 })
                    {
                        _ = NextendoApi.SetProfileImageAsync(ViewModel.Image);
                    }
                }

                _profile.Name = ViewModel.Name;
                _profile.Image = ViewModel.Image;
                _profile.UpdateState();
                _parent.AccountManager.SetUserName(_profile.UserId, _profile.Name);
                _parent.AccountManager.SetUserImage(_profile.UserId, _profile.Image);
            }
            else if (_isNewUser)
            {
                _parent.AccountManager.AddUser(ViewModel.Name, ViewModel.Image, ViewModel.UserId);
            }
            else
            {
                return;
            }

            _parent?.GoBack();
        }

        public void SelectProfileImage()
        {
            _parent.Navigate(typeof(UserProfileImageSelectorView), (_parent, ViewModel));
        }

        private void ChangePictureButton_Click(object sender, RoutedEventArgs e)
        {
            if (_profile != null || _isNewUser)
            {
                SelectProfileImage();
            }
        }
    }
}
