using HelloWorld_IOS.ViewModels;

namespace HelloWorld_IOS.Views;

public partial class DiagnosticsPage : ContentPage
{
    private readonly DiagnosticsViewModel _vm;

    public DiagnosticsPage(DiagnosticsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
        Loaded += (_, _) => _vm.Refresh();
    }

    private async void OnCloseClicked(object? sender, EventArgs e)
        => await Navigation.PopModalAsync();

    private void OnRefreshClicked(object? sender, EventArgs e) => _vm.Refresh();

    private async void OnShareClicked(object? sender, EventArgs e) => await _vm.ShareSelectedAsync();

    private async void OnExportAllClicked(object? sender, EventArgs e) => await _vm.ShareAllAsZipAsync();

    private void OnDeleteClicked(object? sender, EventArgs e) => _vm.DeleteSelected();

    private void OnClearAllClicked(object? sender, EventArgs e) => _vm.DeleteAll();
}
