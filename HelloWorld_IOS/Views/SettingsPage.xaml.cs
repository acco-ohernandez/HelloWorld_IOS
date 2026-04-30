using HelloWorld_IOS.ViewModels;

namespace HelloWorld_IOS.Views;

public partial class SettingsPage : ContentPage
{
    private readonly SettingsViewModel _vm;
    private readonly MainViewModel _mainVm;

    public SettingsPage(SettingsViewModel vm, MainViewModel mainVm)
    {
        InitializeComponent();
        _vm = vm;
        _mainVm = mainVm;
        BindingContext = vm;
        Loaded += async (_, _) => await _vm.LoadAsync();
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        if (await _vm.SaveAsync())
        {
            // Force a fresh ApsServices on next translate so updated creds take effect.
            _mainVm.InvalidateApsServices();
            await Navigation.PopModalAsync();
        }
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
        => await Navigation.PopModalAsync();
}
